using BookSearch.App.Ask;
using BookSearch.Core;
using BookSearch.Core.Data;
using BookSearch.Core.Search;
using BookSearch.Core.Summarize;

namespace BookSearch.App.History;

/// <summary>
/// Writes summaries in the background and keeps the search history (ST-23, DCR-1). Every search is recorded; if the library
/// covers the question and automatic summaries are on, its summary joins a queue. Summaries are written one at a time in the
/// order the searches were made, and a new search never stops one that is being written. Each outcome is saved to the
/// history, and entries still waiting or being written when the app closes are completed after the next start.
/// </summary>
public sealed class SummaryCoordinator : IDisposable
{
    private readonly ISearchHistoryStore _history;
    private readonly ISummarizer _summarizer;
    private readonly IAskService _search;
    private readonly Func<bool> _autoSummary;
    private readonly Action<Action> _ui;
    private readonly object _gate = new();
    private readonly List<SummaryJob> _queue = [];
    private readonly Dictionary<long, SummaryJob> _live = [];   // jobs created or restored in this session, by entry id
    private SummaryJob? _running;
    private long _generation;                              // bumped by ClearAsync so a restart pass working from an old list stops
    private readonly HashSet<long> _closed = [];            // entries closed while a restart pass may still hold them in its list
    private bool _resuming;
    private Task _loop = Task.CompletedTask;
    private bool _loopRunning;
    private Task _resume = Task.CompletedTask;
    private volatile bool _disposed;

    /// <param name="post">Runs an action on the UI thread, in order. By default it posts to the synchronization context of
    /// the thread that created the coordinator, or runs the action at once if there is none.</param>
    public SummaryCoordinator(ISearchHistoryStore history, ISummarizer summarizer, IAskService search,
        Func<bool> autoSummary, Action<Action>? post = null)
    {
        _history = history;
        _summarizer = summarizer;
        _search = search;
        _autoSummary = autoSummary;
        var context = SynchronizationContext.Current;
        _ui = post ?? (action =>
        {
            if (context is null) action();
            else context.Post(_ => action(), null);
        });
    }

    /// <summary>The history changed (an entry was added, finished, closed or cleared); the History page reloads.</summary>
    public event Action? HistoryChanged;

    // ---- loading the model ahead of time ----------------------------------------------------------------------------

    /// <summary>
    /// Starts the language model in the background (when automatic summaries are on and the library has something to search), so
    /// the first summary doesn't wait for a 2-3 GB model to load. Safe to call often: a running model is simply kept in use,
    /// which also postpones its idle shutdown while the user is on the Ask page.
    /// </summary>
    public async Task PrewarmAsync()
    {
        if (_disposed || !_autoSummary()) return;
        try
        {
            if ((await _search.GetLibrarySummaryAsync().ConfigureAwait(false)).ReadyBooks == 0) return;
            await _summarizer.PrewarmAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception) { /* a warm-up is a courtesy; anything wrong shows up when a summary is asked for */ }
    }

    // ---- recording and queueing -----------------------------------------------------------------------------------

    /// <summary>
    /// Records a finished search. If the library covers it and automatic summaries are on, its summary is queued; otherwise the
    /// entry says why there is none. Returns the job whose results the Ask page fills with summaries.
    /// </summary>
    /// <param name="threadId">The conversation the question joins; a new one is started if this is null or no longer exists.</param>
    /// <param name="searchedAs">The follow-up rewritten to stand on its own, when it was; the summary is written for it, and it is kept so a restart searches it again.</param>
    /// <param name="filter">The search filter the question was asked with; kept so the question can be searched the same way after a restart.</param>
    /// <param name="alsoSearched">The question in the other languages it was also searched in (DCR-11); kept for the same reason.</param>
    public async Task<SummaryJob> RecordSearchAsync(string question, SearchResult result, long? threadId = null,
        string? searchedAs = null, SearchFilter? filter = null, IReadOnlyList<string>? alsoSearched = null)
    {
        await _resume.ConfigureAwait(false);                  // entries left over from last time go first
        var covered = !result.NotCovered && result.Hits.Count > 0;
        var status = !covered ? SummaryStatus.NotCovered : _autoSummary() ? SummaryStatus.Waiting : SummaryStatus.None;
        if (threadId is not { } thread || !await _history.ThreadExistsAsync(thread).ConfigureAwait(false))
            thread = await _history.AddThreadAsync().ConfigureAwait(false);
        var id = await _history.AddAsync(question, status, thread, filter?.ToJson(), searchedAs, alsoSearched).ConfigureAwait(false);
        var job = new SummaryJob(id, question, result, thread, searchedAs, alsoSearched) { Status = status };
        lock (_gate) _live[id] = job;
        HistoryChanged?.Invoke();
        if (status == SummaryStatus.Waiting) Enqueue(job);
        return job;
    }

    /// <summary>Stops the summary: one being written is cancelled, one waiting leaves the queue. Summaries already written stay on screen.</summary>
    public async Task StopAsync(SummaryJob job)
    {
        bool waiting, running;
        lock (_gate)
        {
            waiting = _queue.Remove(job);
            running = ReferenceEquals(_running, job);
        }
        job.StopRequested = true;
        if (waiting)
        {
            await _history.UpdateAsync(job.EntryId, SummaryStatus.Stopped).ConfigureAwait(false);
            await RunOnUiAsync(() => { job.EndUnfinishedPassages(); job.Status = SummaryStatus.Stopped; }).ConfigureAwait(false);
            HistoryChanged?.Invoke();
        }
        else if (running) job.Cts?.Cancel();                  // the queue's worker saves the "stopped" status
    }

    // ---- history actions ------------------------------------------------------------------------------------------

    /// <summary>Closes one history entry. A summary still waiting or being written for it is dropped.</summary>
    public async Task CloseEntryAsync(long entryId)
    {
        SummaryJob? job;
        lock (_gate)
        {
            _live.TryGetValue(entryId, out job);
            if (job is not null) _queue.Remove(job);
            if (_resuming) _closed.Add(entryId);
        }
        if (job is not null)
        {
            job.Discarded = true;
            if (ReferenceEquals(_running, job)) job.Cts?.Cancel();
        }
        await _history.DeleteAsync(entryId).ConfigureAwait(false);
        HistoryChanged?.Invoke();
    }

    /// <summary>Deletes a whole conversation. Summaries still waiting or being written for its questions are dropped.</summary>
    public async Task CloseThreadAsync(long threadId)
    {
        lock (_gate)
        {
            foreach (var job in _live.Values.Where(j => j.ThreadId == threadId))
            {
                job.Discarded = true;
                _queue.Remove(job);
                if (ReferenceEquals(_running, job)) job.Cts?.Cancel();
            }
        }
        // Questions restored after a restart have no live job until their turn comes up: drop those too.
        foreach (var entry in await _history.ListThreadAsync(threadId).ConfigureAwait(false))
            lock (_gate)
            {
                if (_resuming) _closed.Add(entry.Id);
                if (_live.TryGetValue(entry.Id, out var job))
                {
                    job.Discarded = true;
                    _queue.Remove(job);
                    if (ReferenceEquals(_running, job)) job.Cts?.Cancel();
                }
            }
        await _history.DeleteThreadAsync(threadId).ConfigureAwait(false);
        HistoryChanged?.Invoke();
    }

    /// <summary>Deletes the whole history and drops every summary still waiting or being written.</summary>
    public async Task ClearAsync()
    {
        List<SummaryJob> dropped;
        lock (_gate)
        {
            _generation++;
            foreach (var job in _live.Values) job.Discarded = true;
            foreach (var job in _queue) job.Discarded = true;
            dropped = _queue.ToList();
            _queue.Clear();
            if (_running is { } running)
            {
                running.Discarded = true;
                running.Cts?.Cancel();
            }
        }
        // A question still on screen whose summary was waiting must not stay "waiting" for ever.
        if (dropped.Count > 0)
            await RunOnUiAsync(() =>
            {
                foreach (var job in dropped)
                {
                    job.EndUnfinishedPassages();
                    job.Status = SummaryStatus.Stopped;
                }
            }).ConfigureAwait(false);
        await _history.ClearAsync().ConfigureAwait(false);
        HistoryChanged?.Invoke();
    }

    // ---- after a restart --------------------------------------------------------------------------------------------

    /// <summary>
    /// Completes the summaries that were waiting or being written when the app last closed: each question is searched
    /// again against the library as it is now, and the summary is written from those results, oldest first.
    /// </summary>
    public Task ResumeUnfinishedAsync()
    {
        lock (_gate)
        {
            _resuming = true;
            return _resume = Task.Run(ResumeAsync);
        }
    }

    private async Task ResumeAsync()
    {
        try { await ResumePassAsync().ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                _resuming = false;
                _closed.Clear();
            }
        }
    }

    private async Task ResumePassAsync()
    {
        long generation;
        lock (_gate) generation = _generation;               // before the list is read: a Clear during the read makes it stale
        IReadOnlyList<SearchHistoryEntry> entries;
        try { entries = await _history.ListUnfinishedAsync().ConfigureAwait(false); }
        catch (Exception) { return; }
        foreach (var entry in entries)
        {
            if (_disposed) return;
            SummaryJob job;
            lock (_gate)       // the check and the registration are one step, so a close can't fall between them
            {
                if (generation != _generation) return;                  // the history was cleared since the list was read
                if (_live.ContainsKey(entry.Id) || _closed.Contains(entry.Id)) continue;   // handled already, or closed meanwhile
                job = new SummaryJob(entry.Id, entry.Question, null, entry.ThreadId, entry.SearchedAs, entry.AlsoSearched) { Status = SummaryStatus.Waiting };
                _live[entry.Id] = job;
            }
            try
            {
                try { await _search.WarmUpEmbeddingAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (ModelUnavailableException) { /* keyword results are better than none */ }
                var result = await _search.SearchAsync(job.SummaryQuestion, _search.EmbeddingReady, CancellationToken.None,
                    SearchFilter.FromJson(entry.Filter), entry.AlsoSearched).ConfigureAwait(false);
                if (job.Discarded) continue;                            // closed while it was being searched
                if (result.NotCovered || result.Hits.Count == 0)
                {
                    await _history.UpdateAsync(entry.Id, SummaryStatus.NotCovered).ConfigureAwait(false);
                    job.Status = SummaryStatus.NotCovered;
                    HistoryChanged?.Invoke();
                    continue;
                }
                job.Search = result;
                Enqueue(job);
            }
            catch (Exception)
            {
                await _history.UpdateAsync(entry.Id, SummaryStatus.Failed).ConfigureAwait(false);
                HistoryChanged?.Invoke();
            }
        }
    }

    // ---- the queue ------------------------------------------------------------------------------------------------

    private void Enqueue(SummaryJob job)
    {
        lock (_gate)
        {
            if (_disposed || job.Discarded) return;     // closed while its search was still running
            _queue.Add(job);
            if (_loopRunning) return;
            _loopRunning = true;
            _loop = Task.Run(RunQueueAsync);
        }
    }

    private async Task RunQueueAsync()
    {
        while (true)
        {
            SummaryJob job;
            lock (_gate)
            {
                if (_queue.Count == 0 || _disposed)
                {
                    _running = null;
                    _loopRunning = false;
                    return;
                }
                job = _queue[0];
                _queue.RemoveAt(0);
                _running = job;
            }
            if (job.Discarded) continue;                // closed after it was queued
            await RunJobAsync(job).ConfigureAwait(false);
        }
    }

    private async Task RunJobAsync(SummaryJob job)
    {
        using var cts = new CancellationTokenSource();
        job.Cts = cts;
        if (job.StopRequested) cts.Cancel();
        await SaveAsync(job, SummaryStatus.Writing).ConfigureAwait(false);
        await RunOnUiAsync(() =>
        {
            job.Reset();
            job.Status = SummaryStatus.Writing;
        }).ConfigureAwait(false);
        HistoryChanged?.Invoke();

        try
        {
            var run = await _summarizer.SummarizeAsync(job.SummaryQuestion, job.Search!,
                phase => _ui(() => job.Phase = phase),
                index => _ui(() => job.PassageStarted(index)),
                (index, token) => _ui(() => job.PassageToken(index, token)),
                passage => _ui(() => job.PassageDone(passage)),
                cts.Token).ConfigureAwait(false);
            await RunOnUiAsync(() => { }).ConfigureAwait(false);       // everything posted so far has been applied

            if (run.NotCovered) await FinishAsync(job, SummaryStatus.NotCovered).ConfigureAwait(false);
            else if (run.Passages.All(p => !p.NotCovered && string.IsNullOrWhiteSpace(p.Text)))
                await FinishAsync(job, SummaryStatus.Failed, failure: null).ConfigureAwait(false);   // the model wrote nothing at all
            else
            {
                await SaveAsync(job, SummaryStatus.Ready, run.Text, ms: (int)run.Total.TotalMilliseconds).ConfigureAwait(false);
                await RunOnUiAsync(() =>
                {
                    job.Run = run;
                    job.Status = SummaryStatus.Ready;
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await RunOnUiAsync(() => { }).ConfigureAwait(false);
            if (job.Discarded) await RunOnUiAsync(() => { job.EndUnfinishedPassages(); job.Status = SummaryStatus.Stopped; }).ConfigureAwait(false);
            else if (_disposed && !job.StopRequested) { /* the app is closing: it stays "writing" and is completed next time */ }
            else await FinishAsync(job, SummaryStatus.Stopped).ConfigureAwait(false);
        }
        catch (ModelUnavailableException)
        {
            await RunOnUiAsync(() => { }).ConfigureAwait(false);
            await FinishAsync(job, SummaryStatus.Failed, modelUnavailable: true).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            await RunOnUiAsync(() => { }).ConfigureAwait(false);
            await FinishAsync(job, SummaryStatus.Failed, failure: e.Message).ConfigureAwait(false);
        }
        finally
        {
            job.Cts = null;
            HistoryChanged?.Invoke();
        }
    }

    private async Task FinishAsync(SummaryJob job, string status, bool modelUnavailable = false, string? failure = null)
    {
        // A stopped or failed run keeps the summaries of the results that were finished, on screen and in the history.
        string? written = null;
        if (status is SummaryStatus.Stopped or SummaryStatus.Failed)
            await RunOnUiAsync(() => written = job.WrittenText()).ConfigureAwait(false);
        await SaveAsync(job, status, written).ConfigureAwait(false);
        await RunOnUiAsync(() =>
        {
            job.ModelUnavailable = modelUnavailable;
            job.Failure = failure;
            job.EndUnfinishedPassages();
            job.Status = status;
        }).ConfigureAwait(false);
    }

    /// <summary>Saves to the history unless the entry was closed or the history cleared meanwhile.</summary>
    private async Task SaveAsync(SummaryJob job, string status, string? text = null, int? ms = null)
    {
        if (job.Discarded) return;
        await _history.UpdateAsync(job.EntryId, status, text, model: null, ms).ConfigureAwait(false);
    }

    private Task RunOnUiAsync(Action action)
    {
        var done = new TaskCompletionSource();
        _ui(() =>
        {
            try { action(); done.TrySetResult(); }
            catch (Exception e) { done.TrySetException(e); }
        });
        return done.Task;
    }

    /// <summary>Completes when nothing is waiting or being written (and any restart work has been queued). For tests and shutdown.</summary>
    public async Task WhenIdleAsync()
    {
        await _resume.ConfigureAwait(false);
        while (true)
        {
            Task loop;
            lock (_gate)
            {
                if (!_loopRunning && _queue.Count == 0) return;
                loop = _loop;
            }
            await loop.ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        SummaryJob? running;
        lock (_gate)
        {
            _disposed = true;
            _queue.Clear();               // they stay "waiting" in the history and are completed after the next start
            running = _running;
        }
        running?.Cts?.Cancel();           // and so does the one being written (it stays "writing")
    }
}
