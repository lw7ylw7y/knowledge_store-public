using BookSearch.App;
using BookSearch.App.Ask;
using BookSearch.App.History;
using BookSearch.Core.Data;
using BookSearch.Core.Search;
using static BookSearch.Tests.SummaryKit;

namespace BookSearch.Tests;

/// <summary>Tags on books and the explicit search filter (ST-25), which replaced scoping by the title typed in the question.</summary>
public class FilterTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "filter-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private TagRepository Repo => new(_db.Conn);

    // ---- tag text -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("  novels ", "novels")]
    [InlineData("#to  read", "to read")]
    [InlineData("###", null)]
    [InlineData("   ", null)]
    public void Tags_Clean_TrimsCollapsesAndDropsTheHash(string text, string? expected) => Assert.Equal(expected, Tags.Clean(text));

    [Fact]
    public void Tags_Clean_CapsTheLength() => Assert.Equal(Tags.MaxLength, Tags.Clean(new string('a', 100))!.Length);

    [Fact]
    public void Tags_Parse_SplitsOnCommasSemicolonsAndLinesAndDropsDuplicates()
    {
        Assert.Equal(["novels", "to read", "Austen"], Tags.Parse("novels, to read;Austen\nNOVELS,, #novels "));
        Assert.Empty(Tags.Parse(""));
        Assert.Empty(Tags.Parse(null));
    }

    // ---- storing tags -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Tags_Set_ReplacesTheBooksTagsAndIgnoresCaseDuplicates()
    {
        var book = _db.AddBook("Emma");

        Repo.Set(book, ["Novels", "novels", "Austen"]);
        Assert.Equal(["Austen", "Novels"], Repo.ForBook(book));

        Repo.Set(book, ["classics"]);
        Assert.Equal(["classics"], Repo.ForBook(book));
        Repo.Set(book, []);
        Assert.Empty(Repo.ForBook(book));
    }

    [Fact]
    public void Tags_OneSpellingPerTag_TheFirstOneInUseWins()
    {
        var a = _db.AddBook("A");
        var b = _db.AddBook("B");
        Repo.Set(a, ["Novels"]);

        Repo.Set(b, ["novels"]);                                              // typed in another case for another book

        Assert.Equal(["Novels"], Repo.ForBook(b));
        var tag = Assert.Single(Repo.List());
        Assert.Equal(("Novels", 2), (tag.Name, tag.Books));
    }

    [Fact]
    public void Tags_Add_GivesSeveralBooksTheTagsAndKeepsTheirOthers()
    {
        var a = _db.AddBook("A");
        var b = _db.AddBook("B");
        Repo.Set(a, ["mine"]);

        Repo.Add([a, b], ["Novels", "to read"]);

        Assert.Equal(["mine", "Novels", "to read"], Repo.ForBook(a));
        Assert.Equal(["Novels", "to read"], Repo.ForBook(b));
        Assert.Equal(["mine", "Novels", "to read"], Repo.List().Select(t => t.Name));
        Assert.Equal(3, Repo.ForAllBooks()[a].Count);
    }

    [Fact]
    public void Tags_RemovingABook_RemovesItsTags()
    {
        var a = _db.AddBook("A");
        var b = _db.AddBook("B");
        Repo.Add([a, b], ["shared"]);

        new BookRepository(_db.Conn).Delete(a);

        Assert.Empty(Repo.ForBook(a));
        Assert.Equal(1, Assert.Single(Repo.List()).Books);
    }

    // ---- which books a filter allows ------------------------------------------------------------------------------------------

    [Fact]
    public void Filter_Resolve_UnionsTagsAndBooksAndKeepsOnlyIndexedBooks()
    {
        var emma = _db.AddBook("Emma");
        var pride = _db.AddBook("Pride and Prejudice");
        var moby = _db.AddBook("Moby Dick");
        var indexing = _db.AddBook("Still indexing", BookStatus.Indexing);
        Repo.Add([emma, pride, indexing], ["Austen"]);

        Assert.Null(Repo.Resolve(SearchFilter.None));                                                    // no filter: everything
        Assert.Equal([emma, pride], Repo.Resolve(new SearchFilter(["austen"], [])!)!.Order());           // tag, any case; the unfinished book is left out
        Assert.Equal([emma, pride, moby], Repo.Resolve(new SearchFilter(["Austen"], [new FilterBook(moby, "Moby Dick")]))!.Order());
        Assert.Empty(Repo.Resolve(new SearchFilter(["nothing"], []))!);                                  // matches no book: empty, not null
        Assert.Empty(Repo.Resolve(new SearchFilter([], [new FilterBook(999, "Removed")]))!);
    }

    [Fact]
    public void Filter_Json_RoundTripsAndDescribesItself()
    {
        var filter = new SearchFilter(["novels"], [new FilterBook(3, "Emma")]);

        var back = SearchFilter.FromJson(filter.ToJson());

        Assert.Equal(["novels"], back.Tags);
        Assert.Equal([new FilterBook(3, "Emma")], back.Books);
        Assert.Equal("novels, Emma", back.Describe());
        Assert.Null(SearchFilter.None.ToJson());
        Assert.True(SearchFilter.FromJson(null).IsEmpty);
        Assert.True(SearchFilter.FromJson("not json").IsEmpty);
    }

    // ---- searching inside a filter --------------------------------------------------------------------------------------------

    private (long Alice, long Pride) TwoBooksWithAMatchingPassageEach()
    {
        var alice = _db.AddBook("Alice's Adventures in Wonderland");
        var pride = _db.AddBook("Pride and Prejudice");
        _db.Add(
            _db.Write(alice, 0, "Time flies when the rabbit is late", TestDb.Vec(1, 0.1f)),
            _db.Write(pride, 0, "A truth universally acknowledged about time and marriage", TestDb.Vec(1, 0.3f)));
        return (alice, pride);
    }

    [Fact]
    public async Task Search_WithBookIds_StaysInThoseBooks_ForKeywordsAndMeaningAlike()
    {
        var (alice, _) = TwoBooksWithAMatchingPassageEach();
        var withMeaning = new SearchService(_db.Conn, new FakeQueryEmbedder(_ => TestDb.Vec(1)), new SearchOptions { CoverageThreshold = 0 });
        var keywordsOnly = new SearchService(_db.Conn, null);

        var filtered = await withMeaning.SearchAsync("what is said about time", bookIds: new HashSet<long> { alice });
        var filteredKeywords = await keywordsOnly.SearchAsync("what is said about time", bookIds: new HashSet<long> { alice });
        var everywhere = await withMeaning.SearchAsync("what is said about time");

        Assert.NotEmpty(filtered.Hits);
        Assert.All(filtered.Hits, h => Assert.Equal(alice, h.BookId));
        Assert.NotEmpty(filteredKeywords.Hits);
        Assert.All(filteredKeywords.Hits, h => Assert.Equal(alice, h.BookId));
        Assert.Contains(everywhere.Hits, h => h.BookId != alice);                                       // without the filter the other book shows up
    }

    [Fact]
    public async Task Search_NamingABookInTheQuestion_NoLongerNarrowsTheSearch()
    {
        var (alice, _) = TwoBooksWithAMatchingPassageEach();
        var service = new SearchService(_db.Conn, null);

        var result = await service.SearchAsync("what does Alice in Wonderland say about time");

        Assert.Contains(result.Hits, h => h.BookId != alice);                                           // implicit scoping was removed (ST-25)
        Assert.False(result.FilterMatchedNoBooks);
    }

    [Fact]
    public async Task Search_FilterWithNoIndexedBooks_FindsNothingAndSaysSo_NeverEverything()
    {
        TwoBooksWithAMatchingPassageEach();
        var service = new SearchService(_db.Conn, null);

        var result = await service.SearchAsync("what is said about time", bookIds: new HashSet<long>());

        Assert.Empty(result.Hits);
        Assert.True(result.FilterMatchedNoBooks);
    }

    [Fact]
    public async Task AskService_ResolvesTagsAndBooksFromTheLibrary()
    {
        var paths = new AppPaths(_dir);
        long alice, pride;
        using (var conn = Database.Open(paths.DatabasePath))
        {
            Migrator.Migrate(conn);
            var books = new BookRepository(conn);
            alice = books.Insert(new Book { FilePath = "/a.epub", FileHash = "a", Title = "Alice", Format = "epub", Status = BookStatus.Ready });
            pride = books.Insert(new Book { FilePath = "/p.epub", FileHash = "p", Title = "Pride", Format = "epub", Status = BookStatus.Ready });
            foreach (var (id, text) in new[] { (alice, "The rabbit looked at his watch"), (pride, "The rabbit-like Mr Collins looked at his watch") })
                Database.InTransaction(conn, tx => new ChunkRepository(conn).WriteBatch(
                    [new ChunkWrite(new Chunk { BookId = id, ChunkIndex = 0, Text = text, TokenCount = 6 }, TestDb.Vec(1, 0.1f))], tx));
            new TagRepository(conn).Add([alice], ["children"]);
        }
        await using var supervisor = new BookSearch.Models.ServerSupervisor(new NeverLauncher());
        using var service = new AskService(paths, supervisor);

        var options = await service.GetFilterOptionsAsync();
        var byTag = await service.SearchAsync("rabbit watch", false, CancellationToken.None, new SearchFilter(["Children"], []));
        var byBook = await service.SearchAsync("rabbit watch", false, CancellationToken.None, new SearchFilter([], [new FilterBook(pride, "Pride")]));
        var open = await service.SearchAsync("rabbit watch", false, CancellationToken.None);
        var none = await service.SearchAsync("rabbit watch", false, CancellationToken.None, new SearchFilter(["nobody has this"], []));

        Assert.Equal([("children", 1)], options.Tags.Select(t => (t.Name, t.Books)));
        Assert.Equal(["Alice", "Pride"], options.Books.Select(b => b.Title).Order());
        Assert.Equal([alice], byTag.Hits.Select(h => h.BookId));
        Assert.Equal([pride], byBook.Hits.Select(h => h.BookId));
        Assert.Equal(2, open.Hits.Count);
        Assert.True(none.FilterMatchedNoBooks);
    }

    private sealed class NeverLauncher : BookSearch.Models.IServerLauncher
    {
        public Task<BookSearch.Models.IServerHandle> StartAsync(BookSearch.Models.ServerRole role, CancellationToken ct) =>
            throw new BookSearch.Core.ModelUnavailableException("no server in this test");
    }

    // ---- the Ask page ---------------------------------------------------------------------------------------------------------

    private static AskViewModel Ask(FakeAsk ask, SummaryCoordinator coordinator, Settings settings, Dialogs dialogs, InMemoryHistory? history = null) =>
        new(ask, new Strings(), coordinator, settings, history ?? new InMemoryHistory(), a => a(), dialogs) { Question = "who is Darcy" };

    [Fact]
    public async Task AskVm_FilterChip_IsHiddenOnTheNewestQuestionWhileItRepeatsTheFilterLine_ButKeptWhenItDiffers()
    {
        var ask = new FakeAsk { Options = new FilterOptions([new TagInfo("Austen", 3), new TagInfo("Science", 2)], []) };
        var history = new InMemoryHistory();
        var dialogs = new Dialogs { FilterAnswer = new SearchFilter(["Austen"], []) };
        using var coordinator = Coordinator(new FakeSummarizer(), history, ask);
        var vm = Ask(ask, coordinator, new Settings(), dialogs, history);
        await vm.ChooseFilterCommand.ExecuteAsync(null);
        await vm.SearchCommand.ExecuteAsync(null);
        await coordinator.WhenIdleAsync();
        var first = vm.Turns[0];
        Assert.Equal("Austen", first.FilterLabel);
        Assert.False(first.HasFilter);                                                 // the line at the bottom says it already

        dialogs.FilterAnswer = new SearchFilter(["Science"], []);                      // the filter changes
        await vm.ChooseFilterCommand.ExecuteAsync(null);
        Assert.True(first.HasFilter);                                                  // this question was asked with another one

        vm.Question = "and the next one";
        await vm.SearchCommand.ExecuteAsync(null);
        await coordinator.WhenIdleAsync();
        Assert.True(vm.Turns[0].HasFilter);                                            // no longer the newest: keeps its chip
        Assert.False(vm.Turns[1].HasFilter);
    }

    [Fact]
    public async Task AskVm_ChoosingAFilter_AppliesToTheNextSearchIsShownOnTheTurnAndIsRemembered()
    {
        var ask = new FakeAsk { Options = new FilterOptions([new TagInfo("Austen", 3)], [new FilterBook(2, "Emma")]) };
        var history = new InMemoryHistory();
        var settings = new Settings();
        var dialogs = new Dialogs { FilterAnswer = new SearchFilter(["Austen"], [new FilterBook(2, "Emma")]) };
        using var coordinator = Coordinator(new FakeSummarizer(), history, ask);
        var vm = Ask(ask, coordinator, settings, dialogs, history);
        Assert.False(vm.HasFilter);
        Assert.Equal("Ask_FilterNone", vm.FilterSummary);

        await vm.ChooseFilterCommand.ExecuteAsync(null);
        await vm.SearchCommand.ExecuteAsync(null);
        await coordinator.WhenIdleAsync();

        Assert.True(vm.HasFilter);
        Assert.Equal("Ask_FilterActive", vm.FilterSummary);
        Assert.Equal(["Austen"], ask.Filters[0]!.Tags);
        Assert.Equal("Austen, Emma", vm.Latest!.FilterLabel);                                    // the chip on the question
        Assert.False(vm.Latest.HasFilter);                                                       // the line at the bottom says it: no second chip
        var saved = SearchFilter.FromJson(history.All().Single().Filter);                        // kept with the history entry
        Assert.Equal(["Austen"], saved.Tags);
        Assert.Equal([new FilterBook(2, "Emma")], saved.Books);
        var again = Ask(new FakeAsk(), coordinator, settings, new Dialogs(), history);           // the next start
        Assert.Equal("Austen, Emma", again.Filter.Describe());
    }

    [Fact]
    public async Task AskVm_CancellingTheDialog_ChangesNothing_AndClearRemovesTheFilter()
    {
        var ask = new FakeAsk { Options = new FilterOptions([new TagInfo("novels", 2)], []) };
        var settings = new Settings();
        var dialogs = new Dialogs { FilterAnswer = new SearchFilter(["novels"], []) };
        using var coordinator = Coordinator(new FakeSummarizer(), new InMemoryHistory(), ask);
        var vm = Ask(ask, coordinator, settings, dialogs);
        await vm.ChooseFilterCommand.ExecuteAsync(null);
        Assert.True(vm.ClearFilterCommand.CanExecute(null));

        dialogs.FilterAnswer = null;                                                              // cancelled
        await vm.ChooseFilterCommand.ExecuteAsync(null);
        Assert.True(vm.HasFilter);

        vm.ClearFilterCommand.Execute(null);

        Assert.False(vm.HasFilter);
        Assert.False(vm.ClearFilterCommand.CanExecute(null));
        Assert.Null(settings.Load().Filter);
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.True(ask.Filters[0] is null or { IsEmpty: true });
    }

    [Fact]
    public async Task AskVm_Refresh_DropsTagsAndBooksThatNoLongerExist()
    {
        var ask = new FakeAsk { Options = new FilterOptions([new TagInfo("novels", 2)], [new FilterBook(1, "Emma")]) };
        var settings = new Settings { Current = new AppSettings { Filter = new SearchFilter(["Novels", "gone"], [new FilterBook(1, "Emma"), new FilterBook(9, "Removed")]).ToJson() } };
        using var coordinator = Coordinator(new FakeSummarizer(), new InMemoryHistory(), ask);
        var vm = Ask(ask, coordinator, settings, new Dialogs());

        await vm.RefreshAsync();

        Assert.Equal(["Novels"], vm.Filter.Tags);
        Assert.Equal([new FilterBook(1, "Emma")], vm.Filter.Books);
        Assert.Equal(vm.Filter.ToJson(), settings.Load().Filter);
    }

    [Fact]
    public async Task AskVm_FilterMatchingNoBooks_SaysSoInsteadOfNoResults()
    {
        var ask = new FakeAsk
        {
            Options = new FilterOptions([new TagInfo("x", 1)], []),
            ResultFor = _ => Search(0) with { FilterMatchedNoBooks = true, NotCovered = true },
        };
        using var coordinator = Coordinator(new FakeSummarizer(), new InMemoryHistory(), ask);
        var vm = Ask(ask, coordinator, new Settings(), new Dialogs { FilterAnswer = new SearchFilter(["x"], []) });
        await vm.ChooseFilterCommand.ExecuteAsync(null);

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal("Ask_NoBooksInFilter", vm.Latest!.NoResultsText);
    }

    [Fact]
    public async Task Coordinator_UnfinishedSummaryAfterARestart_IsSearchedWithTheFilterItWasAskedWith()
    {
        var history = new InMemoryHistory();
        var filter = new SearchFilter(["Austen"], []);
        history.Add("who is Darcy", SummaryStatus.Waiting, null, history.AddThread(), filter.ToJson());
        var ask = new FakeAsk();
        using var coordinator = Coordinator(new FakeSummarizer(), history, ask);

        await coordinator.ResumeUnfinishedAsync();
        await coordinator.WhenIdleAsync();

        Assert.Equal(["Austen"], ask.Filters.Single()!.Tags);
    }

    [Fact]
    public void Migration5_AddsTagsAndTheHistoryFilter()
    {
        var thread = new SearchHistoryRepository(_db.Conn).AddThread();
        var id = new SearchHistoryRepository(_db.Conn).Add("q", SummaryStatus.None, threadId: thread, filter: "{\"tags\":[\"x\"],\"books\":[]}");

        Assert.Equal("{\"tags\":[\"x\"],\"books\":[]}", new SearchHistoryRepository(_db.Conn).Get(id)!.Filter);
        Assert.True(Migrator.LatestVersion >= 5);
        Assert.Empty(Repo.List());
    }
}
