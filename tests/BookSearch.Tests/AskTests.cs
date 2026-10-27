using BookSearch.App;
using BookSearch.App.Ask;
using BookSearch.Core;
using BookSearch.Core.Chunking;
using BookSearch.Core.Data;
using BookSearch.Core.Ingest;
using BookSearch.Core.Search;
using BookSearch.Core.Summarize;
using BookSearch.Core.Worker;
using BookSearch.Models;
using BookSearch.Models.Management;

namespace BookSearch.Tests;

public class AskTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ask-" + Guid.NewGuid().ToString("N"));

    private sealed class Strings : IStrings
    {
        public string Get(string key) => key;
    }

    private sealed class FakeAsk : IAskService
    {
        public LibrarySummary Summary { get; set; } = new(3, 0);
        public bool Ready { get; set; } = true;
        public Func<string, bool, CancellationToken, Task<SearchResult>> OnSearch { get; set; } =
            (_, _, _) => Task.FromResult(Result());
        public Func<CancellationToken, Task> OnWarmUp { get; set; } = _ => Task.CompletedTask;
        public List<(string Question, bool Embedding)> Searches { get; } = [];

        public bool EmbeddingReady => Ready;
        public Task<LibrarySummary> GetLibrarySummaryAsync() => Task.FromResult(Summary);
        public Task<FilterOptions> GetFilterOptionsAsync() => Task.FromResult(new FilterOptions([], []));
        public Task<SearchResult> SearchAsync(string question, bool useEmbedding, CancellationToken ct, SearchFilter? filter = null, IReadOnlyList<string>? alsoSearch = null)
        {
            lock (Searches) Searches.Add((question, useEmbedding));
            return OnSearch(question, useEmbedding, ct);
        }
        public Task<IReadOnlyList<string?>> GetBookLanguagesAsync(SearchFilter filter) => Task.FromResult<IReadOnlyList<string?>>([]);
        public Task WarmUpEmbeddingAsync(CancellationToken ct) => OnWarmUp(ct);
    }

    private static SearchHit Hit(long id, string title = "Alice", string text = "Alice fell down the rabbit hole.",
        string? section = "Chapter 1", int? page = null, int? pageEnd = null, double? similarity = 0.62,
        params TextSpan[] spans) =>
        new(id, 1, title, "Lewis Carroll", section, page, pageEnd ?? page, null, text, spans, text, 0.03, similarity, 1, 1, null);

    private static SearchResult Result(IReadOnlyList<SearchHit>? hits = null, bool notCovered = false, bool keywordOnly = false,
        string[]? words = null, string[]? central = null) =>
        new(hits ?? [Hit(1)], notCovered, keywordOnly, keywordOnly ? "english warning" : null, 0.6, false,
            new QueryTerms(words ?? ["rabbit"], []), new SearchTimings(1, 1, 1, 1, 1, 5),
            CentralTerms: central is null ? null : new QueryTerms(central, []));

    private static AskViewModel Vm(FakeAsk service, string question = "falling down a hole") =>
        new(service, new Strings(), SummaryKit.Coordinator(new SummaryKit.FakeSummarizer(), new SummaryKit.InMemoryHistory()), new SummaryKit.Settings(), new SummaryKit.InMemoryHistory()) { Question = question };

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    // ---- results ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AskVm_Results_ExposeHighlightSpans()
    {
        var spans = new[] { new TextSpan(5, 4), new TextSpan(20, 6) };
        var service = new FakeAsk { OnSearch = (_, _, _) => Task.FromResult(Result([Hit(1, spans: spans, page: 12, pageEnd: 13)])) };
        var vm = Vm(service);

        await vm.SearchCommand.ExecuteAsync(null);

        var row = Assert.Single(vm.Latest!.Results);
        Assert.Equal(spans, row.Excerpt.Spans);
        Assert.Equal("Alice", row.Title);
        Assert.Equal("Lewis Carroll", row.Author);
        Assert.Equal("Chapter 1 · Ask_Pages", row.Where);
        Assert.False(row.Excerpt.IsRtl);
        Assert.Equal(["rabbit"], row.Terms.Words);                          // the terms travel with the row, for the Reader
        Assert.False(vm.IsSearching);
    }

    [Fact]
    public async Task AskVm_SinglePage_ShowsPageNotRange()
    {
        var service = new FakeAsk { OnSearch = (_, _, _) => Task.FromResult(Result([Hit(1, page: 7), Hit(2, section: null)])) };
        var vm = Vm(service);

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal("Chapter 1 · Ask_Page", vm.Latest!.Results[0].Where);
        Assert.Equal("", vm.Latest!.Results[1].Where);
    }

    [Fact]
    public async Task AskVm_NoMatches_SaysSo()
    {
        var service = new FakeAsk { OnSearch = (_, _, _) => Task.FromResult(Result([], notCovered: true)) };
        var vm = Vm(service);

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Empty(vm.Latest!.Results);
        Assert.Equal("Ask_NoResults", vm.Latest!.NoResultsText);
    }

    [Fact]
    public async Task AskVm_NotCovered_ShowsBannerAndDisablesSummarize()
    {
        var service = new FakeAsk
        {
            OnSearch = (_, _, _) => Task.FromResult(Result([Hit(1), Hit(2)], notCovered: true)),
        };
        var vm = Vm(service, "something unrelated");

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.True(vm.Latest!.NotCovered);
        Assert.True(vm.Latest!.ShowNotCovered);
        Assert.Equal(2, vm.Latest!.Results.Count);                                  // the nearest passages are still shown
        Assert.All(vm.Latest!.Results, r => Assert.Equal("", r.SummaryText));      // no summary is written for them

        service.OnSearch = (_, _, _) => Task.FromResult(Result());          // a covered question has no banner
        vm.Question = "a covered question";
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.False(vm.Latest!.NotCovered);
    }

    // ---- cancel -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AskVm_NewSearch_CancelsPrevious()
    {
        var firstStarted = new TaskCompletionSource<CancellationToken>();
        var releaseFirst = new TaskCompletionSource();
        var service = new FakeAsk
        {
            OnSearch = async (q, _, ct) =>
            {
                if (q == "first")
                {
                    firstStarted.SetResult(ct);
                    await releaseFirst.Task;               // ignores cancellation, like a slow query that finishes anyway
                    return Result([Hit(1, "From the first search")]);
                }
                return Result([Hit(2, "From the second search")]);
            },
        };
        var vm = Vm(service, "first");

        var first = vm.SearchCommand.ExecuteAsync(null);
        var firstToken = await firstStarted.Task;
        vm.Question = "second";
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.True(firstToken.IsCancellationRequested);
        Assert.Equal(["From the second search"], vm.Latest!.Results.Select(r => r.Title));
        Assert.False(vm.IsSearching);

        releaseFirst.SetResult();                          // the first search now finishes late
        await first;

        Assert.Equal(["From the second search"], vm.Latest!.Results.Select(r => r.Title));   // and is ignored
        Assert.False(vm.IsSearching);
    }

    // ---- embedding server ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AskVm_EmbeddingStarting_ShowsKeywordWarning()
    {
        var warmUp = new TaskCompletionSource();
        var service = new FakeAsk
        {
            Ready = false,
            OnWarmUp = _ => warmUp.Task,
            OnSearch = (_, embedding, _) => Task.FromResult(embedding
                ? Result([Hit(1, "Hybrid result")])
                : Result([Hit(2, "Keyword result")], keywordOnly: true)),
        };
        var vm = Vm(service);

        var run = vm.SearchCommand.ExecuteAsync(null);
        while (!vm.Latest!.HasWarning) await Task.Delay(10).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Ask_WarningStarting", vm.Latest!.Warning);                    // keyword matches are already on screen
        Assert.Equal(["Keyword result"], vm.Latest!.Results.Select(r => r.Title));
        Assert.True(vm.IsSearching);

        warmUp.SetResult();                                                 // the server becomes ready
        await run;

        Assert.Null(vm.Latest!.Warning);
        Assert.Equal(["Hybrid result"], vm.Latest!.Results.Select(r => r.Title));
        Assert.Equal([(  "falling down a hole", false), ("falling down a hole", true)], service.Searches);
    }

    [Fact]
    public async Task AskVm_EmbeddingCannotStart_KeepsKeywordResultsWithUnavailableWarning()
    {
        var service = new FakeAsk
        {
            Ready = false,
            OnWarmUp = _ => throw new ModelUnavailableException("llama-server.exe was not found"),
            OnSearch = (_, _, _) => Task.FromResult(Result([Hit(2, "Keyword result")], keywordOnly: true)),
        };
        var vm = Vm(service);

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal("Ask_WarningUnavailable", vm.Latest!.Warning);
        Assert.Equal(["Keyword result"], vm.Latest!.Results.Select(r => r.Title));
        Assert.Single(service.Searches);
        Assert.False(vm.IsSearching);
    }

    [Fact]
    public async Task AskVm_ServerRunning_SearchesWithMeaningFromTheStart()
    {
        var service = new FakeAsk { Ready = true };
        var vm = Vm(service);

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal([("falling down a hole", true)], service.Searches);
        Assert.Null(vm.Latest!.Warning);
    }

    // ---- dismissing banners -------------------------------------------------------------------------------------

    [Fact]
    public async Task AskVm_DismissNotCovered_HidesBannerForThisSearchOnly()
    {
        var service = new FakeAsk { OnSearch = (_, _, _) => Task.FromResult(Result([Hit(1)], notCovered: true)) };
        var vm = Vm(service);
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.True(vm.Latest!.ShowNotCovered);

        var changed = new List<string?>();
        vm.Latest!.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.Latest!.DismissNotCovered();

        Assert.False(vm.Latest!.ShowNotCovered);
        Assert.Contains(nameof(TurnViewModel.ShowNotCovered), changed);      // so the page closes the bar
        Assert.True(vm.Latest!.NotCovered);                                         // the result is still not covered...

        vm.Question = "another unrelated question";
        await vm.SearchCommand.ExecuteAsync(null);                          // the next search shows its own banner
        Assert.True(vm.Latest!.ShowNotCovered);
    }

    [Fact]
    public async Task AskVm_DismissWarning_ClearsItUntilTheNextSearchNeedsOne()
    {
        var service = new FakeAsk
        {
            Ready = false,
            OnWarmUp = _ => throw new ModelUnavailableException("no server"),
            OnSearch = (_, _, _) => Task.FromResult(Result([Hit(1)], keywordOnly: true)),
        };
        var vm = Vm(service);
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.True(vm.Latest!.HasWarning);

        vm.Latest!.DismissWarning();
        Assert.False(vm.Latest!.HasWarning);
        Assert.Single(vm.Latest!.Results);                                          // the results stay

        vm.Question = "again";
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.True(vm.Latest!.HasWarning);
    }

    [Fact]
    public async Task AskVm_DismissIncompleteNotice_StaysDismissedUntilIndexingFinishes()
    {
        var service = new FakeAsk { Summary = new LibrarySummary(1, 4) };
        var vm = Vm(service);
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.True(vm.HasIncompleteNotice);

        vm.DismissIncompleteNotice();
        Assert.False(vm.HasIncompleteNotice);

        await vm.SearchCommand.ExecuteAsync(null);                          // still indexing: no nagging on every search
        await vm.RefreshAsync();
        Assert.False(vm.HasIncompleteNotice);

        service.Summary = new LibrarySummary(5, 0);                         // indexing finished
        await vm.RefreshAsync();
        service.Summary = new LibrarySummary(5, 2);                         // a new round of indexing starts
        await vm.RefreshAsync();
        Assert.True(vm.HasIncompleteNotice);
    }

    // ---- empty states -------------------------------------------------------------------------------------------

    [Fact]
    public async Task AskVm_NoBooks_ShowsEmptyState()
    {
        var service = new FakeAsk { Summary = new LibrarySummary(0, 0) };
        var vm = Vm(service);

        await vm.RefreshAsync();
        Assert.True(vm.ShowNoBooks);
        Assert.False(vm.ShowStillIndexing);
        Assert.False(vm.CanAsk);

        await vm.SearchCommand.ExecuteAsync(null);                          // searching does nothing without books
        Assert.Empty(service.Searches);
        Assert.Empty(vm.Turns);
    }

    [Fact]
    public async Task AskVm_BooksStillIndexing_ShowsIndexingState()
    {
        var service = new FakeAsk { Summary = new LibrarySummary(0, 2) };
        var vm = Vm(service);

        await vm.RefreshAsync();

        Assert.True(vm.ShowStillIndexing);
        Assert.False(vm.ShowNoBooks);
        Assert.False(vm.CanAsk);
    }

    [Fact]
    public async Task AskVm_SomeBooksStillIndexing_WarnsResultsMayBeIncomplete()
    {
        var service = new FakeAsk { Summary = new LibrarySummary(1, 4) };
        var vm = Vm(service);

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.True(vm.CanAsk);
        Assert.True(vm.HasIncompleteNotice);
        Assert.Equal("Ask_Incomplete", vm.IncompleteNotice);
        Assert.NotEmpty(vm.Latest!.Results);

        service.Summary = new LibrarySummary(5, 0);
        await vm.RefreshAsync();
        Assert.False(vm.HasIncompleteNotice);
    }

    [Fact]
    public async Task AskVm_BlankQuestion_DoesNothing()
    {
        var service = new FakeAsk();
        var vm = Vm(service, "   ");

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Empty(service.Searches);
    }

    [Fact]
    public async Task AskVm_Rows_AndTheReader_GetOnlyTheCentralWords()
    {
        var service = new FakeAsk { OnSearch = (_, _, _) => Task.FromResult(Result(words: ["people", "rabbit"], central: ["rabbit"])) };
        var vm = Vm(service);
        await vm.SearchCommand.ExecuteAsync(null);
        BookSearch.App.Reader.ReaderRequest? opened = null;
        vm.ResultOpened += p => opened = p;

        vm.OpenResultCommand.Execute(vm.Latest!.Results[0]);

        Assert.Equal(["rabbit"], vm.Latest.Results[0].Terms.Words);          // "people" is not highlighted
        Assert.Equal(["rabbit"], opened!.Terms.Words);
    }

    [Fact]
    public async Task AskVm_OpenResult_AsksTheReaderToOpenThatChunk()
    {
        var service = new FakeAsk { OnSearch = (_, _, _) => Task.FromResult(Result([Hit(1, text: "The whole passage about a rabbit.")])) };
        var vm = Vm(service);
        await vm.SearchCommand.ExecuteAsync(null);
        BookSearch.App.Reader.ReaderRequest? opened = null;
        vm.ResultOpened += p => opened = p;

        vm.OpenResultCommand.Execute(vm.Latest!.Results[0]);

        Assert.NotNull(opened);
        Assert.Equal(1, opened!.ChunkId);
        Assert.Equal(["rabbit"], opened.Terms.Words);          // so the Reader can highlight them in the chapter
    }

    [Fact]
    public void AskVm_GoToLibrary_AsksTheShellToShowTheLibrary()
    {
        var vm = Vm(new FakeAsk());
        var requested = 0;
        vm.LibraryRequested += () => requested++;

        vm.GoToLibraryCommand.Execute(null);

        Assert.Equal(1, requested);
    }

    [Fact]
    public void WindowSizing_IsTheSmallestWindowTheRequirementsSay()
    {
        Assert.Equal(800, WindowSizing.MinWidth);                                      // NFR-22
        Assert.Equal(600, WindowSizing.MinHeight);
        Assert.True(WindowSizing.DefaultWidth >= WindowSizing.MinWidth && WindowSizing.DefaultHeight >= WindowSizing.MinHeight);
    }

    // ---- real search over a real database --------------------------------------------------------------------------

    [Fact]
    public async Task AskService_KeywordSearch_FindsPassageWithHighlights()
    {
        var paths = new AppPaths(_dir);
        using (var conn = Database.Open(paths.DatabasePath))
        {
            Migrator.Migrate(conn);
            var ingest = new IngestService(conn, new FakeEmbedder(), new WordCountTokenizer(), new PunctuationSentenceSplitter());
            var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "alice.epub");
            await ingest.AddAsync(path);
            await new IngestWorker(conn, ingest).RunAsync();
        }
        await using var supervisor = new ServerSupervisor(new NeverLauncher());
        using var service = new AskService(paths, supervisor);

        var summary = await service.GetLibrarySummaryAsync();
        var result = await service.SearchAsync("falling", useEmbedding: false, CancellationToken.None);

        Assert.Equal(new LibrarySummary(1, 0), summary);
        Assert.False(service.EmbeddingReady);
        Assert.True(result.KeywordOnly);
        Assert.NotEmpty(result.Hits);
        Assert.NotEmpty(result.Hits[0].Highlights);
        var row = new ResultRow(result.Hits[0], result.HighlightTerms, new Strings());
        Assert.NotEmpty(row.Excerpt.Spans);
    }

    private sealed class NeverLauncher : IServerLauncher
    {
        public Task<IServerHandle> StartAsync(ServerRole role, CancellationToken ct) =>
            throw new ModelUnavailableException("no server in this test");
    }

    [Fact]
    public async Task AskService_EmbeddingServerUnavailable_FallsBackToKeywordResults()
    {
        var paths = new AppPaths(_dir);
        using (var conn = Database.Open(paths.DatabasePath))
        {
            Migrator.Migrate(conn);
            var ingest = new IngestService(conn, new FakeEmbedder(), new WordCountTokenizer(), new PunctuationSentenceSplitter());
            await ingest.AddAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "alice.epub"));
            await new IngestWorker(conn, ingest).RunAsync();
        }
        await using var supervisor = new ServerSupervisor(new NeverLauncher());
        using var service = new AskService(paths, supervisor);

        var result = await service.SearchAsync("rabbit hole", useEmbedding: true, CancellationToken.None);
        Assert.True(result.KeywordOnly);
        Assert.NotEmpty(result.Hits);
        await Assert.ThrowsAsync<ModelUnavailableException>(() => service.WarmUpEmbeddingAsync(CancellationToken.None));
    }

    // ---- text direction and server options --------------------------------------------------------------------------

    [Theory]
    [InlineData("The quick brown fox", false)]
    [InlineData("שלום עולם", true)]
    [InlineData("مرحبا بالعالم", true)]
    [InlineData("日本語のテキスト", false)]
    [InlineData("Hello مرحبا بالعالم الجميل", true)]     // mostly Arabic
    [InlineData("12345 !?", false)]
    [InlineData("", false)]
    public void TextDirection_DetectsRightToLeft(string text, bool rtl) =>
        Assert.Equal(rtl, TextDirection.IsRightToLeft(text));

    [Fact]
    public void ServerOptions_MissingServerOrModel_IsModelUnavailable()
    {
        var paths = new AppPaths(_dir);
        var manifest = ModelManifest.LoadDefault();
        var store = new ModelStore(paths.ModelsFolder);
        var factory = new ServerOptionsFactory(paths, manifest, store, _dir, () => ModelTier.Minimum, () => HardwareTiers.LargeChatContext);

        var noServer = Assert.Throws<ModelUnavailableException>(() => factory.Create(ServerRole.Embedding));
        Assert.Contains("llama-server.exe", noServer.Message);

        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllText(Path.Combine(_dir, "bin", ServerLocator.FileName), "");
        var noModel = Assert.Throws<ModelUnavailableException>(() => factory.Create(ServerRole.Embedding));
        Assert.Contains(manifest.Embedding.File, noModel.Message);

        Directory.CreateDirectory(paths.ModelsFolder);
        File.WriteAllText(store.PathFor(manifest.Embedding), "");
        var options = factory.Create(ServerRole.Embedding);
        Assert.Equal(ServerRole.Embedding, options.Role);
        Assert.Equal(store.PathFor(manifest.Embedding), options.ModelPath);
        Assert.True(options.Threads >= 1);

        File.WriteAllText(store.PathFor(manifest.LlmFor(ModelTier.Minimum)), "");
        var chat = factory.Create(ServerRole.Chat);
        Assert.Equal(store.PathFor(manifest.LlmFor(ModelTier.Minimum)), chat.ModelPath);
        Assert.Equal(HardwareTiers.LargeChatContext, chat.ContextSize);                // the chosen pool
        Assert.Equal(0, options.ContextSize);                                          // the embedding server keeps its own default
    }
}
