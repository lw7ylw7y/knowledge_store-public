using BookSearch.App;
using BookSearch.App.Reader;
using BookSearch.Core.Chunking;
using BookSearch.Core.Data;
using BookSearch.Core.Ingest;
using BookSearch.Core.Reading;
using BookSearch.Core.Search;
using BookSearch.Core.Worker;

namespace BookSearch.Tests;

public class ReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "reader-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _tempFiles = [];

    private sealed class Strings : IStrings
    {
        public string Get(string key) => key;
    }

    private sealed class FakeFiles : IFileLauncher
    {
        public HashSet<string> Existing { get; } = [];
        public List<string> Opened { get; } = [];
        public Exception? FailWith { get; set; }
        public bool Exists(string path) => Existing.Contains(path);
        public void Open(string path)
        {
            if (FailWith is not null) throw FailWith;
            Opened.Add(path);
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        foreach (var f in _tempFiles) File.Delete(f);
    }

    private AppPaths Paths => new(_dir);

    /// <summary>Indexes a fixture book into the temp library (the way the worker does) and returns its path and id.</summary>
    private async Task<(string Path, long BookId)> IndexAsync(string fixture)
    {
        var path = Path.Combine(Path.GetTempPath(), $"reader-{Guid.NewGuid():N}-{fixture}");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", fixture), path);
        _tempFiles.Add(path);
        using var conn = Database.Open(Paths.DatabasePath);
        Migrator.Migrate(conn);
        var ingest = new IngestService(conn, new FakeEmbedder(), new WordCountTokenizer(), new PunctuationSentenceSplitter());
        var added = await ingest.AddAsync(path);
        await new IngestWorker(conn, ingest).RunAsync();
        return (path, added.BookId);
    }

    private (ReaderViewModel Vm, ReaderService Service, FakeFiles Files) Reader(string? existingFile = null)
    {
        var service = new ReaderService(Paths);
        var files = new FakeFiles();
        if (existingFile is not null) files.Existing.Add(existingFile);
        return (new ReaderViewModel(service, files, new Strings()), service, files);
    }

    private List<Chunk> ChunksOf(long bookId)
    {
        using var conn = Database.Open(Paths.DatabasePath);
        var chunks = new ChunkRepository(conn);
        var order = chunks.SectionsInReadingOrder(bookId);
        return order.SelectMany(s => chunks.ListBySection(bookId, s)).ToList();
    }

    /// <summary>A chunk in the middle of the chapter that has the most chunks.</summary>
    private Chunk MiddleChunkOfLongestChapter(long bookId)
    {
        var bySection = ChunksOf(bookId).GroupBy(c => c.SectionId).OrderByDescending(g => g.Count()).First().OrderBy(c => c.ChunkIndex).ToList();
        Assert.True(bySection.Count >= 3, "the fixture chapter should have several chunks");
        return bySection[bySection.Count / 2];
    }

    // ---- chapter text -----------------------------------------------------------------------------------------

    [Fact]
    public async Task ChapterText_Merge_JoinsOverlappingChunksWithoutRepeats()
    {
        var (_, bookId) = await IndexAsync("alice.epub");
        using var conn = Database.Open(Paths.DatabasePath);
        var chunks = new ChunkRepository(conn);
        var section = chunks.SectionsInReadingOrder(bookId).First(s => chunks.ListBySection(bookId, s).Count >= 3);
        var list = chunks.ListBySection(bookId, section);

        var merged = ChapterText.Merge(list);

        Assert.Equal(list.Count, merged.Ranges.Count);
        foreach (var (range, chunk) in merged.Ranges.Zip(list))
            Assert.Equal(chunk.Text, merged.Text.Substring(range.Start, range.Length));   // every chunk sits intact in the text
        Assert.True(merged.Text.Length < list.Sum(c => c.Text.Length));                  // and the repeated sentences were dropped
        Assert.Equal(merged.Ranges.OrderBy(r => r.Start), merged.Ranges);                // in reading order
    }

    [Fact]
    public void ChapterText_Merge_ChunksWithNothingInCommonAreSeparatedByABlankLine()
    {
        var chunks = new List<Chunk>
        {
            new() { Id = 1, BookId = 1, ChunkIndex = 0, Text = "First paragraph of the chapter." },
            new() { Id = 2, BookId = 1, ChunkIndex = 1, Text = "A different second paragraph." },
        };

        var merged = ChapterText.Merge(chunks);

        Assert.Equal("First paragraph of the chapter.\n\nA different second paragraph.", merged.Text);
        Assert.Equal(new ChunkRange(2, 1, 33, 29), merged.Ranges[1]);
        Assert.Equal(new ChapterText("", []).Text, ChapterText.Merge([]).Text);
    }

    // ---- reader view model ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ReaderVm_OpenChunk_LoadsSectionAndHighlightRange()
    {
        var (path, bookId) = await IndexAsync("alice.epub");
        var chunk = MiddleChunkOfLongestChapter(bookId);
        var (vm, service, _) = Reader(path);
        using var _s = service;
        var scrolledTo = new List<int>();
        vm.ScrollRequested += scrolledTo.Add;
        var word = chunk.Text.Split(' ').First(w => w.Length > 5 && w.All(char.IsLetter)).ToLowerInvariant();

        await vm.OpenAsync(new ReaderRequest(chunk.Id, new QueryTerms([word], [])));

        Assert.False(vm.IsLoading);
        Assert.Null(vm.Error);
        Assert.False(string.IsNullOrEmpty(vm.BookTitle));
        Assert.NotEqual("", vm.ChapterTitle);
        Assert.StartsWith("Reader_Position", vm.PositionText);

        // The highlighted ranges, put back together, are exactly the passage that was opened.
        var passageParts = vm.Paragraphs.Where(p => p.PassageSpan is not null)
            .Select(p => p.Text.Substring(p.PassageSpan!.Value.Start, p.PassageSpan.Value.Length)).ToList();
        Assert.Equal(chunk.Text, string.Join("\n\n", passageParts));
        Assert.True(vm.Paragraphs.Count > passageParts.Count);                // the rest of the chapter is there too

        var first = vm.Paragraphs.ToList().FindIndex(p => p.PassageSpan is not null);
        Assert.Equal([first], scrolledTo);                                    // scrolled to the passage
        Assert.Contains(vm.Paragraphs, p => p.TermSpans.Count > 0);           // the question's words are marked
    }

    [Fact]
    public async Task ReaderVm_NextChapter_LoadsFollowingSection()
    {
        var (path, bookId) = await IndexAsync("alice.epub");
        var chunk = ChunksOf(bookId).First();
        var (vm, service, _) = Reader(path);
        using var _s = service;
        await vm.OpenAsync(new ReaderRequest(chunk.Id, new QueryTerms([], [])));
        var firstTitle = vm.ChapterTitle;

        Assert.False(vm.CanGoPrevious);                                       // the first chapter has nothing before it
        Assert.True(vm.CanGoNext);

        await vm.NextChapterCommand.ExecuteAsync(null);

        Assert.NotEqual(firstTitle, vm.ChapterTitle);
        Assert.True(vm.CanGoPrevious);
        Assert.DoesNotContain(vm.Paragraphs, p => p.PassageSpan is not null); // no passage is marked in another chapter

        await vm.PreviousChapterCommand.ExecuteAsync(null);
        Assert.Equal(firstTitle, vm.ChapterTitle);
        Assert.False(vm.CanGoPrevious);
    }

    [Fact]
    public async Task ReaderVm_LastChapter_HasNoNext()
    {
        var (path, bookId) = await IndexAsync("alice.epub");
        var last = ChunksOf(bookId).Last();
        var (vm, service, _) = Reader(path);
        using var _s = service;

        await vm.OpenAsync(new ReaderRequest(last.Id, new QueryTerms([], [])));

        Assert.False(vm.CanGoNext);
        Assert.True(vm.CanGoPrevious);
        Assert.Contains(vm.Paragraphs, p => p.PassageSpan is not null);
    }

    [Fact]
    public async Task ReaderVm_ArabicBook_SetsRightToLeft()
    {
        var (path, bookId) = await IndexAsync("arabic.txt");
        var (vm, service, _) = Reader(path);
        using var _s = service;

        await vm.OpenAsync(new ReaderRequest(ChunksOf(bookId).First().Id, new QueryTerms([], [])));

        Assert.True(vm.IsRtl);
        Assert.All(vm.Paragraphs, p => Assert.True(p.IsRtl));
    }

    [Fact]
    public async Task ReaderVm_EnglishBook_ReadsLeftToRight()
    {
        var (path, bookId) = await IndexAsync("alice.epub");
        var (vm, service, _) = Reader(path);
        using var _s = service;

        await vm.OpenAsync(new ReaderRequest(ChunksOf(bookId).First().Id, new QueryTerms([], [])));

        Assert.False(vm.IsRtl);
    }

    [Fact]
    public async Task ReaderVm_PdfPassage_ShowsPageNumbers()
    {
        var (path, bookId) = await IndexAsync("origin.pdf");
        var chunk = ChunksOf(bookId).First(c => c.PageStart is not null);
        var (vm, service, _) = Reader(path);
        using var _s = service;

        await vm.OpenAsync(new ReaderRequest(chunk.Id, new QueryTerms([], [])));

        Assert.StartsWith("Reader_Page", vm.PagesText);
    }

    [Fact]
    public async Task ReaderVm_MissingSourceFile_DisablesOpenFile()
    {
        var (path, bookId) = await IndexAsync("alice.epub");
        var (vm, service, files) = Reader(existingFile: null);              // the original file is gone
        using var _s = service;

        await vm.OpenAsync(new ReaderRequest(ChunksOf(bookId).First().Id, new QueryTerms([], [])));

        Assert.False(vm.CanOpenFile);
        Assert.False(vm.OpenFileCommand.CanExecute(null));
        Assert.Equal("Reader_FileNotFound", vm.FileMessage);
        Assert.True(vm.HasFileMessage);
        Assert.Empty(vm.Paragraphs.Where(p => p.Text.Length == 0));          // the text is still readable without the file
        Assert.NotEmpty(vm.Paragraphs);
        Assert.Empty(files.Opened);
        _ = path;
    }

    [Fact]
    public async Task ReaderVm_OpenFile_LaunchesTheOriginal_AndNoticesIfItWasMovedMeanwhile()
    {
        var (path, bookId) = await IndexAsync("alice.epub");
        var (vm, service, files) = Reader(path);
        using var _s = service;
        await vm.OpenAsync(new ReaderRequest(ChunksOf(bookId).First().Id, new QueryTerms([], [])));
        Assert.True(vm.CanOpenFile);
        Assert.False(vm.HasFileMessage);

        vm.OpenFileCommand.Execute(null);
        Assert.Equal([path], files.Opened);

        files.Existing.Clear();                                              // the file is moved after the chapter was loaded
        vm.OpenFileCommand.Execute(null);
        Assert.Single(files.Opened);                                         // nothing more was launched
        Assert.False(vm.CanOpenFile);
        Assert.Equal("Reader_FileNotFound", vm.FileMessage);
    }

    [Fact]
    public async Task ReaderVm_NoProgramForTheFile_ShowsWhy()
    {
        var (path, bookId) = await IndexAsync("alice.epub");
        var (vm, service, files) = Reader(path);
        using var _s = service;
        files.FailWith = new IOException("No application is associated with this file.");
        await vm.OpenAsync(new ReaderRequest(ChunksOf(bookId).First().Id, new QueryTerms([], [])));

        vm.OpenFileCommand.Execute(null);

        Assert.Equal("Reader_OpenFailed", vm.FileMessage);
        Assert.True(vm.CanOpenFile);                                         // the user can try again
    }

    [Fact]
    public async Task ReaderVm_UnknownChunk_ShowsError()
    {
        await IndexAsync("notes.md");
        var (vm, service, _) = Reader();
        using var _s = service;

        await vm.OpenAsync(new ReaderRequest(999_999, new QueryTerms([], [])));

        Assert.True(vm.HasError);
        Assert.Equal("Reader_LoadFailed", vm.Error);
        Assert.False(vm.IsLoading);
    }

    // ---- reading a whole book from the Library: the saved place (ST-35) --------------------------------------

    private ReadingPosition? SavedPlace(long bookId)
    {
        using var conn = Database.Open(Paths.DatabasePath);
        return new ReadingPositionRepository(conn).Get(bookId);
    }

    [Fact]
    public async Task ReadingPosition_IsOneRowPerBook_AndASecondSaveReplacesTheFirst()
    {
        var (_, alice) = await IndexAsync("alice.epub");
        var (_, arabic) = await IndexAsync("arabic.txt");
        using var conn = Database.Open(Paths.DatabasePath);
        var positions = new ReadingPositionRepository(conn);

        positions.Save(alice, 2, 10);
        positions.Save(arabic, 0, 3);
        positions.Save(alice, 5, 1);

        Assert.Equal(new ReadingPosition(alice, 5, 1), positions.Get(alice));          // replaced, not added
        Assert.Equal(new ReadingPosition(arabic, 0, 3), positions.Get(arabic));        // the other book's place is untouched
        Assert.Equal(2, positions.All().Count);
        positions.Save(999_999, 1, 1);                                                 // a book that is gone: nothing happens
        Assert.Null(positions.Get(999_999));
    }

    [Fact]
    public async Task ReadingPosition_IsRemovedWithTheBook()
    {
        var (_, bookId) = await IndexAsync("alice.epub");
        using var conn = Database.Open(Paths.DatabasePath);
        var positions = new ReadingPositionRepository(conn);
        positions.Save(bookId, 1, 1);

        new BookRepository(conn).Delete(bookId);

        Assert.Empty(positions.All());
    }

    [Fact]
    public async Task ReadingPosition_SectionCounts_MatchTheChaptersTheReaderPagesThrough()
    {
        var (_, bookId) = await IndexAsync("alice.epub");
        var (_, other) = await IndexAsync("arabic.txt");
        using var conn = Database.Open(Paths.DatabasePath);
        var positions = new ReadingPositionRepository(conn);
        positions.Save(bookId, 0, 0);                                                  // only books that were opened are counted

        var counts = positions.SectionCounts();

        Assert.Equal(new ChunkRepository(conn).SectionsInReadingOrder(bookId).Count, counts[bookId]);
        Assert.Equal(counts[bookId], positions.SectionCount(bookId));                  // the per-book lookup agrees
        Assert.DoesNotContain(other, counts.Keys);
    }

    [Fact]
    public async Task ReaderService_LoadBook_StartsAtTheFirstChapter_ThenAtTheSavedPlace_ClampedWhenTheBookChanged()
    {
        var (_, bookId) = await IndexAsync("alice.epub");
        using var service = new ReaderService(Paths);

        var first = await service.LoadBookAsync(bookId);
        Assert.Equal(0, first!.Index);
        Assert.Equal(0, first.ResumeParagraph);
        Assert.Null(first.HighlightChunkId);                                           // no passage is marked

        await service.SavePositionAsync(bookId, 3, 7);
        var resumed = await service.LoadBookAsync(bookId);
        Assert.Equal(3, resumed!.Index);
        Assert.Equal(7, resumed.ResumeParagraph);

        await service.SavePositionAsync(bookId, 9_999, 4);                             // the book has fewer chapters now
        var clamped = await service.LoadBookAsync(bookId);
        Assert.Equal(clamped!.Count - 1, clamped.Index);                               // the nearest place, no error

        Assert.Null(await service.LoadBookAsync(999_999));                             // a book that is gone
    }

    [Fact]
    public async Task ReaderVm_OpenBook_ShowsTheSavedPlaceWithoutHighlights_AndBackGoesToTheLibrary()
    {
        var (path, bookId) = await IndexAsync("alice.epub");
        var (vm, service, _) = Reader(path);
        using var _s = service;
        await service.SavePositionAsync(bookId, 2, 1);
        var scrolledTo = new List<int>();
        vm.ScrollRequested += scrolledTo.Add;

        await vm.OpenBookAsync(bookId);

        Assert.Null(vm.Error);
        Assert.Equal(ReaderOrigin.Library, vm.Origin);
        Assert.Equal("Reader_BackToLibrary", vm.BackText);
        Assert.StartsWith("Reader_Position", vm.PositionText);
        Assert.All(vm.Paragraphs, p => { Assert.Null(p.PassageSpan); Assert.Empty(p.TermSpans); });
        Assert.Equal([Math.Min(1, vm.Paragraphs.Count - 1)], scrolledTo);              // scrolled to the saved paragraph
        Assert.Equal(2, SavedPlace(bookId)!.SectionPos);                               // and it is still the place
    }

    [Fact]
    public async Task ReaderVm_FromTheLibrary_SavesThePlaceAsTheReaderMoves_OnlyWhenItChanged()
    {
        var (path, bookId) = await IndexAsync("alice.epub");
        var (vm, service, _) = Reader(path);
        using var _s = service;
        await vm.OpenBookAsync(bookId);
        Assert.Equal(new ReadingPosition(bookId, 0, 0), SavedPlace(bookId));           // opening records the place: it is now "opened"
        Assert.True(vm.Paragraphs.Count > 5);

        await vm.NoteVisibleParagraphAsync(4);
        Assert.Equal(4, SavedPlace(bookId)!.Paragraph);

        await vm.NextChapterCommand.ExecuteAsync(null);                                // a new chapter starts at its top
        Assert.Equal(new ReadingPosition(bookId, 1, 0), SavedPlace(bookId));

        var last = vm.Paragraphs.Count - 1;                                            // a short chapter: ask for its last paragraph
        Assert.True(last >= 1);
        await vm.NoteVisibleParagraphAsync(last);
        await vm.SavePlaceAsync();                                                     // leaving the page
        Assert.Equal(new ReadingPosition(bookId, 1, last), SavedPlace(bookId));
    }

    [Fact]
    public async Task ReaderVm_FromASearchResult_NeverMovesTheSavedPlace_AndSaysBackToResults()
    {
        var (path, bookId) = await IndexAsync("alice.epub");
        var (vm, service, _) = Reader(path);
        using var _s = service;
        await service.SavePositionAsync(bookId, 1, 3);
        var chunk = MiddleChunkOfLongestChapter(bookId);

        await vm.OpenAsync(new ReaderRequest(chunk.Id, new QueryTerms([], [])));
        await vm.NextChapterCommand.ExecuteAsync(null);
        await vm.NoteVisibleParagraphAsync(2);
        await vm.SavePlaceAsync();

        Assert.Equal("Reader_BackToResults", vm.BackText);
        Assert.Equal(new ReadingPosition(bookId, 1, 3), SavedPlace(bookId));           // looking a passage up is not reading on
    }

    [Fact]
    public async Task ReaderVm_OpenBook_WithNoText_SaysSoAndOffersTheOriginal_AndAGoneBookSaysThat()
    {
        // A book that was added but never indexed has no chunks at all.
        var path = Path.Combine(Path.GetTempPath(), $"reader-{Guid.NewGuid():N}-alice.epub");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "alice.epub"), path);
        _tempFiles.Add(path);
        long bookId;
        using (var conn = Database.Open(Paths.DatabasePath))
        {
            Migrator.Migrate(conn);
            var ingest = new IngestService(conn, new FakeEmbedder(), new WordCountTokenizer(), new PunctuationSentenceSplitter());
            bookId = (await ingest.AddAsync(path)).BookId;
        }
        var (vm, service, _) = Reader(path);
        using var _s = service;

        await vm.OpenBookAsync(bookId);

        Assert.Equal("Reader_NoText", vm.Error);
        Assert.Empty(vm.Paragraphs);
        Assert.True(vm.OpenFileCommand.CanExecute(null));                              // the original is still there to read
        Assert.Null(SavedPlace(bookId));

        await vm.OpenBookAsync(999_999);
        Assert.Equal("Reader_BookNotFound", vm.Error);
    }

    // ---- review fixes (ST-35) ------------------------------------------------------------------------------------

    /// <summary>Delegates to a real service but fails every save, as a locked database would.</summary>
    private sealed class FailingSaves(IReaderService inner) : IReaderService
    {
        public Task<ReaderChapter?> LoadForChunkAsync(long chunkId) => inner.LoadForChunkAsync(chunkId);
        public Task<ReaderChapter?> LoadChapterAsync(long bookId, int index) => inner.LoadChapterAsync(bookId, index);
        public Task<ReaderChapter?> LoadBookAsync(long bookId) => inner.LoadBookAsync(bookId);
        public Task SavePositionAsync(long bookId, int chapterIndex, int paragraph) =>
            throw new Microsoft.Data.Sqlite.SqliteException("database is locked", 5);
    }

    [Fact]
    public async Task ReaderVm_AFailedBookLoad_NeverSavesTheBookShownBefore()
    {
        var (path, bookId) = await IndexAsync("alice.epub");
        var (vm, service, _) = Reader(path);
        using var _s = service;
        await vm.OpenBookAsync(bookId);
        await vm.NoteVisibleParagraphAsync(3);
        Assert.Equal(3, SavedPlace(bookId)!.Paragraph);

        await vm.OpenBookAsync(999_999);                                               // the next book is gone
        await vm.NoteVisibleParagraphAsync(0);                                         // the page's timer still ticks
        await vm.SavePlaceAsync();                                                     // and leaving saves

        Assert.Equal("Reader_BookNotFound", vm.Error);
        Assert.Empty(vm.Paragraphs);                                                   // the old text is not left under the error
        Assert.Equal(3, SavedPlace(bookId)!.Paragraph);                                // the first book's place did not move
    }

    [Fact]
    public async Task ReaderVm_ASavedParagraphBeyondTheChapter_IsClampedToItsLastParagraph()
    {
        var (path, bookId) = await IndexAsync("alice.epub");
        var (vm, service, _) = Reader(path);
        using var _s = service;
        await service.SavePositionAsync(bookId, 1, 9_999);
        var scrolledTo = new List<int>();
        vm.ScrollRequested += scrolledTo.Add;

        await vm.OpenBookAsync(bookId);

        Assert.Equal([vm.Paragraphs.Count - 1], scrolledTo);
        Assert.Equal(vm.Paragraphs.Count - 1, SavedPlace(bookId)!.Paragraph);          // and that is the place now
    }

    [Fact]
    public async Task ReaderVm_AFailedSave_DoesNotInterruptReading()
    {
        var (path, bookId) = await IndexAsync("alice.epub");
        var real = new ReaderService(Paths);
        using var _s = real;
        var vm = new ReaderViewModel(new FailingSaves(real), new FakeFiles(), new Strings());

        await vm.OpenBookAsync(bookId);
        await vm.NoteVisibleParagraphAsync(2);
        await vm.SavePlaceAsync();

        Assert.Null(vm.Error);
        Assert.NotEmpty(vm.Paragraphs);
    }

    [Fact]
    public async Task ReaderVm_IdenticalParagraphs_KeepTheirOwnIndex()
    {
        var (path, bookId) = await IndexAsync("alice.epub");
        var (vm, service, _) = Reader(path);
        using var _s = service;

        await vm.OpenBookAsync(bookId);

        Assert.Equal(Enumerable.Range(0, vm.Paragraphs.Count), vm.Paragraphs.Select(p => p.Index));   // so a scroll finds the right one
    }
}
