using System.Collections.ObjectModel;
using BookSearch.App.Reader;
using BookSearch.Core.Data;
using BookSearch.Core.Worker;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BookSearch.App;

/// <summary>One row of the book list. Only the row that changed raises change notifications.</summary>
public sealed partial class BookRow : ObservableObject
{
    private readonly IStrings _strings;

    public BookRow(BookEntry entry, IStrings strings)
    {
        _strings = strings;
        Id = entry.Id;
        Apply(entry);
    }

    public long Id { get; }

    /// <summary>The original file; a book that is not ready opens it in its default program.</summary>
    public string FilePath { get; private set; } = "";

    /// <summary>Ticked for "Add tags to selected…". Ticking never opens the book.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AutomationName))] private bool _isTicked;

    /// <summary>The note under the row when the original file could not be opened.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasNote))] private string? _note;

    public bool HasNote => !string.IsNullOrEmpty(Note);

    /// <summary>Where the reader stopped, once the book has been opened from the Library; null before that.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PlaceText), nameof(MetaText), nameof(HasPlace), nameof(ShowContinue), nameof(ShowRead), nameof(ShowContinueButton), nameof(ShowReadButton), nameof(PrimaryLabel), nameof(PrimaryTooltip), nameof(PrimaryName), nameof(AutomationName))]
    private ReadingPlace? _place;

    public bool CanRead => Status == BookStatus.Ready;

    /// <summary>The accent button: a book already in progress (UX design issue 14: a list full of accent buttons has none).</summary>
    public bool ShowContinue => CanRead && Place is not null;

    /// <summary>A ready book that was never opened: a standard button.</summary>
    public bool ShowRead => CanRead && Place is null;
    public bool ShowOpenFile => !CanRead;
    public bool HasPlace => CanRead && Place is not null;
    public string PlaceText => HasPlace ? _strings.Format("Library_Place", Place!.SectionPos + 1, Place.SectionCount) : "";

    /// <summary>The line under the title: author and language, then where the reader stopped.</summary>
    public string MetaText => string.Join(" · ", new[] { Byline, PlaceText }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>A "Ready" line is noise on most rows, so the status shows only when something needs attention (UX design, quiet rows).</summary>
    public bool ShowStatus => Status != BookStatus.Ready;

    /// <summary>The page is in selection mode: the tick box shows and the row's buttons and menu are hidden.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowButtons), nameof(ShowContinueButton), nameof(ShowReadButton), nameof(ShowOpenFileButton), nameof(AutomationName), nameof(PrimaryTooltip))] private bool _isSelecting;

    public bool ShowButtons => !IsSelecting;

    // The row's main button, one of three, and never while the person is choosing books.
    public bool ShowContinueButton => ShowContinue && ShowButtons;
    public bool ShowReadButton => ShowRead && ShowButtons;
    public bool ShowOpenFileButton => ShowOpenFile && ShowButtons;

    /// <summary>The row's main action: Read, Continue (a place is saved) or, for a book that is not ready, Open file.</summary>
    public string PrimaryLabel => _strings.Get(!CanRead ? "Library_OpenFile" : Place is null ? "Library_Read" : "Library_Continue");

    public string PrimaryTooltip => IsSelecting ? "" : _strings.Get(!CanRead ? "Library_TooltipOpenFile" : Place is null ? "Library_TooltipRead" : "Library_TooltipContinue");

    /// <summary>One sentence for a screen reader: the book, author, status and place, and what Enter does.</summary>
    public string AutomationName
    {
        get
        {
            var facts = string.Join(", ", new[] { Title, Byline, StatusText, PlaceText }.Where(s => !string.IsNullOrWhiteSpace(s)));
            // In selection mode Enter ticks the row, so that is what is said (and whether it is ticked already).
            var action = IsSelecting ? _strings.Get(IsTicked ? "Library_ActionUntick" : "Library_ActionTick")
                : _strings.Get(!CanRead ? "Library_ActionOpenFile" : Place is null ? "Library_ActionRead" : "Library_ActionContinue");
            return IsSelecting && IsTicked ? $"{facts}, {_strings.Get("Library_RowSelected")}. {action}" : $"{facts}. {action}";
        }
    }

    public string SelectName => _strings.Format("Library_SelectBook", Title);

    /// <summary>Names for the row's buttons, so a screen reader does not say "Read, Read, Read" down the list.</summary>
    public string PrimaryName => _strings.Format(!CanRead ? "Library_OpenFileName" : Place is null ? "Library_ReadName" : "Library_ContinueName", Title);

    /// <summary>The name of the row's "⋯" button.</summary>
    public string MenuName => _strings.Format("Library_MoreName", Title);

    /// <summary>What UI Automation reads for the list item when the item is data-bound.</summary>
    public override string ToString() => AutomationName;

    [ObservableProperty] private string _title = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MetaText))] private string _byline = "";

    /// <summary>The author alone (the find box matches title and author, not the language code in <see cref="Byline"/>).</summary>
    public string Author { get; private set; } = "";
    [ObservableProperty] private string _status = BookStatus.Queued;
    [ObservableProperty] private double _percent;          // 0-100 for a ProgressBar
    [ObservableProperty] private string? _reason;
    [ObservableProperty] private string _stage = IngestStage.Extract;

    /// <summary>The book's tags as the user typed them.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasTags))] private IReadOnlyList<string> _tags = [];

    public bool HasTags => Tags.Count > 0;

    public string StatusText => Status switch
    {
        BookStatus.Queued => _strings.Get("Library_StatusQueued"),
        BookStatus.Indexing when Percent <= 0 && Stage == IngestStage.Extract => _strings.Get("Library_StatusReading"),
        BookStatus.Indexing => _strings.Format("Library_StatusIndexing", Percent),
        BookStatus.Ready => _strings.Get("Library_StatusReady"),
        BookStatus.Rejected => _strings.Get("Library_StatusRejected"),
        BookStatus.Failed => _strings.Get("Library_StatusFailed"),
        _ => Status,
    };

    public bool ShowReason => !string.IsNullOrEmpty(Reason) && Status is BookStatus.Rejected or BookStatus.Failed;

    partial void OnStatusChanged(string value) => RaiseDerived();
    partial void OnTitleChanged(string value)
    {
        foreach (var name in new[] { nameof(SelectName), nameof(PrimaryName), nameof(MenuName), nameof(AutomationName) })
            OnPropertyChanged(name);
    }
    partial void OnPercentChanged(double value) => OnPropertyChanged(nameof(StatusText));
    partial void OnStageChanged(string value) => OnPropertyChanged(nameof(StatusText));
    partial void OnReasonChanged(string? value) => OnPropertyChanged(nameof(ShowReason));

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ShowStatus));
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(ShowReason));
        OnPropertyChanged(nameof(CanRead));
        OnPropertyChanged(nameof(ShowContinue));
        OnPropertyChanged(nameof(ShowRead));
        OnPropertyChanged(nameof(ShowContinueButton));
        OnPropertyChanged(nameof(ShowReadButton));
        OnPropertyChanged(nameof(ShowOpenFileButton));
        OnPropertyChanged(nameof(ShowOpenFile));
        OnPropertyChanged(nameof(HasPlace));
        OnPropertyChanged(nameof(PlaceText));
        OnPropertyChanged(nameof(PrimaryLabel));
        OnPropertyChanged(nameof(PrimaryTooltip));
        OnPropertyChanged(nameof(PrimaryName));
        OnPropertyChanged(nameof(AutomationName));
    }

    /// <summary>Takes everything from a freshly loaded book (title and author appear after extraction).</summary>
    public void Apply(BookEntry e)
    {
        Title = e.Title;
        Author = e.Author ?? "";
        Byline = string.Join(" · ", new[] { e.Author, e.Language?.ToUpperInvariant() }.Where(s => !string.IsNullOrWhiteSpace(s)));
        Reason = e.Reason;
        Stage = e.Stage;
        Percent = e.Progress * 100;
        Status = e.Status;
        Tags = e.Tags ?? [];
        FilePath = e.FilePath;
        Place = e.Place;
    }

    /// <summary>Takes just the progress fields from a poll.</summary>
    public void Apply(BookProgress p)
    {
        Reason = p.Error ?? Reason;
        Stage = p.Stage;
        Percent = p.Progress * 100;
        Status = p.Status;
    }
}

/// <summary>The Library page: the book list, adding and removing books, and pausing indexing.</summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    public static readonly string[] SupportedExtensions = [".epub", ".pdf", ".txt", ".md"];

    private readonly ILibraryService _service;
    private readonly IDialogs _dialogs;
    private readonly IStrings _strings;
    private readonly Dictionary<long, BookRow> _rows = [];
    private int _polling;
    private bool _loadingRows;

    private readonly IFileLauncher? _files;

    /// <param name="files">Opens a book's original file in its default program (for a book that is not ready); without one, that does nothing.</param>
    public LibraryViewModel(ILibraryService service, IDialogs dialogs, IStrings strings, IFileLauncher? files = null)
    {
        _service = service;
        _dialogs = dialogs;
        _strings = strings;
        _files = files;
        Books.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasBooks));
            OnPropertyChanged(nameof(ShowBrowseCommands));
            SelectCommand.NotifyCanExecuteChanged();
            if (_loadingRows) return;
            RaiseIndexing();
            ApplyFind();
        };
    }

    /// <summary>Every book, in the order the library lists them.</summary>
    public ObservableCollection<BookRow> Books { get; } = [];

    /// <summary>The books the list shows: all of them, or those whose title or author contain <see cref="FindText"/>.</summary>
    public ObservableCollection<BookRow> Shown { get; } = [];

    /// <summary>The "Find a book" box. Ticks are kept on books it hides.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NothingFound), nameof(NoMatchText))] private string _findText = "";

    partial void OnFindTextChanged(string value) => ApplyFind();

    /// <summary>The find box matched no book.</summary>
    public bool NothingFound => !IsEmpty && Shown.Count == 0;

    public string NoMatchText => NothingFound ? _strings.Format("Library_NoMatch", FindText.Trim()) : "";

    private void ApplyFind()
    {
        var term = FindText.Trim();
        var wanted = Books.Where(b => term.Length == 0
            || b.Title.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || b.Author.Contains(term, StringComparison.CurrentCultureIgnoreCase)).ToList();
        // Bring Shown to the wanted list with the fewest changes, so the list keeps its focus and scroll when one book is
        // added or removed, and loading hundreds of books is not a reset for each of them.
        var keep = wanted.ToHashSet();
        for (var i = Shown.Count - 1; i >= 0; i--)
            if (!keep.Contains(Shown[i])) Shown.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < Shown.Count && ReferenceEquals(Shown[i], wanted[i])) continue;
            var at = Shown.IndexOf(wanted[i]);
            if (at > i) Shown.Move(at, i);
            else Shown.Insert(i, wanted[i]);
        }
        OnPropertyChanged(nameof(NothingFound));
        OnPropertyChanged(nameof(NoMatchText));
    }
    public bool IsEmpty => Books.Count == 0;

    /// <summary>The command bar's Add books item shows once there are books; while empty the accent button of the empty state is the one.</summary>
    public bool HasBooks => Books.Count > 0;

    public bool HasActiveJobs => Books.Any(b => b.Status is BookStatus.Queued or BookStatus.Indexing);

    // ---- the indexing strip: one place for what is happening, instead of a bar on every row ----------------------

    /// <summary>The strip above the list shows while books are waiting or being indexed, or indexing is paused.</summary>
    public bool ShowIndexingStrip => HasActiveJobs || IsPaused;

    private int ActiveCount => Books.Count(b => b.Status is BookStatus.Queued or BookStatus.Indexing);

    /// <summary>"Indexing 2 books", or "Indexing paused: 2 books waiting".</summary>
    public string IndexingText => IsPaused ? (ActiveCount == 0 ? _strings.Get("Library_IndexingPausedIdle") : _strings.Format("Library_IndexingPaused", ActiveCount))
        : ActiveCount == 1 ? _strings.Get("Library_IndexingOne") : _strings.Format("Library_Indexing", ActiveCount);

    /// <summary>The overall progress, 0 to 100: the average of the books waiting or being indexed (a waiting book counts 0).</summary>
    public double IndexingPercent
    {
        get
        {
            // Books that finished since the strip appeared count as 100, so the bar never moves backwards when one completes.
            var active = Books.Where(b => b.Status is BookStatus.Queued or BookStatus.Indexing).ToList();
            var total = active.Count + _batchFinished;
            return total == 0 ? 0 : (_batchFinished * 100 + active.Sum(b => b.Status == BookStatus.Indexing ? b.Percent : 0)) / total;
        }
    }

    private int _batchFinished;    // books that stopped being queued or indexing since the strip appeared

    public string PauseLabel => _strings.Get(IsPaused ? "Library_Resume" : "Library_Pause");

    private void RaiseIndexing()
    {
        if (!HasActiveJobs && !IsPaused) _batchFinished = 0;       // the strip is gone: the next batch starts from nothing
        OnPropertyChanged(nameof(HasActiveJobs));
        OnPropertyChanged(nameof(ShowIndexingStrip));
        OnPropertyChanged(nameof(IndexingText));
        OnPropertyChanged(nameof(IndexingPercent));
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasNotice))] private string? _noticeText;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasBanner))] private string? _banner;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PauseLabel), nameof(ShowIndexingStrip), nameof(IndexingText))] private bool _isPaused;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasElevationNotice))] private string? _elevationNotice;

    public bool HasElevationNotice => !string.IsNullOrEmpty(ElevationNotice);
    public bool HasNotice => !string.IsNullOrEmpty(NoticeText);
    public bool HasBanner => !string.IsNullOrEmpty(Banner);

    /// <summary>Loads the list and starts the worker if books were left unfinished (after a restart, say).</summary>
    public async Task LoadAsync()
    {
        _loadingRows = true;                               // add the rows without refreshing the list for each one
        try { foreach (var entry in await _service.ListAsync()) AddRow(entry); }
        finally { _loadingRows = false; }
        ApplyFind();
        RaiseIndexing();
        IsPaused = await _service.GetPausedAsync();
        await StartWorkerAsync();
    }

    // ---- add ------------------------------------------------------------------------------------------------

    /// <summary>Queues files from the picker or a drop. Rows appear as Queued at once; the worker does the indexing.</summary>
    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var duplicates = new List<string>();
        var unsupported = new List<string>();
        var unreadable = new List<string>();
        var added = 0;

        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            if (!SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())) { unsupported.Add(name); continue; }
            try
            {
                var result = await _service.AddAsync(path);
                if (result.Duplicate) { duplicates.Add(name); continue; }
                if (_rows.TryGetValue(result.BookId, out var existing))   // a failed book put back in the queue
                {
                    if (await _service.GetAsync(result.BookId) is { } fresh) existing.Apply(fresh);
                }
                else if (await _service.GetAsync(result.BookId) is { } entry) AddRow(entry);
                added++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                unreadable.Add(name);
            }
        }

        var notes = new List<string>();
        if (duplicates.Count > 0)
            notes.Add(duplicates.Count == 1
                ? _strings.Format("Library_NoticeDuplicate", duplicates[0])
                : _strings.Format("Library_NoticeDuplicates", duplicates.Count));
        if (unsupported.Count > 0) notes.Add(_strings.Format("Library_NoticeUnsupported", string.Join(", ", unsupported)));
        if (unreadable.Count > 0) notes.Add(_strings.Format("Library_NoticeUnreadable", string.Join(", ", unreadable)));
        NoticeText = notes.Count == 0 ? null : string.Join(" ", notes);

        RaiseIndexing();
        if (added > 0) await StartWorkerAsync();
    }

    // The notices and banners at the top of the page can all be closed by the user.
    public void DismissNotice() => NoticeText = null;
    public void DismissBanner() => Banner = null;
    public void DismissElevationNotice() => ElevationNotice = null;

    /// <summary>The file picker could not be opened or failed; tell the user instead of doing nothing.</summary>
    public void ReportPickerFailure(string detail) => NoticeText = _strings.Format("Library_NoticePickerFailed", detail);

    /// <summary>Dropped files could not be read (for example the drop was refused by Windows).</summary>
    public void ReportDropFailure(string detail) => NoticeText = _strings.Format("Library_NoticeDropFailed", detail);

    /// <summary>
    /// An elevated app can't receive files dragged from a normal Explorer window (Windows blocks it), so say so up front
    /// rather than leaving the user to wonder why a drop does nothing.
    /// </summary>
    public void SetElevated(bool elevated) =>
        ElevationNotice = elevated ? _strings.Get("Library_BannerElevated") : null;

    // ---- reading (DCR-10, ST-35) ----------------------------------------------------------------------------

    /// <summary>The shell opens this book in the Reader, at the place the reader stopped.</summary>
    public event Action<long>? ReadRequested;

    /// <summary>
    /// The row's main action. A ready book is read in the Reader; any other (queued, indexing, rejected, failed) opens its
    /// original file in the program Windows uses for it, and says so in the row if that fails.
    /// </summary>
    [RelayCommand]
    private void Open(BookRow? row)
    {
        if (row is null) return;
        if (IsSelecting)                                   // in selection mode a click ticks the row; it never opens
        {
            row.IsTicked = !row.IsTicked;
            return;
        }
        row.Note = null;
        if (row.CanRead)
        {
            ReadRequested?.Invoke(row.Id);
            return;
        }
        if (_files is null) return;
        if (!_files.Exists(row.FilePath))
        {
            row.Note = _strings.Get("Library_NoteFileMissing");
            return;
        }
        try { _files.Open(row.FilePath); }
        catch (IOException e) { row.Note = _strings.Format("Library_NoteOpenFailed", e.Message); }
    }

    /// <summary>Re-reads where the reader stopped in each book (the Reader saves it while the page is away).</summary>
    public async Task RefreshPlacesAsync()
    {
        var places = await _service.ListPlacesAsync();
        foreach (var row in Books) row.Place = places.TryGetValue(row.Id, out var place) ? place : null;
    }

    // ---- remove ---------------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task RemoveAsync(BookRow? row)
    {
        if (row is null || !await _dialogs.ConfirmRemoveAsync(row.Title)) return;
        Books.Remove(row);          // the row goes at once; the purge runs in the background
        row.PropertyChanged -= OnRowChanged;
        _rows.Remove(row.Id);
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(AddTagsLabel));
        AddTagsToSelectedCommand.NotifyCanExecuteChanged();
        await _service.RemoveAsync(row.Id);
    }

    // ---- tags (ST-25) ---------------------------------------------------------------------------------------

    /// <summary>How many rows are ticked, for "Add tags to selected…".</summary>
    public int SelectedCount => Books.Count(b => b.IsTicked);

    /// <summary>Unticks every row (a new visit to the page starts with nothing ticked).</summary>
    public void ClearTicks()
    {
        foreach (var row in Books) row.IsTicked = false;
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(AddTagsLabel));
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BookRow.Title) or nameof(BookRow.Byline)) ApplyFind();   // the title appears after the file is read
        if (e.PropertyName != nameof(BookRow.IsTicked)) return;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(AddTagsLabel));
        AddTagsToSelectedCommand.NotifyCanExecuteChanged();
    }

    // ---- selection mode: tick boxes only while the person is choosing books to tag ----------------------------

    /// <summary>The tick boxes show and the rows' buttons are hidden; a click on a row ticks it.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowBrowseCommands))] private bool _isSelecting;

    partial void OnIsSelectingChanged(bool value)
    {
        foreach (var row in Books) row.IsSelecting = value;
    }

    /// <summary>"2 selected" in the header.</summary>
    public string SelectionText => _strings.Format("Library_SelectedCount", SelectedCount);

    /// <summary>"Add tags to 2 books".</summary>
    public string AddTagsLabel => SelectedCount == 1 ? _strings.Get("Library_AddTagsToOne") : _strings.Format("Library_AddTagsToMany", SelectedCount);

    /// <summary>The command bar's Add books and Select items: there are books and the person is not choosing some.</summary>
    public bool ShowBrowseCommands => HasBooks && !IsSelecting;

    [RelayCommand(CanExecute = nameof(HasBooks))]
    private void Select() => IsSelecting = true;

    /// <summary>Leaves selection mode and unticks everything (also what a visit to the page starts with).</summary>
    [RelayCommand]
    private void CancelSelection()
    {
        ClearTicks();
        IsSelecting = false;
    }

    /// <summary>Asks for the tags of one book and saves them.</summary>
    [RelayCommand]
    private async Task EditTagsAsync(BookRow? row)
    {
        if (row is null) return;
        var chosen = await _dialogs.EditTagsAsync(row.Title, row.Tags, await _service.ListTagsAsync());
        if (chosen is null) return;
        await _service.SetTagsAsync(row.Id, chosen);
        await RefreshRowAsync(row);
    }

    /// <summary>Asks for tags and adds them to every ticked book.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task AddTagsToSelectedAsync()
    {
        var rows = Books.Where(b => b.IsTicked).ToList();
        if (rows.Count == 0) return;
        var chosen = await _dialogs.AddTagsAsync(rows.Count, await _service.ListTagsAsync());
        if (chosen is null || chosen.Count == 0) return;
        await _service.AddTagsAsync(rows.Select(r => r.Id).ToList(), chosen);
        foreach (var row in rows) await RefreshRowAsync(row);
        CancelSelection();                                 // done: nothing stays ticked, and the tick boxes go away
    }

    private bool HasSelection() => Books.Any(b => b.IsTicked);

    // ---- pause and progress ---------------------------------------------------------------------------------

    [RelayCommand]
    private async Task TogglePauseAsync()
    {
        var pause = !IsPaused;
        await _service.SetPausedAsync(pause);
        IsPaused = pause;
        if (!pause) await StartWorkerAsync();
    }

    /// <summary>Called about once a second: updates only the rows that changed.</summary>
    public async Task PollAsync()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;   // the previous poll is still running
        try
        {
            await ApplyChangesAsync(await _service.PollAsync());
            if (_service.WorkerGaveUp) Banner = _strings.Get("Library_BannerWorkerStopped");
            else if (Banner == _strings.Get("Library_BannerWorkerStopped")) Banner = null;
        }
        finally { Volatile.Write(ref _polling, 0); }
    }

    public async Task ApplyChangesAsync(IReadOnlyList<BookProgress> changes)
    {
        foreach (var change in changes)
        {
            if (!_rows.TryGetValue(change.BookId, out var row)) continue;   // not in the list (removed, or not loaded yet)
            var statusBefore = row.Status;
            var stageBefore = row.Stage;
            var wasActive = statusBefore is BookStatus.Queued or BookStatus.Indexing;
            row.Apply(change);
            if (wasActive && row.Status is not (BookStatus.Queued or BookStatus.Indexing)) _batchFinished++;
            // Title, author and language are filled in once the file has been read, and the reason once it fails.
            if (row.Status != statusBefore || row.Stage != stageBefore) await RefreshRowAsync(row);
        }
        RaiseIndexing();
    }

    private async Task RefreshRowAsync(BookRow row)
    {
        if (await _service.GetAsync(row.Id) is { } entry) row.Apply(entry);
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private void AddRow(BookEntry entry)
    {
        var row = new BookRow(entry, _strings) { IsSelecting = IsSelecting };
        row.PropertyChanged += OnRowChanged;
        _rows[entry.Id] = row;
        Books.Add(row);
    }

    private async Task StartWorkerAsync()
    {
        var start = await _service.EnsureWorkerAsync();
        Banner = start.Problem;
    }
}
