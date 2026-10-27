using BookSearch.App.Ask;
using BookSearch.Core.Search;
using System.Collections.Specialized;
using System.ComponentModel;
using BookSearch.App;
using BookSearch.Core.Chunking;
using BookSearch.Core.Data;
using BookSearch.Core.Ingest;
using BookSearch.Core.Worker;
using BookSearch.Models.Management;

namespace BookSearch.Tests;

public class LibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "library-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _tempFiles = [];

    private sealed class Strings : IStrings
    {
        public string Get(string key) => key;
    }

    private sealed class Dialogs(bool answer) : IDialogs
    {
        public List<string> Asked { get; } = [];
        public Task<bool> ConfirmRemoveAsync(string bookTitle) { Asked.Add(bookTitle); return Task.FromResult(answer); }
        public Task<bool> ConfirmClearHistoryAsync() => Task.FromResult(answer);
        public Task<bool> ConfirmDeleteThreadAsync(int questions) => Task.FromResult(answer);
        public Task<DeleteAllChoice> ConfirmDeleteAllAsync() => Task.FromResult(DeleteAllChoice.Cancel);
        public IReadOnlyList<string>? TagsAnswer { get; set; }
        public Task<SearchFilter?> ChooseFilterAsync(FilterOptions options, SearchFilter current) => Task.FromResult<SearchFilter?>(null);
        public Task<IReadOnlyList<string>?> EditTagsAsync(string bookTitle, IReadOnlyList<string> current, IReadOnlyList<string> allTags) => Task.FromResult(TagsAnswer);
        public Task<IReadOnlyList<string>?> AddTagsAsync(int books, IReadOnlyList<string> allTags) => Task.FromResult(TagsAnswer);
    }

    private sealed class CountingLauncher : IWorkerLauncher
    {
        public int Starts { get; private set; }
        public IWorkerProcess Start() { Starts++; return new Done(); }
        private sealed class Done : IWorkerProcess { public Task<int> Exited => Task.FromResult(0); }
    }

    /// <summary>A service that keeps everything in memory, so view-model behaviour can be tested on its own.</summary>
    private sealed class FakeService : ILibraryService
    {
        public List<BookEntry> Books { get; } = [];
        public List<long> Removed { get; } = [];
        public List<BookProgress> PendingChanges { get; } = [];
        public bool Paused { get; set; }
        public string? WorkerProblem { get; set; }
        public int WorkerRequests { get; private set; }
        public bool WorkerGaveUp { get; set; }

        public Task<IReadOnlyList<BookEntry>> ListAsync() => Task.FromResult<IReadOnlyList<BookEntry>>(Books.ToList());
        public Task<BookEntry?> GetAsync(long id) => Task.FromResult(Books.FirstOrDefault(b => b.Id == id));
        public Task<AddResult> AddAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveAsync(long id) { Removed.Add(id); return Task.CompletedTask; }
        public Task<IReadOnlyList<BookProgress>> PollAsync()
        {
            var changes = PendingChanges.ToList();
            PendingChanges.Clear();
            return Task.FromResult<IReadOnlyList<BookProgress>>(changes);
        }
        public Dictionary<long, ReadingPlace> Places { get; } = [];
        public Task<IReadOnlyDictionary<long, ReadingPlace>> ListPlacesAsync() =>
            Task.FromResult<IReadOnlyDictionary<long, ReadingPlace>>(new Dictionary<long, ReadingPlace>(Places));
        public Task<bool> GetPausedAsync() => Task.FromResult(Paused);
        public Task SetPausedAsync(bool paused) { Paused = paused; return Task.CompletedTask; }
        public Task<WorkerStart> EnsureWorkerAsync() { WorkerRequests++; return Task.FromResult(new WorkerStart(WorkerProblem)); }

        public List<string> AllTags { get; } = [];
        public Dictionary<long, IReadOnlyList<string>> TagsSet { get; } = [];
        public List<(IReadOnlyList<long> Books, IReadOnlyList<string> Tags)> TagsAdded { get; } = [];
        public Task<IReadOnlyList<string>> ListTagsAsync() => Task.FromResult<IReadOnlyList<string>>(AllTags.ToList());
        public Task SetTagsAsync(long bookId, IReadOnlyList<string> tags)
        {
            TagsSet[bookId] = tags;
            var i = Books.FindIndex(b => b.Id == bookId);
            if (i >= 0) Books[i] = Books[i] with { Tags = tags };
            return Task.CompletedTask;
        }
        public Task AddTagsAsync(IReadOnlyList<long> bookIds, IReadOnlyList<string> tags)
        {
            TagsAdded.Add((bookIds, tags));
            foreach (var id in bookIds)
            {
                var i = Books.FindIndex(b => b.Id == id);
                if (i >= 0) Books[i] = Books[i] with { Tags = (Books[i].Tags ?? []).Concat(tags).ToList() };
            }
            return Task.CompletedTask;
        }
    }

    private static BookEntry Entry(long id, string title, string status = BookStatus.Ready, double progress = 1,
        string stage = IngestStage.Done, string? reason = null) =>
        new(id, title, "An Author", "en", status, reason, stage, progress);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        foreach (var f in _tempFiles) File.Delete(f);
    }

    private AppPaths Paths => new(_dir);

    private string TempCopy(string fixture, string? newName = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lib-{Guid.NewGuid():N}-{newName ?? fixture}");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", fixture), path);
        _tempFiles.Add(path);
        return path;
    }

    private (LibraryService Service, LibraryViewModel Vm, CountingLauncher Launcher) Real(string? problem = null, bool confirm = true)
    {
        var launcher = new CountingLauncher();
        var service = new LibraryService(Paths, launcher, () => problem);
        return (service, new LibraryViewModel(service, new Dialogs(confirm), new Strings()), launcher);
    }

    // ---- add ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task LibraryVm_AddFiles_CreatesQueuedRows()
    {
        var (service, vm, launcher) = Real();
        using var _ = service;

        await vm.AddFilesAsync([TempCopy("notes.md"), TempCopy("plain.txt"), TempCopy("alice.epub")]);

        Assert.Equal(3, vm.Books.Count);
        Assert.All(vm.Books, r => Assert.Equal(BookStatus.Queued, r.Status));
        Assert.All(vm.Books, r => Assert.Equal("Library_StatusQueued", r.StatusText));
        Assert.False(vm.IsEmpty);
        Assert.Null(vm.NoticeText);
        Assert.Equal(3, (await service.ListAsync()).Count);       // and they are in the database
        Assert.True(launcher.Starts >= 1);                        // the worker was asked to run
    }

    [Fact]
    public async Task LibraryVm_DuplicateFile_ShowsNotice()
    {
        var (service, vm, _) = Real();
        using var _s = service;
        await vm.AddFilesAsync([TempCopy("notes.md")]);

        await vm.AddFilesAsync([TempCopy("notes.md", "same-content-other-name.md")]);

        Assert.Single(vm.Books);
        Assert.Equal("Library_NoticeDuplicate", vm.NoticeText);
        Assert.True(vm.HasNotice);

        await vm.AddFilesAsync([TempCopy("plain.txt")]);                   // a later successful add clears the notice
        Assert.Equal(2, vm.Books.Count);
        Assert.Null(vm.NoticeText);
    }

    [Fact]
    public async Task LibraryVm_UnsupportedFile_IsSkippedWithNotice()
    {
        var (service, vm, launcher) = Real();
        using var _ = service;
        var docx = Path.Combine(Path.GetTempPath(), $"lib-{Guid.NewGuid():N}.docx");
        File.WriteAllText(docx, "not a book");
        _tempFiles.Add(docx);

        await vm.AddFilesAsync([docx]);

        Assert.Empty(vm.Books);
        Assert.Equal("Library_NoticeUnsupported", vm.NoticeText);
        Assert.Equal(0, launcher.Starts);
    }

    [Fact]
    public async Task LibraryVm_MissingFile_IsReportedNotThrown()
    {
        var (service, vm, _) = Real();
        using var _s = service;

        await vm.AddFilesAsync([Path.Combine(_dir, "gone.epub")]);

        Assert.Empty(vm.Books);
        Assert.Equal("Library_NoticeUnreadable", vm.NoticeText);
    }

    [Fact]
    public async Task LibraryVm_PrerequisiteMissing_ShowsBannerAndKeepsBooksQueued()
    {
        var (service, vm, launcher) = Real(problem: "llama-server is missing");
        using var _ = service;

        await vm.AddFilesAsync([TempCopy("notes.md")]);

        Assert.Equal("llama-server is missing", vm.Banner);
        Assert.True(vm.HasBanner);
        Assert.Equal(BookStatus.Queued, vm.Books.Single().Status);
        Assert.Equal(0, launcher.Starts);
    }

    // ---- load, progress and rejection -------------------------------------------------------------------------

    [Fact]
    public async Task LibraryVm_Load_ShowsExistingBooksAndResumesUnfinishedOnes()
    {
        var service = new FakeService();
        service.Books.AddRange([Entry(1, "Done"), Entry(2, "Half", BookStatus.Indexing, 0.5, IngestStage.Embed)]);
        var vm = new LibraryViewModel(service, new Dialogs(true), new Strings());

        await vm.LoadAsync();

        Assert.Equal(["Done", "Half"], vm.Books.Select(b => b.Title));
        Assert.Equal(50, vm.Books[1].Percent);
        Assert.True(vm.Books[1].ShowStatus);                                           // a book that is not ready says where it is
        Assert.False(vm.Books[0].ShowStatus);                                          // "Ready" is not shown: it is the normal state
        Assert.Equal(1, service.WorkerRequests);
        Assert.Equal("An Author · EN", vm.Books[0].Byline);
    }

    [Fact]
    public async Task LibraryVm_ProgressUpdate_UpdatesRowOnly()
    {
        var service = new FakeService();
        service.Books.AddRange([Entry(1, "A", BookStatus.Indexing, 0.1, IngestStage.Embed), Entry(2, "B", BookStatus.Queued, 0, IngestStage.Extract)]);
        var vm = new LibraryViewModel(service, new Dialogs(true), new Strings());
        await vm.LoadAsync();
        var a = vm.Books[0];
        var b = vm.Books[1];

        var listChanges = new List<NotifyCollectionChangedEventArgs>();
        var aChanges = new List<string?>();
        var bChanges = new List<string?>();
        vm.Books.CollectionChanged += (_, e) => listChanges.Add(e);
        ((INotifyPropertyChanged)a).PropertyChanged += (_, e) => aChanges.Add(e.PropertyName);
        ((INotifyPropertyChanged)b).PropertyChanged += (_, e) => bChanges.Add(e.PropertyName);

        service.PendingChanges.Add(new BookProgress(1, BookStatus.Indexing, IngestStage.Embed, 0.6, null));
        await vm.PollAsync();

        Assert.Empty(listChanges);                       // the list itself was not rebuilt
        Assert.Same(a, vm.Books[0]);
        Assert.Equal(60, a.Percent);
        Assert.Contains(nameof(BookRow.Percent), aChanges);
        Assert.Empty(bChanges);                          // and the other row heard nothing
    }

    [Fact]
    public async Task LibraryVm_FinishedBook_ReloadsTitleAndStatus()
    {
        var service = new FakeService();
        service.Books.Add(Entry(1, "notes", BookStatus.Indexing, 0.9, IngestStage.Embed));
        var vm = new LibraryViewModel(service, new Dialogs(true), new Strings());
        await vm.LoadAsync();

        service.Books[0] = new BookEntry(1, "Proper Title", "Real Author", "fr", BookStatus.Ready, null, IngestStage.Done, 1);
        service.PendingChanges.Add(new BookProgress(1, BookStatus.Ready, IngestStage.Done, 1, null));
        await vm.PollAsync();

        Assert.Equal("Proper Title", vm.Books[0].Title);
        Assert.Equal("Real Author · FR", vm.Books[0].Byline);
        Assert.Equal("Library_StatusReady", vm.Books[0].StatusText);
        Assert.False(vm.Books[0].ShowStatus);
    }

    [Fact]
    public async Task LibraryVm_RejectedBook_ShowsReason()
    {
        var (service, vm, _) = Real();
        using var _s = service;
        await vm.AddFilesAsync([TempCopy("scanned.pdf")]);
        await vm.PollAsync();

        // The worker (here run in-process) rejects the scanned PDF.
        using (var conn = Database.Open(Paths.DatabasePath))
        {
            var ingest = new IngestService(conn, new FakeEmbedder(), new WordCountTokenizer(), new PunctuationSentenceSplitter());
            await new IngestWorker(conn, ingest).RunAsync();
        }
        await vm.PollAsync();

        var row = vm.Books.Single();
        Assert.Equal(BookStatus.Rejected, row.Status);
        Assert.Equal("Library_StatusRejected", row.StatusText);
        Assert.False(string.IsNullOrWhiteSpace(row.Reason));
        Assert.True(row.ShowReason);
        Assert.True(row.ShowStatus);                                                   // rejected: the status shows with its reason
    }

    [Fact]
    public async Task LibraryVm_BookIndexedByWorker_BecomesReady()
    {
        var (service, vm, _) = Real();
        using var _s = service;
        await vm.AddFilesAsync([TempCopy("notes.md")]);

        using (var conn = Database.Open(Paths.DatabasePath))
        {
            var ingest = new IngestService(conn, new FakeEmbedder(), new WordCountTokenizer(), new PunctuationSentenceSplitter());
            await new IngestWorker(conn, ingest).RunAsync();
        }
        await vm.PollAsync();

        var row = vm.Books.Single();
        Assert.Equal(BookStatus.Ready, row.Status);
        Assert.Equal(100, row.Percent);
        Assert.False(vm.HasActiveJobs);
    }

    [Fact]
    public void LibraryVm_PickerFailure_ShowsNotice()
    {
        var vm = new LibraryViewModel(new FakeService(), new Dialogs(true), new Strings());

        vm.ReportPickerFailure("COMException: boom");

        Assert.True(vm.HasNotice);
        Assert.Equal("Library_NoticePickerFailed", vm.NoticeText);
    }

    [Fact]
    public void LibraryVm_DropFailureAndElevation_ShowNotices()
    {
        var vm = new LibraryViewModel(new FakeService(), new Dialogs(true), new Strings());

        vm.ReportDropFailure("COMException (HRESULT 0x80004005)");
        Assert.Equal("Library_NoticeDropFailed", vm.NoticeText);

        Assert.False(vm.HasElevationNotice);
        vm.SetElevated(true);
        Assert.True(vm.HasElevationNotice);
        Assert.Equal("Library_BannerElevated", vm.ElevationNotice);
        vm.SetElevated(false);
        Assert.False(vm.HasElevationNotice);
    }

    [Fact]
    public async Task LibraryService_PollAfterDispose_ReturnsNothingInsteadOfThrowing()
    {
        var (service, _, _) = Real();
        await service.AddAsync(TempCopy("notes.md"));
        service.Dispose();

        Assert.Empty(await service.PollAsync());
    }

    [Fact]
    public async Task LibraryVm_AllNoticesAndBanners_CanBeDismissed()
    {
        var service = new FakeService { WorkerProblem = "llama-server is missing" };
        var vm = new LibraryViewModel(service, new Dialogs(true), new Strings());
        await vm.LoadAsync();                                // sets the banner
        vm.ReportPickerFailure("boom");                      // a notice
        vm.SetElevated(true);                                // and the administrator warning
        Assert.True(vm.HasBanner && vm.HasNotice && vm.HasElevationNotice);

        vm.DismissBanner();
        vm.DismissNotice();
        vm.DismissElevationNotice();

        Assert.False(vm.HasBanner);
        Assert.False(vm.HasNotice);
        Assert.False(vm.HasElevationNotice);
    }

    // ---- tags (ST-25) ---------------------------------------------------------------------------------------

    [Fact]
    public async Task LibraryVm_EditTags_SavesTheAnswerAndShowsTheTagsOnTheRow()
    {
        var service = new FakeService();
        service.Books.AddRange([Entry(1, "Emma"), Entry(2, "Dracula")]);
        service.AllTags.Add("Novels");
        var dialogs = new Dialogs(true) { TagsAnswer = ["Novels", "Austen"] };
        var vm = new LibraryViewModel(service, dialogs, new Strings());
        await vm.LoadAsync();
        Assert.False(vm.Books[0].HasTags);

        await vm.EditTagsCommand.ExecuteAsync(vm.Books[0]);

        Assert.Equal(["Novels", "Austen"], service.TagsSet[1]);
        Assert.True(vm.Books[0].HasTags);
        Assert.Equal(["Novels", "Austen"], vm.Books[0].Tags);
        Assert.False(service.TagsSet.ContainsKey(2));
    }

    [Fact]
    public async Task LibraryVm_EditTagsCancelled_ChangesNothing()
    {
        var service = new FakeService();
        service.Books.Add(Entry(1, "Emma"));
        var vm = new LibraryViewModel(service, new Dialogs(true) { TagsAnswer = null }, new Strings());
        await vm.LoadAsync();

        await vm.EditTagsCommand.ExecuteAsync(vm.Books[0]);

        Assert.Empty(service.TagsSet);
    }

    [Fact]
    public async Task LibraryVm_AddTagsToSelected_NeedsASelectionAndTagsEveryTickedBook()
    {
        var service = new FakeService();
        service.Books.AddRange([Entry(1, "Emma"), Entry(2, "Dracula"), Entry(3, "Persuasion")]);
        var vm = new LibraryViewModel(service, new Dialogs(true) { TagsAnswer = ["classics"] }, new Strings());
        await vm.LoadAsync();
        Assert.False(vm.AddTagsToSelectedCommand.CanExecute(null));                    // nothing ticked

        vm.Books[0].IsTicked = true;
        vm.Books[2].IsTicked = true;
        Assert.True(vm.AddTagsToSelectedCommand.CanExecute(null));
        Assert.Equal(2, vm.SelectedCount);
        await vm.AddTagsToSelectedCommand.ExecuteAsync(null);

        Assert.All(vm.Books, b => Assert.False(b.IsTicked));                           // the books are unticked afterwards
        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.AddTagsToSelectedCommand.CanExecute(null));
        Assert.Equal([1L, 3L], service.TagsAdded.Single().Books);
        Assert.Equal(["classics"], vm.Books[0].Tags);
        Assert.Empty(vm.Books[1].Tags);
        Assert.Equal(["classics"], vm.Books[2].Tags);
    }

    [Fact]
    public async Task LibraryService_Tags_AreListedWithTheBooksAndCanBeSetAndAdded()
    {
        var (service, _, _) = Real();
        using var _s = service;
        var a = (await service.AddAsync(TempCopy("alice.epub"))).BookId;
        var b = (await service.AddAsync(TempCopy("pride.epub"))).BookId;

        await service.SetTagsAsync(a, ["Novels", "Austen"]);
        await service.AddTagsAsync([a, b], ["to read"]);

        var books = await service.ListAsync();
        Assert.Equal(["Austen", "Novels", "to read"], books.Single(x => x.Id == a).Tags);
        Assert.Equal(["to read"], books.Single(x => x.Id == b).Tags);
        Assert.Equal(["Austen", "Novels", "to read"], await service.ListTagsAsync());
        Assert.Equal(["to read"], (await service.GetAsync(b))!.Tags);
    }

    // ---- remove ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task LibraryVm_RemoveConfirmed_CallsPurge()
    {
        var service = new FakeService();
        service.Books.AddRange([Entry(1, "Keep"), Entry(2, "Drop")]);
        var dialogs = new Dialogs(true);
        var vm = new LibraryViewModel(service, dialogs, new Strings());
        await vm.LoadAsync();

        await vm.RemoveCommand.ExecuteAsync(vm.Books[1]);

        Assert.Equal(["Drop"], dialogs.Asked);
        Assert.Equal([2L], service.Removed);
        Assert.Equal(["Keep"], vm.Books.Select(b => b.Title));
    }

    [Fact]
    public async Task LibraryVm_RemoveCancelled_DoesNothing()
    {
        var service = new FakeService();
        service.Books.Add(Entry(1, "Keep"));
        var vm = new LibraryViewModel(service, new Dialogs(false), new Strings());
        await vm.LoadAsync();

        await vm.RemoveCommand.ExecuteAsync(vm.Books[0]);

        Assert.Empty(service.Removed);
        Assert.Single(vm.Books);
    }

    [Fact]
    public async Task LibraryService_Remove_DeletesChunksFullTextAndVectors()
    {
        var (service, _, _) = Real();
        using var _s = service;
        var added = await service.AddAsync(TempCopy("notes.md"));
        using var conn = Database.Open(Paths.DatabasePath);
        var ingest = new IngestService(conn, new FakeEmbedder(), new WordCountTokenizer(), new PunctuationSentenceSplitter());
        await new IngestWorker(conn, ingest).RunAsync();
        var chunks = new ChunkRepository(conn);
        Assert.True(chunks.CountChunks(added.BookId) > 0);
        Assert.True(chunks.CountVectors(added.BookId) > 0);

        await service.RemoveAsync(added.BookId);

        Assert.Equal(0, chunks.CountChunks());
        Assert.Equal(0, chunks.CountVectors());
        Assert.Equal(0, chunks.CountFts());
        Assert.Null(await service.GetAsync(added.BookId));
    }

    // ---- pause ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task LibraryVm_PauseAndResume_TogglesFlagAndRestartsWorker()
    {
        var service = new FakeService();
        service.Books.Add(Entry(1, "Queued", BookStatus.Queued, 0, IngestStage.Extract));
        var vm = new LibraryViewModel(service, new Dialogs(true), new Strings());
        await vm.LoadAsync();
        Assert.True(vm.ShowIndexingStrip);
        Assert.Equal("Library_Pause", vm.PauseLabel);
        var requests = service.WorkerRequests;

        await vm.TogglePauseCommand.ExecuteAsync(null);
        Assert.True(service.Paused);
        Assert.True(vm.IsPaused);
        Assert.Equal("Library_Resume", vm.PauseLabel);
        Assert.Equal(requests, service.WorkerRequests);                   // pausing does not start anything

        await vm.TogglePauseCommand.ExecuteAsync(null);
        Assert.False(service.Paused);
        Assert.Equal("Library_Pause", vm.PauseLabel);
        Assert.Equal(requests + 1, service.WorkerRequests);               // resuming makes sure a worker is running
    }

    [Fact]
    public async Task LibraryVm_WorkerGaveUp_ShowsBanner()
    {
        var service = new FakeService { WorkerGaveUp = true };
        var vm = new LibraryViewModel(service, new Dialogs(true), new Strings());

        await vm.PollAsync();

        Assert.Equal("Library_BannerWorkerStopped", vm.Banner);
    }

    // ---- worker setup -----------------------------------------------------------------------------------------

    [Fact]
    public void WorkerSetup_ChecksWorkerServerAndModelInOrder()
    {
        var manifest = ModelManifest.LoadDefault();
        var app = Path.Combine(_dir, "app");
        Directory.CreateDirectory(app);
        var setup = new WorkerSetup(Paths, manifest, new ModelStore(Paths.ModelsFolder), app, new Strings());

        Assert.Equal("Library_NoWorker", setup.CheckPrerequisites());
        File.WriteAllText(setup.WorkerExe, "");
        Assert.Equal("Library_NoServer", setup.CheckPrerequisites());

        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllText(Path.Combine(_dir, "bin", ServerLocator.FileName), "");
        Assert.Equal("Library_NoEmbeddingModel", setup.CheckPrerequisites());

        Directory.CreateDirectory(Paths.ModelsFolder);
        File.WriteAllText(new ModelStore(Paths.ModelsFolder).PathFor(manifest.Embedding), "");
        Assert.Null(setup.CheckPrerequisites());
    }

    [Fact]
    public void WorkerSetup_BuildsArgumentsTheWorkerUnderstands()
    {
        var manifest = ModelManifest.LoadDefault();
        var store = new ModelStore(Paths.ModelsFolder);
        var setup = new WorkerSetup(Paths, manifest, store, _dir, new Strings());

        var args = setup.BuildArguments("C:\\x\\llama-server.exe", 1234);

        Assert.Equal(["--data", _dir, "--server-exe", "C:\\x\\llama-server.exe", "--embedding-model", store.PathFor(manifest.Embedding), "--parent-pid", "1234"], args);
    }

    [Fact]
    public void ServerLocator_PrefersDataFolderThenAppFolder()
    {
        var app = Path.Combine(_dir, "app");
        Directory.CreateDirectory(Path.Combine(app, "bin"));
        Assert.Null(ServerLocator.Find(Paths, app));

        var inApp = Path.Combine(app, "bin", ServerLocator.FileName);
        File.WriteAllText(inApp, "");
        Assert.Equal(inApp, ServerLocator.Find(Paths, app));

        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        var inData = Path.Combine(_dir, "bin", ServerLocator.FileName);
        File.WriteAllText(inData, "");
        Assert.Equal(inData, ServerLocator.Find(Paths, app));
    }

    // ---- reading from the Library (ST-35) ---------------------------------------------------------------------

    private sealed class FakeFiles : BookSearch.App.Reader.IFileLauncher
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

    private static BookEntry EntryAt(long id, string title, string status, string path = "", ReadingPlace? place = null) =>
        new(id, title, "An Author", "en", status, null, status == BookStatus.Ready ? IngestStage.Done : IngestStage.Embed, 0.4, [], path, place);

    [Fact]
    public async Task LibraryVm_ReadyBook_OffersReadThenContinueWithItsPlace()
    {
        var service = new FakeService();
        service.Books.AddRange([EntryAt(1, "Emma", BookStatus.Ready), EntryAt(2, "Dracula", BookStatus.Ready, place: new ReadingPlace(11, 61))]);
        var vm = new LibraryViewModel(service, new Dialogs(true), new Strings());
        await vm.LoadAsync();

        Assert.Equal("Library_Read", vm.Books[0].PrimaryLabel);
        Assert.False(vm.Books[0].HasPlace);
        Assert.Equal("Library_Continue", vm.Books[1].PrimaryLabel);
        Assert.True(vm.Books[1].HasPlace);
        Assert.Equal("Library_Place", vm.Books[1].PlaceText);                          // "Section 12 of 61" through the string table
        Assert.True(vm.Books[0].CanRead);
        Assert.False(vm.Books[0].ShowOpenFile);
        Assert.Contains("Library_ActionContinue", vm.Books[1].AutomationName);        // what Enter does is said to a screen reader
    }

    [Theory]
    [InlineData(BookStatus.Queued)]
    [InlineData(BookStatus.Indexing)]
    [InlineData(BookStatus.Rejected)]
    [InlineData(BookStatus.Failed)]
    public async Task LibraryVm_BookNotReady_OffersOpenFileAndNotTheReader(string status)
    {
        var service = new FakeService();
        service.Books.Add(EntryAt(1, "Scan", status, path: "/books/scan.pdf"));
        var files = new FakeFiles();
        files.Existing.Add("/books/scan.pdf");
        var vm = new LibraryViewModel(service, new Dialogs(true), new Strings(), files);
        await vm.LoadAsync();
        var reads = new List<long>();
        vm.ReadRequested += reads.Add;

        Assert.Equal("Library_OpenFile", vm.Books[0].PrimaryLabel);
        Assert.True(vm.Books[0].ShowOpenFile);
        vm.OpenCommand.Execute(vm.Books[0]);

        Assert.Equal(["/books/scan.pdf"], files.Opened);                               // the original, in its default program
        Assert.Empty(reads);                                                           // not the Reader
        Assert.Null(vm.Books[0].Note);
    }

    [Fact]
    public async Task LibraryVm_OpeningAReadyBook_AsksTheShellToReadIt_AndLaunchesNothing()
    {
        var service = new FakeService();
        service.Books.Add(EntryAt(7, "Emma", BookStatus.Ready, path: "/books/emma.epub"));
        var files = new FakeFiles();
        var vm = new LibraryViewModel(service, new Dialogs(true), new Strings(), files);
        await vm.LoadAsync();
        var reads = new List<long>();
        vm.ReadRequested += reads.Add;

        vm.OpenCommand.Execute(vm.Books[0]);

        Assert.Equal([7L], reads);
        Assert.Empty(files.Opened);
    }

    [Fact]
    public async Task LibraryVm_OpenFile_WhenTheFileIsGoneOrCannotStart_SaysSoInTheRow()
    {
        var service = new FakeService();
        service.Books.AddRange([EntryAt(1, "Gone", BookStatus.Indexing, path: "/gone.pdf"), EntryAt(2, "NoApp", BookStatus.Rejected, path: "/x.xyz")]);
        var files = new FakeFiles();
        files.Existing.Add("/x.xyz");
        files.FailWith = new IOException("no program");
        var vm = new LibraryViewModel(service, new Dialogs(true), new Strings(), files);
        await vm.LoadAsync();

        vm.OpenCommand.Execute(vm.Books[0]);
        vm.OpenCommand.Execute(vm.Books[1]);

        Assert.Equal("Library_NoteFileMissing", vm.Books[0].Note);
        Assert.StartsWith("Library_NoteOpenFailed", vm.Books[1].Note);
        Assert.True(vm.Books[0].HasNote);
        Assert.Empty(files.Opened);
    }

    [Fact]
    public async Task LibraryVm_Ticking_NeverOpensAnything_AndTheRowNoteClearsOnTheNextOpen()
    {
        var service = new FakeService();
        service.Books.Add(EntryAt(1, "Emma", BookStatus.Ready));
        var vm = new LibraryViewModel(service, new Dialogs(true), new Strings(), new FakeFiles());
        await vm.LoadAsync();
        var reads = new List<long>();
        vm.ReadRequested += reads.Add;

        vm.Books[0].IsTicked = true;                                                   // the tick box
        vm.Books[0].Note = "old note";
        Assert.Empty(reads);
        Assert.Equal(1, vm.SelectedCount);

        vm.OpenCommand.Execute(vm.Books[0]);
        Assert.Null(vm.Books[0].Note);
        Assert.Equal([1L], reads);
    }

    [Fact]
    public async Task LibraryVm_ClearTicks_StartsAVisitWithNothingSelected()
    {
        var service = new FakeService();
        service.Books.AddRange([EntryAt(1, "A", BookStatus.Ready), EntryAt(2, "B", BookStatus.Ready)]);
        var vm = new LibraryViewModel(service, new Dialogs(true), new Strings());
        await vm.LoadAsync();
        vm.Books[0].IsTicked = true;
        vm.Books[1].IsTicked = true;

        vm.ClearTicks();

        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.AddTagsToSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task LibraryVm_RefreshPlaces_PicksUpWhereTheReaderStopped()
    {
        var service = new FakeService();
        service.Books.Add(EntryAt(1, "Emma", BookStatus.Ready));
        var vm = new LibraryViewModel(service, new Dialogs(true), new Strings());
        await vm.LoadAsync();
        Assert.Equal("Library_Read", vm.Books[0].PrimaryLabel);

        service.Places[1] = new ReadingPlace(2, 10);                                   // the Reader saved a place meanwhile
        await vm.RefreshPlacesAsync();

        Assert.Equal("Library_Continue", vm.Books[0].PrimaryLabel);
        Assert.True(vm.Books[0].HasPlace);
    }

    [Fact]
    public async Task LibraryService_ListsPlacesWithChapterCounts_ForBooksThatWereOpened()
    {
        var path = TempCopy("alice.epub");
        using var service = new LibraryService(Paths, new CountingLauncher(), () => null);
        var added = await service.AddAsync(path);
        using (var conn = Database.Open(Paths.DatabasePath))
        {
            var ingest = new IngestService(conn, new FakeEmbedder(), new WordCountTokenizer(), new PunctuationSentenceSplitter());
            await new IngestWorker(conn, ingest).RunAsync();
            Assert.Empty(await service.ListPlacesAsync());                             // never opened: no place
            new ReadingPositionRepository(conn).Save(added.BookId, 3, 5);
        }

        var places = await service.ListPlacesAsync();
        var entry = await service.GetAsync(added.BookId);

        var place = places[added.BookId];
        Assert.Equal(3, place.SectionPos);
        Assert.True(place.SectionCount > 3);
        Assert.Equal(place, entry!.Place);
        Assert.Equal(path, entry.FilePath);                                            // what Open file launches
    }

    [Fact]
    public async Task LibraryVm_RemovingATickedBook_UpdatesTheSelection()
    {
        var service = new FakeService();
        service.Books.AddRange([EntryAt(1, "A", BookStatus.Ready), EntryAt(2, "B", BookStatus.Ready)]);
        var vm = new LibraryViewModel(service, new Dialogs(true), new Strings());
        await vm.LoadAsync();
        vm.Books[0].IsTicked = true;
        Assert.True(vm.AddTagsToSelectedCommand.CanExecute(null));

        await vm.RemoveCommand.ExecuteAsync(vm.Books[0]);

        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.AddTagsToSelectedCommand.CanExecute(null));                    // nothing is ticked any more
    }

    [Fact]
    public async Task LibraryService_AClampedPlace_AndABookWithNoChapters_HaveSensiblePlaces()
    {
        var path = TempCopy("alice.epub");
        using var service = new LibraryService(Paths, new CountingLauncher(), () => null);
        var added = await service.AddAsync(path);
        var bare = await service.AddAsync(TempCopy("arabic.txt"));                     // added but never indexed: no chunks at all
        using (var conn = Database.Open(Paths.DatabasePath))
        {
            var ingest = new IngestService(conn, new FakeEmbedder(), new WordCountTokenizer(), new PunctuationSentenceSplitter());
            await ingest.IngestAsync(added.BookId);
            var positions = new ReadingPositionRepository(conn);
            positions.Save(added.BookId, 9_999, 0);                                    // more than the book has
            positions.Save(bare.BookId, 4, 0);
        }

        var places = await service.ListPlacesAsync();

        var place = places[added.BookId];
        Assert.Equal(place.SectionCount - 1, place.SectionPos);                        // clamped to the last chapter
        Assert.DoesNotContain(bare.BookId, places.Keys);                               // no chapters: no "Section 5 of 0"
        Assert.Null((await service.GetAsync(bare.BookId))!.Place);
        Assert.Equal(place, (await service.GetAsync(added.BookId))!.Place);            // the per-row lookup agrees with the list
    }

    [Fact]
    public async Task LibraryVm_OnlyABookInProgressGetsTheAccentButton_AUnopenedOneJustSaysRead()
    {
        var service = new FakeService();
        service.Books.AddRange([EntryAt(1, "Emma", BookStatus.Ready), EntryAt(2, "Dracula", BookStatus.Ready, place: new ReadingPlace(3, 40)),
            EntryAt(3, "Scan", BookStatus.Rejected)]);
        var vm = new LibraryViewModel(service, new Dialogs(true), new Strings());
        await vm.LoadAsync();

        Assert.True(vm.Books[0].ShowRead);                                             // never opened: a standard button
        Assert.False(vm.Books[0].ShowContinue);
        Assert.True(vm.Books[1].ShowContinue);                                         // in progress: the accent
        Assert.False(vm.Books[1].ShowRead);
        Assert.True(vm.Books[2].ShowOpenFile);                                         // not ready: Open file
        Assert.False(vm.Books[2].ShowRead);
        Assert.False(vm.Books[2].ShowContinue);

        var changed = new List<string?>();
        vm.Books[0].PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        service.Places[1] = new ReadingPlace(0, 10);                                   // the Reader saved a place meanwhile
        await vm.RefreshPlacesAsync();

        Assert.True(vm.Books[0].ShowContinue);                                         // the button swaps
        Assert.False(vm.Books[0].ShowRead);
        Assert.Contains(nameof(BookRow.ShowContinue), changed);
    }

    // ---- the quiet Library: find, selection mode, the indexing strip, rows --------------------------------------

    private static async Task<(LibraryViewModel Vm, FakeService Service)> Quiet(params BookEntry[] books)
    {
        var service = new FakeService();
        service.Books.AddRange(books);
        var vm = new LibraryViewModel(service, new Dialogs(true) { TagsAnswer = ["classics"] }, new Strings());
        await vm.LoadAsync();
        return (vm, service);
    }

    [Fact]
    public async Task LibraryVm_Find_ShowsOnlyMatchingBooks_AndKeepsTicksOnTheOnesItHides()
    {
        var (vm, _) = await Quiet(Entry(1, "Pride and Prejudice"), Entry(2, "Emma"), Entry(3, "Dracula"));
        vm.SelectCommand.Execute(null);
        vm.Books[0].IsTicked = true;

        vm.FindText = "emm";

        Assert.Equal(["Emma"], vm.Shown.Select(b => b.Title));
        Assert.Equal(3, vm.Books.Count);                                               // the library itself is not filtered
        Assert.Equal(1, vm.SelectedCount);                                             // the hidden, ticked book still counts
        vm.Shown[0].IsTicked = true;
        Assert.Equal(2, vm.SelectedCount);

        vm.FindText = "an author";                                                     // matches the byline too
        Assert.Equal(3, vm.Shown.Count);
        vm.FindText = "zzz";
        Assert.True(vm.NothingFound);
        Assert.Equal("Library_NoMatch", vm.NoMatchText);
        vm.FindText = "";
        Assert.Equal(3, vm.Shown.Count);
        Assert.False(vm.NothingFound);
    }

    [Fact]
    public async Task LibraryVm_Find_FollowsATitleThatAppearsAfterTheFileIsRead()
    {
        var (vm, service) = await Quiet(Entry(1, "scan.pdf", BookStatus.Indexing, 0.2, IngestStage.Extract));
        vm.FindText = "origin";
        Assert.Empty(vm.Shown);

        service.Books[0] = Entry(1, "On the Origin of Species", BookStatus.Indexing, 0.4, IngestStage.Embed);
        await vm.ApplyChangesAsync([new BookProgress(1, BookStatus.Queued, IngestStage.Extract, 0.2, null)]);   // the stage changes: the row is reloaded
        await vm.ApplyChangesAsync([new BookProgress(1, BookStatus.Indexing, IngestStage.Embed, 0.4, null)]);

        Assert.Equal(["On the Origin of Species"], vm.Shown.Select(b => b.Title));
    }

    [Fact]
    public async Task LibraryVm_SelectionMode_TickBoxesOnlyWhileChoosing_AClickTicks_AndCancelClears()
    {
        var (vm, _) = await Quiet(Entry(1, "Emma"), Entry(2, "Dracula"));
        var reads = new List<long>();
        vm.ReadRequested += reads.Add;
        Assert.False(vm.IsSelecting);
        Assert.All(vm.Books, b => { Assert.False(b.IsSelecting); Assert.True(b.ShowButtons); });

        vm.SelectCommand.Execute(null);
        Assert.True(vm.IsSelecting);
        Assert.All(vm.Books, b => { Assert.True(b.IsSelecting); Assert.False(b.ShowButtons); Assert.False(b.ShowReadButton); });

        vm.OpenCommand.Execute(vm.Books[0]);                                           // a click on a row ticks it, never opens it
        Assert.True(vm.Books[0].IsTicked);
        Assert.Empty(reads);
        Assert.Equal("Library_AddTagsToOne", vm.AddTagsLabel);
        Assert.Equal("Library_SelectedCount", vm.SelectionText);
        vm.OpenCommand.Execute(vm.Books[1]);
        Assert.Equal("Library_AddTagsToMany", vm.AddTagsLabel);

        vm.CancelSelectionCommand.Execute(null);
        Assert.False(vm.IsSelecting);
        Assert.Equal(0, vm.SelectedCount);
        Assert.All(vm.Books, b => Assert.True(b.ShowButtons));
    }

    [Fact]
    public async Task LibraryVm_AddingTags_LeavesSelectionMode()
    {
        var (vm, service) = await Quiet(Entry(1, "Emma"), Entry(2, "Dracula"));
        vm.SelectCommand.Execute(null);
        vm.Books[1].IsTicked = true;

        await vm.AddTagsToSelectedCommand.ExecuteAsync(null);

        Assert.Equal([2L], service.TagsAdded.Single().Books);
        Assert.False(vm.IsSelecting);                                                  // done: the tick boxes go away
        Assert.False(vm.Books[1].IsTicked);
    }

    [Fact]
    public async Task LibraryVm_SelectIsOnlyForALibraryWithBooks_AndANewRowStartsInTheCurrentMode()
    {
        var (vm, service) = await Quiet();
        Assert.False(vm.SelectCommand.CanExecute(null));                               // nothing to select

        service.Books.Add(Entry(1, "Emma"));
        await vm.LoadAsync();                                                          // a book appears
        Assert.True(vm.SelectCommand.CanExecute(null));
        Assert.True(vm.ShowBrowseCommands);
        vm.SelectCommand.Execute(null);
        Assert.False(vm.ShowBrowseCommands);                                           // Add books and Select give way to the selection commands
    }

    [Fact]
    public async Task LibraryVm_IndexingStrip_ShowsWhileWorkRemains_WithOneOverallProgress_AndPause()
    {
        var (vm, _) = await Quiet(Entry(1, "Done"), Entry(2, "Half", BookStatus.Indexing, 0.5, IngestStage.Embed),
            Entry(3, "Waiting", BookStatus.Queued, 0, IngestStage.Extract));

        Assert.True(vm.ShowIndexingStrip);
        Assert.Equal("Library_Indexing", vm.IndexingText);                             // "Indexing 2 books"
        Assert.Equal(25, vm.IndexingPercent);                                          // (50 + 0) / 2

        await vm.TogglePauseCommand.ExecuteAsync(null);
        Assert.Equal("Library_IndexingPaused", vm.IndexingText);
        Assert.Equal("Library_Resume", vm.PauseLabel);
        Assert.True(vm.ShowIndexingStrip);                                             // still there while paused: Resume lives in it

        var (finished, _) = await Quiet(Entry(1, "Done"));
        Assert.False(finished.ShowIndexingStrip);                                      // nothing to index: no strip
    }

    [Fact]
    public async Task LibraryVm_RowShowsMetaTextTagsAndMenuName_AndNoStatusWhenReady()
    {
        var (vm, _) = await Quiet(EntryAt(1, "Emma", BookStatus.Ready, place: new ReadingPlace(11, 61)) with { Tags = ["novels", "1800s"] });
        var row = vm.Books[0];

        Assert.Equal("An Author · EN · Library_Place", row.MetaText);                  // author, language, then where the reader stopped
        Assert.Equal(["novels", "1800s"], row.Tags);
        Assert.True(row.HasTags);
        Assert.False(row.ShowStatus);
        Assert.Equal("Library_MoreName", row.MenuName);
    }

    // ---- review fixes for the quiet Library ----------------------------------------------------------------------

    [Fact]
    public async Task LibraryVm_RemovingABook_ChangesTheShownListByOneRemove_NotAReset()
    {
        var (vm, _) = await Quiet(Entry(1, "Emma"), Entry(2, "Dracula"), Entry(3, "Persuasion"));
        var changes = new List<NotifyCollectionChangedEventArgs>();
        vm.Shown.CollectionChanged += (_, e) => changes.Add(e);

        await vm.RemoveCommand.ExecuteAsync(vm.Books[1]);

        var change = Assert.Single(changes);                                           // so the list keeps focus and scroll
        Assert.Equal(NotifyCollectionChangedAction.Remove, change.Action);
        Assert.Equal(["Emma", "Persuasion"], vm.Shown.Select(b => b.Title));
    }

    [Fact]
    public async Task LibraryVm_RemovingATickedBook_InSelectionMode_UpdatesTheCountTexts()
    {
        var (vm, _) = await Quiet(Entry(1, "Emma"), Entry(2, "Dracula"));
        vm.SelectCommand.Execute(null);
        vm.Books[0].IsTicked = true;
        vm.Books[1].IsTicked = true;
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await vm.RemoveCommand.ExecuteAsync(vm.Books[0]);

        Assert.Contains(nameof(LibraryViewModel.SelectionText), raised);
        Assert.Contains(nameof(LibraryViewModel.AddTagsLabel), raised);
        Assert.Equal(1, vm.SelectedCount);
    }

    [Fact]
    public async Task LibraryVm_ARowAddedWhileSelecting_StartsInSelectionMode()
    {
        var (vm, service) = await Quiet(Entry(1, "Emma"));
        vm.SelectCommand.Execute(null);

        service.Books.Add(Entry(2, "Dracula"));
        service.Books.RemoveAt(0);                                                     // the stand-in lists only the new book now
        await vm.LoadAsync();

        var added = vm.Books.Single(b => b.Title == "Dracula");
        Assert.True(added.IsSelecting);
        Assert.False(added.ShowButtons);
    }

    [Fact]
    public async Task LibraryVm_InSelectionMode_AClickOnANotReadyRow_TicksItAndLaunchesNothing()
    {
        var service = new FakeService();
        service.Books.Add(EntryAt(1, "Scan", BookStatus.Rejected, path: "/scan.pdf"));
        var files = new FakeFiles();
        files.Existing.Add("/scan.pdf");
        var vm = new LibraryViewModel(service, new Dialogs(true), new Strings(), files);
        await vm.LoadAsync();
        vm.SelectCommand.Execute(null);

        vm.OpenCommand.Execute(vm.Books[0]);

        Assert.True(vm.Books[0].IsTicked);
        Assert.Empty(files.Opened);
    }

    [Fact]
    public async Task LibraryVm_ARowTellsAScreenReaderWhatEnterDoes_InSelectionMode_AndWhetherItIsSelected()
    {
        var (vm, _) = await Quiet(Entry(1, "Emma"));
        var row = vm.Books[0];
        Assert.Contains("Library_ActionRead", row.AutomationName);
        Assert.NotEqual("", row.PrimaryTooltip);

        vm.SelectCommand.Execute(null);
        Assert.Contains("Library_ActionTick", row.AutomationName);
        Assert.DoesNotContain("Library_ActionRead", row.AutomationName);
        Assert.Equal("", row.PrimaryTooltip);                                          // no "Read this book" tooltip while choosing

        row.IsTicked = true;
        Assert.Contains("Library_RowSelected", row.AutomationName);
        Assert.Contains("Library_ActionUntick", row.AutomationName);
    }

    [Fact]
    public async Task LibraryVm_OverallProgress_NeverMovesBackwardsWhenABookFinishes()
    {
        var (vm, service) = await Quiet(Entry(1, "A", BookStatus.Indexing, 0.9, IngestStage.Embed), Entry(2, "B", BookStatus.Queued, 0, IngestStage.Extract));
        var before = vm.IndexingPercent;                                               // (90 + 0) / 2
        service.Books[0] = Entry(1, "A");                                              // what the library will report once it is done
        service.Books[1] = Entry(2, "B");
        Assert.Equal(45, before);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        service.Books[1] = Entry(2, "B", BookStatus.Queued, 0, IngestStage.Extract);   // B is still waiting
        await vm.ApplyChangesAsync([new BookProgress(1, BookStatus.Ready, IngestStage.Done, 1, null)]);   // A finishes

        Assert.True(vm.IndexingPercent >= before, $"{vm.IndexingPercent} < {before}");
        Assert.Equal(50, vm.IndexingPercent);                                          // (100 + 0) / 2
        Assert.Contains(nameof(LibraryViewModel.IndexingPercent), raised);             // polling raises it
        Assert.Contains(nameof(LibraryViewModel.IndexingText), raised);

        service.Books[1] = Entry(2, "B");
        await vm.ApplyChangesAsync([new BookProgress(2, BookStatus.Ready, IngestStage.Done, 1, null)]);   // B finishes: nothing left
        Assert.False(vm.ShowIndexingStrip);
        Assert.Equal(0, vm.IndexingPercent);                                           // the next batch starts from nothing
    }

    [Fact]
    public async Task LibraryVm_Find_MatchesTitleAndAuthor_NotTheLanguageCode()
    {
        var service = new FakeService();
        service.Books.AddRange([new BookEntry(1, "Emma", "Jane Austen", "en", BookStatus.Ready, null, IngestStage.Done, 1),
            new BookEntry(2, "Dracula", "Bram Stoker", "de", BookStatus.Ready, null, IngestStage.Done, 1)]);
        var vm = new LibraryViewModel(service, new Dialogs(true), new Strings());
        await vm.LoadAsync();

        vm.FindText = "austen";
        Assert.Equal(["Emma"], vm.Shown.Select(b => b.Title));                         // author
        vm.FindText = "drac";
        Assert.Equal(["Dracula"], vm.Shown.Select(b => b.Title));                      // title
        vm.FindText = "de";
        Assert.Empty(vm.Shown);                                                        // the "DE" language code is not searched
    }

    [Fact]
    public async Task LibraryVm_PausedWithNothingWaiting_SaysSo_NotZeroWaiting()
    {
        var (vm, service) = await Quiet(Entry(1, "Done"));
        service.Paused = true;
        await vm.LoadAsync();                                                          // reads the pause flag

        Assert.True(vm.ShowIndexingStrip);
        Assert.Equal("Library_IndexingPausedIdle", vm.IndexingText);
    }
}
