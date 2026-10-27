using BookSearch.App;
using BookSearch.App.Ask;
using BookSearch.App.History;
using BookSearch.Core;
using BookSearch.Core.Chunking;
using BookSearch.Core.Data;
using BookSearch.Core.Search;
using BookSearch.Core.Summarize;
using BookSearch.Models;

namespace BookSearch.Tests;

/// <summary>Fakes and builders shared by the summary, history and Ask tests.</summary>
internal static class SummaryKit
{
    public sealed class Strings : IStrings
    {
        public string Get(string key) => key;
    }

    public sealed class Settings(bool auto = true) : ISettingsStore
    {
        public AppSettings Current { get; set; } = new() { AutoSummary = auto };
        public AppSettings Load() => Current;
        public void Save(AppSettings settings) => Current = settings;
    }

    public sealed class Dialogs(bool answer = true) : IDialogs
    {
        public int ClearAsked { get; private set; }
        public Task<bool> ConfirmRemoveAsync(string bookTitle) => Task.FromResult(answer);
        public Task<bool> ConfirmClearHistoryAsync() { ClearAsked++; return Task.FromResult(answer); }
        public List<int> ThreadDeletesAsked { get; } = [];
        public Task<bool> ConfirmDeleteThreadAsync(int questions) { ThreadDeletesAsked.Add(questions); return Task.FromResult(answer); }
        public DeleteAllChoice DeleteAllAnswer { get; set; } = DeleteAllChoice.Cancel;
        public SearchFilter? FilterAnswer { get; set; }
        public Task<SearchFilter?> ChooseFilterAsync(FilterOptions options, SearchFilter current) => Task.FromResult(FilterAnswer);
        public Task<IReadOnlyList<string>?> EditTagsAsync(string bookTitle, IReadOnlyList<string> current, IReadOnlyList<string> allTags) => Task.FromResult<IReadOnlyList<string>?>(null);
        public Task<IReadOnlyList<string>?> AddTagsAsync(int books, IReadOnlyList<string> allTags) => Task.FromResult<IReadOnlyList<string>?>(null);
        public Task<DeleteAllChoice> ConfirmDeleteAllAsync() => Task.FromResult(DeleteAllAnswer);
    }

    public sealed class FakeSummarizer : ISummarizer
    {
        public Func<string, SearchResult, Action<SummaryPhase>, Action<string>, CancellationToken, Task<SummaryRun>> OnRun { get; set; } =
            (_, _, _, _, _) => Task.FromResult(Run("A fact.", passages: 1));
        public List<string> Questions { get; } = [];

        /// <summary>This many results are reported finished (as "Written 1." and so on) before <see cref="OnRun"/> starts.</summary>
        public int FinishFirst;

        public int Prewarms;
        public int Running;
        public int MaxRunning;

        public Task PrewarmAsync(CancellationToken ct) { Interlocked.Increment(ref Prewarms); return Task.CompletedTask; }

        /// <summary>Runs <see cref="OnRun"/>; the passages it returns are then reported as started and finished, in order, as the real one does.</summary>
        public async Task<SummaryRun> SummarizeAsync(string question, SearchResult search, Action<SummaryPhase> onPhase,
            Action<int> onPassageStarted, Action<int, string> onToken, Action<PassageSummary> onPassage, CancellationToken ct)
        {
            lock (Questions) Questions.Add(question);
            var now = Interlocked.Increment(ref Running);
            lock (Questions) MaxRunning = Math.Max(MaxRunning, now);
            try
            {
                var already = Run("", FinishFirst).Passages;
                foreach (var p in already)
                {
                    onPassageStarted(p.Number - 1);
                    onPassage(p with { Text = $"Written {p.Number}." });
                }
                if (search.Hits.Count > FinishFirst) onPassageStarted(FinishFirst);        // the next result is being read while OnRun streams
                var run = await OnRun(question, search, onPhase, token => onToken(FinishFirst, token), ct);
                foreach (var passage in run.Passages)
                {
                    onPassageStarted(passage.Number - 1);
                    onPassage(passage);
                }
                return run;
            }
            finally { Interlocked.Decrement(ref Running); }
        }
    }

    /// <summary>Translates a question with <see cref="OnTranslate"/> and records what it was asked.</summary>
    public sealed class FakeTranslator : IQuestionTranslator
    {
        /// <summary>The answer (one translation per language); throw <see cref="TranslationUnavailableException"/> for a model failure.</summary>
        public Func<string, IReadOnlyList<string>, IReadOnlyList<string>> OnTranslate { get; set; } =
            (q, languages) => languages.Select(l => $"{q} [{l}]").ToList();
        public List<(string Question, IReadOnlyList<string> Languages)> Calls { get; } = [];

        public Task<IReadOnlyList<string>> TranslateAsync(string question, IReadOnlyList<string> languages, CancellationToken ct)
        {
            lock (Calls) Calls.Add((question, languages));
            return Task.FromResult(OnTranslate(question, languages));
        }
    }

    /// <summary>Rewrites a follow-up with <see cref="OnRewrite"/> and records what it was asked.</summary>
    public sealed class FakeRewriter : IFollowUpRewriter
    {
        /// <summary>The answer; null means "stands on its own already". Throw <see cref="RewriteUnavailableException"/> for a model failure.</summary>
        public Func<string, IReadOnlyList<string>, string?> OnRewrite { get; set; } = (q, earlier) => $"{earlier[^1]} -> {q}";
        public List<(string Question, IReadOnlyList<string> Earlier)> Calls { get; } = [];

        public Task<string?> RewriteAsync(string question, IReadOnlyList<string> earlier, CancellationToken ct)
        {
            lock (Calls) Calls.Add((question, earlier));
            return Task.FromResult(OnRewrite(question, earlier));
        }
    }

    public sealed class FakeAsk : IAskService
    {
        public Func<string, SearchResult> ResultFor { get; set; } = _ => Search(6);
        public bool Ready { get; set; } = true;
        public LibrarySummary Library { get; set; } = new(3, 0);
        public Func<CancellationToken, Task> OnWarmUp { get; set; } = _ => Task.CompletedTask;
        public Func<string, bool, CancellationToken, Task<SearchResult>>? OnSearch { get; set; }
        public List<(string Question, bool Embedding)> Searches { get; } = [];

        /// <summary>The translations each search was given (DCR-11), in the order of <see cref="Searches"/>.</summary>
        public List<IReadOnlyList<string>?> AlsoSearches { get; } = [];

        /// <summary>The languages of the books in scope, most books first.</summary>
        public IReadOnlyList<string?> BookLanguages { get; set; } = [];

        public bool EmbeddingReady => Ready;
        public Func<Task>? OnLibrary { get; set; }
        public async Task<LibrarySummary> GetLibrarySummaryAsync()
        {
            if (OnLibrary is not null) await OnLibrary();
            return Library;
        }
        public FilterOptions Options { get; set; } = new([], []);
        public List<SearchFilter?> Filters { get; } = [];
        public Task<FilterOptions> GetFilterOptionsAsync() => Task.FromResult(Options);
        public Task<SearchResult> SearchAsync(string question, bool useEmbedding, CancellationToken ct, SearchFilter? filter = null, IReadOnlyList<string>? alsoSearch = null)
        {
            lock (Searches) { Searches.Add((question, useEmbedding)); Filters.Add(filter); AlsoSearches.Add(alsoSearch); }
            return OnSearch is not null ? OnSearch(question, useEmbedding, ct) : Task.FromResult(ResultFor(question));
        }
        public Task<IReadOnlyList<string?>> GetBookLanguagesAsync(SearchFilter filter) => Task.FromResult(BookLanguages);
        public Task WarmUpEmbeddingAsync(CancellationToken ct) => OnWarmUp(ct);
    }

    /// <summary>The history in memory, so the coordinator can be tested without a database.</summary>
    public sealed class InMemoryHistory : ISearchHistoryStore
    {
        private readonly object _gate = new();
        private readonly List<SearchHistoryEntry> _entries = [];
        private long _next = 1;

        private readonly List<long> _threads = [];
        private long _nextThread = 1;

        public long Add(string question, string status, DateTime? at = null, long? threadId = null, string? filter = null, string? searchedAs = null,
            IReadOnlyList<string>? alsoSearched = null)
        {
            lock (_gate)
            {
                _entries.Add(new SearchHistoryEntry(_next, question, at ?? DateTime.UtcNow, status, null, null, null, threadId, filter, searchedAs, alsoSearched));
                return _next++;
            }
        }

        public long AddThread() { lock (_gate) { _threads.Add(_nextThread); return _nextThread++; } }

        public Task<long> AddThreadAsync() => Task.FromResult(AddThread());

        public Task<long> AddAsync(string question, string status, long? threadId = null, string? filter = null, string? searchedAs = null,
            IReadOnlyList<string>? alsoSearched = null) =>
            Task.FromResult(Add(question, status, null, threadId, filter, searchedAs, alsoSearched));

        public Task<bool> ThreadExistsAsync(long threadId) { lock (_gate) return Task.FromResult(_threads.Contains(threadId)); }

        public Task<IReadOnlyList<SearchThread>> ListThreadsAsync()
        {
            lock (_gate)
                return Task.FromResult<IReadOnlyList<SearchThread>>(_threads
                    .Select(t => (Id: t, Entries: _entries.Where(e => e.ThreadId == t).OrderBy(e => e.Id).ToList()))
                    .Where(t => t.Entries.Count > 0)
                    .Select(t => new SearchThread(t.Id, t.Entries[0].CreatedAt, t.Entries[^1].CreatedAt, t.Entries[0].Question, t.Entries.Count))
                    .OrderByDescending(t => t.UpdatedAt).ThenByDescending(t => t.Id).ToList());
        }

        public Task<IReadOnlyList<SearchHistoryEntry>> ListThreadAsync(long threadId)
        {
            lock (_gate) return Task.FromResult<IReadOnlyList<SearchHistoryEntry>>(_entries.Where(e => e.ThreadId == threadId).OrderBy(e => e.Id).ToList());
        }

        public Task DeleteThreadAsync(long threadId)
        {
            lock (_gate) { _entries.RemoveAll(e => e.ThreadId == threadId); _threads.Remove(threadId); }
            return Task.CompletedTask;
        }

        /// <summary>Runs before an update is applied; lets a test close an entry while an update is in progress.</summary>
        public Func<Task>? BeforeUpdate { get; set; }

        public async Task UpdateAsync(long id, string status, string? summaryText = null, string? model = null, int? ms = null)
        {
            if (BeforeUpdate is not null) await BeforeUpdate();
            lock (_gate)
            {
                var i = _entries.FindIndex(e => e.Id == id);
                if (i >= 0)
                    _entries[i] = _entries[i] with { Status = status, SummaryText = summaryText ?? _entries[i].SummaryText, SummaryMs = ms ?? _entries[i].SummaryMs };
            }
        }

        public Task<IReadOnlyList<SearchHistoryEntry>> ListAsync()
        {
            lock (_gate) return Task.FromResult<IReadOnlyList<SearchHistoryEntry>>(_entries.OrderByDescending(e => e.Id).ToList());
        }

        /// <summary>Runs after the list was read and before it is returned: the list is then stale if the history changes meanwhile.</summary>
        public Func<Task>? AfterListUnfinished { get; set; }

        public async Task<IReadOnlyList<SearchHistoryEntry>> ListUnfinishedAsync()
        {
            IReadOnlyList<SearchHistoryEntry> list;
            lock (_gate)
                list = _entries.Where(e => SummaryStatus.IsUnfinished(e.Status)).OrderBy(e => e.Id).ToList();
            if (AfterListUnfinished is not null) await AfterListUnfinished();
            return list;
        }

        /// <summary>When set, deleting an entry fails with it (a database error); nothing is removed.</summary>
        public Exception? DeleteFails { get; set; }

        public Task DeleteAsync(long id)
        {
            if (DeleteFails is not null) return Task.FromException(DeleteFails);
            lock (_gate) _entries.RemoveAll(e => e.Id == id);
            return Task.CompletedTask;
        }
        public Task ClearAsync() { lock (_gate) { _entries.Clear(); _threads.Clear(); } return Task.CompletedTask; }

        public SearchHistoryEntry? Get(long id) { lock (_gate) return _entries.FirstOrDefault(e => e.Id == id); }
        public List<SearchHistoryEntry> All() { lock (_gate) return _entries.ToList(); }
    }

    public static SearchHit Hit(long id) =>
        new(id, 1, $"Book {id}", "Author", "Chapter", null, null, null, $"Passage {id} text.", [], $"Passage {id} text.", 0.03, 0.6, 1, 1, null);

    public static SearchResult Search(int hits, bool notCovered = false) =>
        new(Enumerable.Range(1, hits).Select(i => Hit(i)).ToList(), notCovered, false, null, 0.6, false,
            new QueryTerms(["passage"], []), new SearchTimings(1, 1, 1, 1, 1, 5));

    /// <summary>A finished run: every result gets <paramref name="reply"/> as its summary, or is "not covered".</summary>
    public static SummaryRun Run(string reply, int passages = 3, bool notCovered = false)
    {
        var done = Enumerable.Range(1, passages)
            .Select(i => new PassageSummary(i, Hit(i), $"Book {i}", notCovered ? "" : reply, notCovered, "t",
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), 100)).ToList();
        return new SummaryRun(done, false, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
    }

    public static List<ResultRow> Rows(int count) =>
        Enumerable.Range(1, count).Select(i => new ResultRow(Hit(i), new QueryTerms(["passage"], []), new Strings())).ToList();

    /// <summary>A coordinator whose UI posts run at once (no UI thread in tests).</summary>
    public static SummaryCoordinator Coordinator(ISummarizer summarizer, ISearchHistoryStore history, IAskService? ask = null,
        Func<bool>? auto = null, Action<Action>? post = null) =>
        new(history, summarizer, ask ?? new FakeAsk(), auto ?? (() => true), post ?? (a => a()));

    public static async Task Eventually(Func<bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not become true in time.");
            await Task.Delay(10);
        }
    }
}

public class SummaryTests
{
    private static SearchResult Search(int hits, bool notCovered = false) => SummaryKit.Search(hits, notCovered);

    // ---- the real Summarizer -----------------------------------------------------------------------------------------

    private sealed class FakeChat(params string[] tokens) : IChatModel
    {
        public int Calls { get; private set; }
        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Calls++;
            foreach (var t in tokens)
            {
                ct.ThrowIfCancellationRequested();
                yield return t;
                await Task.Yield();
            }
        }
    }

    private sealed class FakeHandle : IServerHandle
    {
        public Uri BaseUri { get; } = new("http://127.0.0.1:9300/");
        public string ApiKey => "k";
        public bool HasExited => false;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeLauncher : IServerLauncher
    {
        public List<ServerRole> Starts { get; } = [];
        public Task<IServerHandle> StartAsync(ServerRole role, CancellationToken ct)
        {
            Starts.Add(role);
            return Task.FromResult<IServerHandle>(new FakeHandle());
        }
    }

    [Fact]
    public async Task Summarizer_LoadsModelThenReportsPhasesAndValidatesCitations()
    {
        var launcher = new FakeLauncher();
        await using var supervisor = new ServerSupervisor(launcher);
        var chat = new FakeChat("Alice fell. ", "She was curious.");
        var summarizer = new Summarizer(supervisor, chat, new PunctuationSentenceSplitter());
        var phases = new List<SummaryPhase>();
        var tokens = new List<(int, string)>();
        var done = new List<int>();

        var run = await summarizer.SummarizeAsync("what happened", Search(2), phases.Add, _ => { }, (i, t) => tokens.Add((i, t)),
            p => done.Add(p.Number), CancellationToken.None);

        Assert.Equal([ServerRole.Chat], launcher.Starts);                                   // only the chat server
        // Loading once, then for each result: the model reads it, then writes.
        Assert.Equal([SummaryPhase.LoadingModel, SummaryPhase.ReadingPassages, SummaryPhase.Writing,
                      SummaryPhase.ReadingPassages, SummaryPhase.Writing], phases);
        Assert.Equal(2, run.Passages.Count);                                                // one call and one summary per result
        Assert.Equal(2, chat.Calls);
        Assert.Equal([1, 2], done);
        Assert.Equal([0, 0, 1, 1], tokens.Select(t => t.Item1));

        // The server is already running now, so the second summary skips the loading phase.
        phases.Clear();
        await summarizer.SummarizeAsync("again", Search(1), phases.Add, _ => { }, (_, _) => { }, _ => { }, CancellationToken.None);
        Assert.Equal([SummaryPhase.ReadingPassages, SummaryPhase.Writing], phases);
        Assert.Single(launcher.Starts);
    }

    [Fact]
    public async Task Summarizer_NotCoveredSearch_NeverLoadsTheModel()
    {
        var launcher = new FakeLauncher();
        await using var supervisor = new ServerSupervisor(launcher);
        var chat = new FakeChat("should not be used");
        var summarizer = new Summarizer(supervisor, chat, new PunctuationSentenceSplitter());

        var run = await summarizer.SummarizeAsync("q", Search(2, notCovered: true), _ => { }, _ => { }, (_, _) => { }, _ => { }, CancellationToken.None);

        Assert.True(run.Refused);
        Assert.Empty(launcher.Starts);
        Assert.Equal(0, chat.Calls);
    }

    [Fact]
    public async Task Summarizer_Cancelled_StopsAndThrows()
    {
        await using var supervisor = new ServerSupervisor(new FakeLauncher());
        using var cts = new CancellationTokenSource();
        var chat = new FakeChat("one ", "two ", "three ", "four ");
        var summarizer = new Summarizer(supervisor, chat, new PunctuationSentenceSplitter());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            summarizer.SummarizeAsync("q", Search(3), _ => { }, _ => { }, (_, t) => { if (t == "two ") cts.Cancel(); }, _ => { }, cts.Token));
    }

    [Fact]
    public async Task Summarizer_Prewarm_LoadsOnlyTheChatModel_AndIsHarmlessWhenItCannotStart()
    {
        var launcher = new FakeLauncher();
        await using var supervisor = new ServerSupervisor(launcher);
        var summarizer = new Summarizer(supervisor, new FakeChat("x"), new PunctuationSentenceSplitter());

        await summarizer.PrewarmAsync(CancellationToken.None);
        await summarizer.PrewarmAsync(CancellationToken.None);               // asking again just keeps it in use

        Assert.Equal([ServerRole.Chat], launcher.Starts);                    // started once, and not the embedding server
        Assert.Equal(ServerState.Ready, supervisor.GetState(ServerRole.Chat));

        await using var broken = new ServerSupervisor(new FailingLauncher());
        await new Summarizer(broken, new FakeChat("x"), new PunctuationSentenceSplitter()).PrewarmAsync(CancellationToken.None);   // no exception
    }

    private sealed class FailingLauncher : IServerLauncher
    {
        public Task<IServerHandle> StartAsync(ServerRole role, CancellationToken ct) =>
            throw new ModelUnavailableException("model file missing");
    }

    [Fact]
    public void Summary_Defaults_AreSmallForACpu()
    {
        var options = new SummaryOptions();

        Assert.Equal(350, options.TokensPerPassage);                         // was 200 until the context change (ST-26)
        Assert.Equal(100, options.NeighborTokens);                           // text before and after the result
        Assert.Equal(150, options.Chat.MaxTokens);                           // 2 to 3 sentences for each result (ST-34); was 450 for a whole book
        Assert.Contains("2 to 3 sentences", SummaryPrompt.SystemText);
    }
}
