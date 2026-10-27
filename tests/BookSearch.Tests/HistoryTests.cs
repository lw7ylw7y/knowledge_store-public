using System.Collections.Concurrent;
using BookSearch.App;
using BookSearch.App.Ask;
using BookSearch.App.History;
using BookSearch.Core;
using BookSearch.Core.Data;
using BookSearch.Core.Search;
using BookSearch.Core.Summarize;
using static BookSearch.Tests.SummaryKit;

namespace BookSearch.Tests;

public class HistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "history-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    // ---- the stored history -----------------------------------------------------------------------------------

    [Fact]
    public async Task History_Search_RecordsQuestionTimeAndSummary()
    {
        using var store = new SearchHistoryStore(new AppPaths(_dir));
        var before = DateTime.UtcNow.AddSeconds(-1);

        var first = await store.AddAsync("What did the Rabbit say?", SummaryStatus.Waiting);
        var second = await store.AddAsync("Who is Darcy?", SummaryStatus.None);
        await store.UpdateAsync(first, SummaryStatus.Ready, "The Rabbit was late.", model: "qwen", ms: 4200);

        var list = await store.ListAsync();
        Assert.Equal([second, first], list.Select(e => e.Id));                   // newest first
        var entry = list[1];
        Assert.Equal("What did the Rabbit say?", entry.Question);
        Assert.Equal(SummaryStatus.Ready, entry.Status);
        Assert.Equal("The Rabbit was late.", entry.SummaryText);
        Assert.Equal(4200, entry.SummaryMs);
        Assert.InRange(entry.CreatedAt, before, DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(SummaryStatus.None, list[0].Status);
        Assert.Null(list[0].SummaryText);
    }

    [Fact]
    public async Task History_SurvivesRestart()
    {
        var paths = new AppPaths(_dir);
        using (var store = new SearchHistoryStore(paths))
        {
            var id = await store.AddAsync("A question", SummaryStatus.Waiting);
            await store.UpdateAsync(id, SummaryStatus.Ready, "An answer.");
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using var reopened = new SearchHistoryStore(paths);          // "the next session"

        var entry = Assert.Single(await reopened.ListAsync());
        Assert.Equal("A question", entry.Question);
        Assert.Equal("An answer.", entry.SummaryText);
    }

    [Fact]
    public async Task History_CloseEntry_DeletesOnlyThatEntry_AndClearAllRemovesEverything()
    {
        using var store = new SearchHistoryStore(new AppPaths(_dir));
        var a = await store.AddAsync("one", SummaryStatus.Ready);
        var b = await store.AddAsync("two", SummaryStatus.Ready);
        var c = await store.AddAsync("three", SummaryStatus.Ready);

        await store.DeleteAsync(b);
        Assert.Equal([c, a], (await store.ListAsync()).Select(e => e.Id));

        await store.UpdateAsync(b, SummaryStatus.Failed);            // updating a closed entry does nothing, and doesn't throw
        Assert.Equal(2, (await store.ListAsync()).Count);

        await store.ClearAsync();
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task History_RemoveBook_LeavesEntriesUntouched()
    {
        var paths = new AppPaths(_dir);
        using var store = new SearchHistoryStore(paths);
        var id = await store.AddAsync("About the removed book", SummaryStatus.Ready);
        await store.UpdateAsync(id, SummaryStatus.Ready, "A summary written from it.");

        using var conn = Database.Open(paths.DatabasePath);
        var books = new BookRepository(conn);
        var book = books.Insert(new Book { FilePath = "/books/x.epub", FileHash = "h", Title = "X", Format = "epub", Status = BookStatus.Ready });
        books.Delete(book);                                          // the same purge the Library's Remove button runs

        var entry = Assert.Single(await store.ListAsync());
        Assert.Equal("A summary written from it.", entry.SummaryText);
    }

    [Fact]
    public async Task History_UnfinishedEntries_AreListedOldestFirst()
    {
        using var store = new SearchHistoryStore(new AppPaths(_dir));
        var a = await store.AddAsync("a", SummaryStatus.Writing);
        await store.AddAsync("b", SummaryStatus.Ready);
        var c = await store.AddAsync("c", SummaryStatus.Waiting);

        Assert.Equal([a, c], (await store.ListUnfinishedAsync()).Select(e => e.Id));
    }

    // ---- the queue ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Coordinator_FinalCoveredSearch_QueuesSummaryInBackground()
    {
        var history = new InMemoryHistory();
        var summarizer = new FakeSummarizer();
        using var coordinator = Coordinator(summarizer, history);

        var job = await coordinator.RecordSearchAsync("why did she fall", Search(5));
        await coordinator.WhenIdleAsync();

        Assert.Equal(SummaryStatus.Ready, job.Status);
        Assert.NotNull(job.Run);
        var entry = history.Get(job.EntryId)!;
        Assert.Equal("why did she fall", entry.Question);
        Assert.Equal(SummaryStatus.Ready, entry.Status);
        Assert.Equal(["why did she fall"], summarizer.Questions);
    }

    [Fact]
    public async Task History_SavedSummary_IsOneParagraphPerResult_HeadedWithItsBook()
    {
        var history = new InMemoryHistory();
        var summarizer = new FakeSummarizer { OnRun = (_, _, _, _, _) => Task.FromResult(Run("Alice fell down the hole.", passages: 2)) };
        using var coordinator = Coordinator(summarizer, history);

        var job = await coordinator.RecordSearchAsync("q", Search(5));
        await coordinator.WhenIdleAsync();

        var text = history.Get(job.EntryId)!.SummaryText!;
        Assert.Equal("Book 1: Alice fell down the hole.\n\nBook 2: Alice fell down the hole.", text);
    }

    [Fact]
    public async Task Coordinator_NotCovered_NoSummaryAndStatusSaysSo()
    {
        var history = new InMemoryHistory();
        var summarizer = new FakeSummarizer();
        using var coordinator = Coordinator(summarizer, history);

        var job = await coordinator.RecordSearchAsync("unrelated", Search(3, notCovered: true));
        await coordinator.WhenIdleAsync();

        Assert.Equal(SummaryStatus.NotCovered, job.Status);
        Assert.Equal(SummaryStatus.NotCovered, history.Get(job.EntryId)!.Status);
        Assert.Empty(summarizer.Questions);
    }

    [Fact]
    public async Task Coordinator_AutoOff_RecordsTheSearchAndWritesNothing()
    {
        var history = new InMemoryHistory();
        var summarizer = new FakeSummarizer();
        using var coordinator = Coordinator(summarizer, history, auto: () => false);

        var job = await coordinator.RecordSearchAsync("q", Search(4));
        await coordinator.WhenIdleAsync();
        Assert.Equal(SummaryStatus.None, job.Status);                 // recorded, nothing written
        Assert.Empty(summarizer.Questions);
        Assert.Equal(SummaryStatus.None, history.Get(job.EntryId)!.Status);
    }

    [Fact]
    public async Task Coordinator_ResultsNeverWaitForTheModel()
    {
        var gate = new TaskCompletionSource();
        var summarizer = new FakeSummarizer { OnRun = async (_, _, _, _, _) => { await gate.Task; return Run("Late. [1]"); } };
        using var coordinator = Coordinator(summarizer, new InMemoryHistory());

        var recording = coordinator.RecordSearchAsync("q", Search(4));
        var job = await recording.WaitAsync(TimeSpan.FromSeconds(5));   // returns while the summarizer is still blocked

        Assert.NotEqual(SummaryStatus.Ready, job.Status);
        gate.SetResult();
        await coordinator.WhenIdleAsync();
        Assert.Equal(SummaryStatus.Ready, job.Status);
    }

    [Fact]
    public async Task Coordinator_NewSearch_DoesNotStopRunningSummary_QueuesSecond()
    {
        var history = new InMemoryHistory();
        var gate = new TaskCompletionSource();
        var firstToken = new TaskCompletionSource<CancellationToken>();
        var summarizer = new FakeSummarizer
        {
            OnRun = async (q, _, _, _, ct) =>
            {
                if (q == "first") { firstToken.SetResult(ct); await gate.Task; }
                return Run($"Answer to {q}.", passages: 1);
            },
        };
        using var coordinator = Coordinator(summarizer, history);

        var first = await coordinator.RecordSearchAsync("first", Search(4));
        var token = await firstToken.Task;                            // the first summary is being written
        var second = await coordinator.RecordSearchAsync("second", Search(4));

        Assert.False(token.IsCancellationRequested);                  // the new search did not stop it
        Assert.Equal(SummaryStatus.Writing, first.Status);
        Assert.Equal(SummaryStatus.Waiting, second.Status);           // the second waits its turn

        gate.SetResult();
        await coordinator.WhenIdleAsync();

        Assert.Equal(SummaryStatus.Ready, first.Status);
        Assert.Equal(SummaryStatus.Ready, second.Status);
        Assert.Equal(["first", "second"], summarizer.Questions);
        Assert.Equal("Book 1: Answer to second.", history.Get(second.EntryId)!.SummaryText);
    }

    [Fact]
    public async Task Coordinator_Queue_RunsOneAtATimeInSearchOrder()
    {
        var summarizer = new FakeSummarizer
        {
            OnRun = async (q, _, _, _, _) => { await Task.Delay(30); return Run($"{q}. [1]"); },
        };
        using var coordinator = Coordinator(summarizer, new InMemoryHistory());

        foreach (var q in new[] { "one", "two", "three", "four" }) await coordinator.RecordSearchAsync(q, Search(4));
        await coordinator.WhenIdleAsync();

        Assert.Equal(["one", "two", "three", "four"], summarizer.Questions);
        Assert.Equal(1, summarizer.MaxRunning);
    }

    [Fact]
    public async Task Coordinator_Stop_CancelsRunningSummary_AndRemovesWaitingOne()
    {
        var history = new InMemoryHistory();
        var started = new TaskCompletionSource();
        var summarizer = new FakeSummarizer
        {
            OnRun = async (_, _, _, onToken, ct) =>
            {
                onToken("Half a summ");
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return Run("unreachable");
            },
        };
        using var coordinator = Coordinator(summarizer, history);
        var running = await coordinator.RecordSearchAsync("running", Search(4));
        await started.Task;
        var waiting = await coordinator.RecordSearchAsync("waiting", Search(4));

        await coordinator.StopAsync(waiting);                         // taken out of the queue
        Assert.Equal(SummaryStatus.Stopped, waiting.Status);
        Assert.Equal(SummaryStatus.Stopped, history.Get(waiting.EntryId)!.Status);

        await coordinator.StopAsync(running);                         // cancelled while being written
        await coordinator.WhenIdleAsync();

        Assert.Equal(SummaryStatus.Stopped, running.Status);
        Assert.Equal(SummaryStatus.Stopped, history.Get(running.EntryId)!.Status);
        Assert.All(running.Passages, p => Assert.Equal(PassageStatus.Unfinished, p.Status));   // no half-written summary is left looking complete
        Assert.All(running.Passages, p => Assert.Equal("", p.Text));
        Assert.Equal(["running"], summarizer.Questions);              // the waiting one was never written
    }

    [Fact]
    public async Task Coordinator_Failures_AreSavedWithTheirReason()
    {
        var history = new InMemoryHistory();
        var summarizer = new FakeSummarizer
        {
            OnRun = (q, _, _, _, _) => q == "no model"
                ? throw new ModelUnavailableException("llama-server missing")
                : throw new InvalidOperationException("boom"),
        };
        using var coordinator = Coordinator(summarizer, history);

        var unavailable = await coordinator.RecordSearchAsync("no model", Search(4));
        var broken = await coordinator.RecordSearchAsync("broken", Search(4));
        await coordinator.WhenIdleAsync();

        Assert.Equal(SummaryStatus.Failed, unavailable.Status);
        Assert.True(unavailable.ModelUnavailable);
        Assert.Equal(SummaryStatus.Failed, broken.Status);
        Assert.Equal("boom", broken.Failure);
        Assert.Equal(SummaryStatus.Failed, history.Get(broken.EntryId)!.Status);
    }

    // ---- loading the model ahead of time ---------------------------------------------------------------------------

    [Fact]
    public async Task Coordinator_Prewarm_LoadsTheModelWhenSummariesAreOnAndThereAreBooks()
    {
        var summarizer = new FakeSummarizer();
        using var coordinator = Coordinator(summarizer, new InMemoryHistory(), new FakeAsk { Library = new LibrarySummary(3, 0) });

        await coordinator.PrewarmAsync();

        Assert.Equal(1, summarizer.Prewarms);
        Assert.Empty(summarizer.Questions);                                  // loading is not summarizing
    }

    [Fact]
    public async Task Coordinator_Prewarm_DoesNothingWithoutBooksOrWhenAutomaticSummariesAreOff()
    {
        var summarizer = new FakeSummarizer();
        using var noBooks = Coordinator(summarizer, new InMemoryHistory(), new FakeAsk { Library = new LibrarySummary(0, 2) });
        using var autoOff = Coordinator(summarizer, new InMemoryHistory(), new FakeAsk(), auto: () => false);

        await noBooks.PrewarmAsync();                                        // nothing to search yet
        await autoOff.PrewarmAsync();                                        // the user does not want a model started for them

        Assert.Equal(0, summarizer.Prewarms);
    }

    [Fact]
    public async Task Coordinator_Prewarm_FailureIsSwallowed()
    {
        var ask = new FakeAsk { Library = new LibrarySummary(3, 0) };
        using var coordinator = Coordinator(new ThrowingPrewarm(), new InMemoryHistory(), ask);

        await coordinator.PrewarmAsync();                                    // does not throw
    }

    private sealed class ThrowingPrewarm : BookSearch.App.Ask.ISummarizer
    {
        public Task PrewarmAsync(CancellationToken ct) => throw new InvalidOperationException("boom");
        public Task<SummaryRun> SummarizeAsync(string question, SearchResult search, Action<SummaryPhase> onPhase,
            Action<int> onPassageStarted, Action<int, string> onToken, Action<PassageSummary> onPassage, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    // ---- shutdown and restart -----------------------------------------------------------------------------------

    [Fact]
    public async Task Coordinator_Shutdown_LeavesUnfinishedEntriesToBeCompleted()
    {
        var history = new InMemoryHistory();
        var started = new TaskCompletionSource();
        var summarizer = new FakeSummarizer
        {
            OnRun = async (_, _, _, _, ct) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return Run("x"); },
        };
        var coordinator = Coordinator(summarizer, history);
        var running = await coordinator.RecordSearchAsync("being written", Search(4));
        await started.Task;
        var waiting = await coordinator.RecordSearchAsync("waiting", Search(4));

        coordinator.Dispose();                                        // the app closes
        await Task.Delay(200);

        Assert.Equal(SummaryStatus.Writing, history.Get(running.EntryId)!.Status);   // not "stopped": it is completed next time
        Assert.Equal(SummaryStatus.Waiting, history.Get(waiting.EntryId)!.Status);
    }

    [Fact]
    public async Task Coordinator_Restart_CompletesUnfinishedSummaries()
    {
        var history = new InMemoryHistory();
        var writing = history.Add("was being written", SummaryStatus.Writing);
        var done = history.Add("already finished", SummaryStatus.Ready);
        await history.UpdateAsync(done, SummaryStatus.Ready, "Kept as it was.");
        var waiting = history.Add("was waiting", SummaryStatus.Waiting);
        var summarizer = new FakeSummarizer { OnRun = (q, _, _, _, _) => Task.FromResult(Run($"Summary of {q}.", passages: 1)) };
        var ask = new FakeAsk();
        using var coordinator = Coordinator(summarizer, history, ask, auto: () => false);   // even with automatic summaries off

        await coordinator.ResumeUnfinishedAsync();
        await coordinator.WhenIdleAsync();

        Assert.Equal(["was being written", "was waiting"], summarizer.Questions);           // the original order
        Assert.Equal(["was being written", "was waiting"], ask.Searches.Select(s => s.Question).ToArray());   // searched again first
        Assert.Equal(SummaryStatus.Ready, history.Get(writing)!.Status);
        Assert.Equal("Book 1: Summary of was being written.", history.Get(writing)!.SummaryText);
        Assert.Equal(SummaryStatus.Ready, history.Get(waiting)!.Status);
        Assert.Equal("Kept as it was.", history.Get(done)!.SummaryText);                    // the finished one is untouched
    }

    [Fact]
    public async Task Coordinator_Restart_QuestionNoLongerCovered_IsMarkedNotCovered()
    {
        var history = new InMemoryHistory();
        var id = history.Add("gone", SummaryStatus.Waiting);
        var summarizer = new FakeSummarizer();
        var ask = new FakeAsk { ResultFor = _ => Search(3, notCovered: true) };
        using var coordinator = Coordinator(summarizer, history, ask);

        await coordinator.ResumeUnfinishedAsync();
        await coordinator.WhenIdleAsync();

        Assert.Equal(SummaryStatus.NotCovered, history.Get(id)!.Status);
        Assert.Empty(summarizer.Questions);
    }

    [Fact]
    public async Task Coordinator_Restart_SearchFails_MarksFailedAndCarriesOn()
    {
        var history = new InMemoryHistory();
        var bad = history.Add("bad", SummaryStatus.Waiting);
        var good = history.Add("good", SummaryStatus.Waiting);
        var ask = new FakeAsk
        {
            OnSearch = (q, _, _) => q == "bad" ? throw new InvalidOperationException("db busy") : Task.FromResult(Search(4)),
        };
        using var coordinator = Coordinator(new FakeSummarizer(), history, ask);

        await coordinator.ResumeUnfinishedAsync();
        await coordinator.WhenIdleAsync();

        Assert.Equal(SummaryStatus.Failed, history.Get(bad)!.Status);
        Assert.Equal(SummaryStatus.Ready, history.Get(good)!.Status);
    }

    [Fact]
    public async Task Coordinator_NewSearchDuringRestart_GoesBehindTheRestoredEntries()
    {
        var history = new InMemoryHistory();
        history.Add("old", SummaryStatus.Waiting);
        var release = new TaskCompletionSource();
        var ask = new FakeAsk { OnWarmUp = async _ => await release.Task };
        var summarizer = new FakeSummarizer();
        using var coordinator = Coordinator(summarizer, history, ask);

        var resume = coordinator.ResumeUnfinishedAsync();
        var recording = coordinator.RecordSearchAsync("new", Search(4));
        await Task.Delay(100);
        Assert.False(recording.IsCompleted);                          // waits until the old entries are queued
        release.SetResult();
        await recording;
        await coordinator.WhenIdleAsync();

        Assert.Equal(["old", "new"], summarizer.Questions);
        await resume;
    }

    // ---- closing entries and clearing -----------------------------------------------------------------------------

    [Fact]
    public async Task Coordinator_CloseEntry_DeletesIt_AndDropsAWaitingSummary()
    {
        var history = new InMemoryHistory();
        var started = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        var summarizer = new FakeSummarizer { OnRun = async (_, _, _, _, _) => { started.TrySetResult(); await gate.Task; return Run("a. [1]"); } };
        using var coordinator = Coordinator(summarizer, history);
        var first = await coordinator.RecordSearchAsync("first", Search(4));
        await started.Task;
        var second = await coordinator.RecordSearchAsync("second", Search(4));

        await coordinator.CloseEntryAsync(second.EntryId);
        gate.SetResult();
        await coordinator.WhenIdleAsync();

        Assert.Null(history.Get(second.EntryId));
        Assert.NotNull(history.Get(first.EntryId));
        Assert.Equal(["first"], summarizer.Questions);                // the closed one was never written
    }

    [Fact]
    public async Task Coordinator_Clear_DeletesEverythingAndDropsTheQueue()
    {
        var history = new InMemoryHistory();
        var started = new TaskCompletionSource();
        var summarizer = new FakeSummarizer
        {
            OnRun = async (_, _, _, _, ct) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return Run("x"); },
        };
        using var coordinator = Coordinator(summarizer, history);
        await coordinator.RecordSearchAsync("running", Search(4));
        await started.Task;
        await coordinator.RecordSearchAsync("waiting", Search(4));

        await coordinator.ClearAsync();
        await coordinator.WhenIdleAsync();

        Assert.Empty(history.All());
        Assert.Equal(["running"], summarizer.Questions);
    }

    // ---- threads ---------------------------------------------------------------------------------------------------

    private sealed class PumpContext : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = [];
        public int ThreadId { get; private set; }

        public PumpContext()
        {
            var up = new ManualResetEventSlim();
            new Thread(() =>
            {
                ThreadId = Environment.CurrentManagedThreadId;
                up.Set();
                foreach (var action in _queue.GetConsumingEnumerable()) action();
            }) { IsBackground = true }.Start();
            up.Wait();
        }

        public void Post(Action action) => _queue.Add(action);
        public void Dispose() => _queue.CompleteAdding();
    }

    [Fact]
    public async Task Coordinator_ReportsFromBackgroundThreads_AreAppliedOnTheUiThread()
    {
        using var ui = new PumpContext();
        var attached = new TaskCompletionSource();
        var summarizer = new FakeSummarizer
        {
            OnRun = async (_, _, onPhase, onToken, _) =>
            {
                await attached.Task.ConfigureAwait(false);                   // the handler below is in place before anything is reported
                await Task.Delay(10).ConfigureAwait(false);
                onPhase(SummaryPhase.ReadingPassages);
                await Task.Run(() => { onPhase(SummaryPhase.Writing); onToken("Alice "); onToken("fell. "); onToken("[1]"); });
                return Run("Alice fell. [1]", passages: 4);
            },
        };
        using var coordinator = Coordinator(summarizer, new InMemoryHistory(), post: ui.Post);
        var job = await coordinator.RecordSearchAsync("q", Search(4));
        var changedOn = new ConcurrentBag<int>();
        job.PropertyChanged += (_, _) => changedOn.Add(Environment.CurrentManagedThreadId);

        attached.SetResult();
        await coordinator.WhenIdleAsync();

        Assert.NotEmpty(changedOn);
        Assert.All(changedOn, id => Assert.Equal(ui.ThreadId, id));   // every change happened on the "UI" thread
        Assert.Equal(SummaryStatus.Ready, job.Status);
        Assert.All(job.Passages, p => Assert.Equal("Alice fell. [1]", p.Text));
    }
}
