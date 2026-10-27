using BookSearch.App.Ask;
using BookSearch.Core.Search;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BookSearch.App.Reader;

/// <summary>Opens a search result in the Reader: which chunk, and the question's terms to highlight in the chapter.</summary>
public sealed record ReaderRequest(long ChunkId, QueryTerms Terms);

/// <summary>One paragraph of the chapter. <see cref="PassageSpan"/> is the part that belongs to the opened passage.</summary>
/// <param name="Index">The paragraph's number in the chapter (from 0). It keeps two identical paragraphs ("* * *") apart when the page scrolls to one.</param>
public sealed record ReaderParagraph(string Text, IReadOnlyList<TextSpan> TermSpans, TextSpan? PassageSpan, bool IsRtl, int Index = 0);

/// <summary>Starts the book's own file in the program Windows associates with it.</summary>
public interface IFileLauncher
{
    bool Exists(string path);

    /// <exception cref="IOException">There is no program to open it with, or it failed to start.</exception>
    void Open(string path);
}

/// <summary>Where the Reader was opened from: it says where Back goes, and only reading from the Library saves a place.</summary>
public enum ReaderOrigin { Results, Library }

/// <summary>
/// The Reader page: one chapter as text, scrolled to the passage a search result came from, or, opened from the Library, a
/// book at the place the reader stopped (ST-35). A place is saved only for books opened from the Library: looking up a passage
/// in another chapter must not move "Continue".
/// </summary>
public sealed partial class ReaderViewModel(IReaderService reader, IFileLauncher files, IStrings strings) : ObservableObject
{
    private static readonly HashSet<string> RtlLanguages = ["ar", "he", "fa", "ur", "yi"];

    private ReaderChapter? _chapter;
    private Task _pendingSave = Task.CompletedTask;     // the save made when a chapter was shown; awaited so the load ends after it
    private int _visibleParagraph;
    private int _paragraphCount;
    private QueryTerms _terms = new([], []);
    private string _filePath = "";
    private int _loadId;

    /// <summary>The page should scroll to this paragraph (the first one of the passage).</summary>
    public event Action<int>? ScrollRequested;

    [ObservableProperty] private string _bookTitle = "";
    [ObservableProperty] private string _author = "";
    [ObservableProperty] private string _chapterTitle = "";
    [ObservableProperty] private string _pagesText = "";
    [ObservableProperty] private string _positionText = "";
    [ObservableProperty] private IReadOnlyList<ReaderParagraph> _paragraphs = [];
    [ObservableProperty] private bool _isRtl;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string? _error;
    [ObservableProperty] private bool _isLoading;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(OpenFileCommand))] private bool _canOpenFile;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFileMessage))] private string? _fileMessage;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(PreviousChapterCommand))] private bool _canGoPrevious;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(NextChapterCommand))] private bool _canGoNext;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(BackText))] private ReaderOrigin _origin = ReaderOrigin.Results;

    /// <summary>"Back to Library" or "Back to results", whichever is where Back goes.</summary>
    public string BackText => strings.Get(Origin == ReaderOrigin.Library ? "Reader_BackToLibrary" : "Reader_BackToResults");

    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool HasFileMessage => !string.IsNullOrEmpty(FileMessage);

    /// <summary>Loads the chapter that holds a search result and scrolls to the result.</summary>
    public async Task OpenAsync(ReaderRequest request)
    {
        Origin = ReaderOrigin.Results;
        _terms = request.Terms;
        ForgetChapter();
        await LoadAsync(() => reader.LoadForChunkAsync(request.ChunkId));
    }

    /// <summary>Opens a whole book from the Library at the place the reader stopped, or at its first chapter.</summary>
    public async Task OpenBookAsync(long bookId)
    {
        Origin = ReaderOrigin.Library;
        _terms = new QueryTerms([], []);
        ForgetChapter();
        await LoadAsync(() => reader.LoadBookAsync(bookId));
    }

    /// <summary>
    /// Nothing of the book shown before is kept while another one loads: a slow or failed load must never save that book's place
    /// again, nor leave its paragraphs on screen under the new book's error.
    /// </summary>
    private void ForgetChapter()
    {
        _chapter = null;
        _paragraphCount = 0;
        _visibleParagraph = 0;
        Paragraphs = [];
    }

    /// <summary>
    /// The page reports the first paragraph on screen (about once a second); when it changed, the place is saved. A failure to
    /// save is not worth interrupting reading for.
    /// </summary>
    public async Task NoteVisibleParagraphAsync(int index)
    {
        if (Origin != ReaderOrigin.Library || _chapter is null || _paragraphCount == 0) return;
        index = Math.Clamp(index, 0, _paragraphCount - 1);
        if (index == _visibleParagraph) return;
        _visibleParagraph = index;
        await SavePlaceAsync();
    }

    /// <summary>Saves the place now (the page calls this when the Reader is left).</summary>
    public Task SavePlaceAsync()
    {
        if (Origin != ReaderOrigin.Library || _chapter is not { Count: > 0 } chapter) return Task.CompletedTask;
        return SaveAsync(chapter.Book.Id, chapter.Index, _visibleParagraph);
    }

    private async Task SaveAsync(long bookId, int chapterIndex, int paragraph)
    {
        try { await reader.SavePositionAsync(bookId, chapterIndex, paragraph); }
        catch (Exception e) when (e is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException) { /* the next change tries again */ }
    }

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private Task PreviousChapterAsync() => GoToAsync(-1);

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private Task NextChapterAsync() => GoToAsync(+1);

    private async Task GoToAsync(int step)
    {
        if (_chapter is not { } current) return;
        await LoadAsync(() => reader.LoadChapterAsync(current.Book.Id, current.Index + step));
    }

    [RelayCommand(CanExecute = nameof(CanOpenFile))]
    private void OpenFile()
    {
        if (!files.Exists(_filePath))                        // it may have been moved since the chapter was loaded
        {
            MarkFileMissing();
            return;
        }
        try { files.Open(_filePath); }
        catch (IOException e) { FileMessage = strings.Format("Reader_OpenFailed", e.Message); }
    }

    private async Task LoadAsync(Func<Task<ReaderChapter?>> load)
    {
        var id = ++_loadId;
        IsLoading = true;
        Error = null;
        try
        {
            var chapter = await load();
            if (id != _loadId) return;                       // a newer load replaced this one
            if (chapter is null)
            {
                Error = strings.Get(Origin == ReaderOrigin.Library ? "Reader_BookNotFound" : "Reader_LoadFailed");
                return;
            }
            Show(chapter);
            await _pendingSave;
        }
        catch (Exception e) when (e is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            if (id == _loadId) Error = strings.Format("Reader_LoadFailedWith", e.Message);
        }
        finally
        {
            if (id == _loadId) IsLoading = false;
        }
    }

    private void Show(ReaderChapter chapter)
    {
        if (chapter.Count == 0)                              // a book with no text at all
        {
            ShowNoText(chapter);
            return;
        }
        _chapter = chapter;
        _filePath = chapter.Book.FilePath;
        BookTitle = chapter.Book.Title;
        Author = chapter.Book.Author ?? "";
        ChapterTitle = string.IsNullOrWhiteSpace(chapter.Title) ? strings.Get("Reader_Untitled") : chapter.Title!;
        PositionText = strings.Format("Reader_Position", chapter.Index + 1, chapter.Count);
        PagesText = chapter switch
        {
            { PageStart: { } start, PageEnd: { } end } when end != start => strings.Format("Reader_Pages", start, end),
            { PageStart: { } start } => strings.Format("Reader_Page", start),
            _ => "",
        };
        CanGoPrevious = chapter.Index > 0;
        CanGoNext = chapter.Index + 1 < chapter.Count;

        if (files.Exists(chapter.Book.FilePath))
        {
            CanOpenFile = true;
            FileMessage = null;
        }
        else MarkFileMissing();

        var bookIsRtl = chapter.Book.Language is { } language && RtlLanguages.Contains(language.ToLowerInvariant());
        IsRtl = bookIsRtl || Ask.TextDirection.IsRightToLeft(chapter.Text.Text);
        var built = BuildParagraphs(chapter, IsRtl);
        Paragraphs = built.Paragraphs;
        _paragraphCount = built.Paragraphs.Count;
        var startAt = built.FirstPassageIndex
            ?? (Origin == ReaderOrigin.Library ? Math.Clamp(chapter.ResumeParagraph, 0, Math.Max(0, _paragraphCount - 1)) : 0);
        _visibleParagraph = startAt;
        ScrollRequested?.Invoke(startAt);
        if (Origin == ReaderOrigin.Library) _pendingSave = SaveAsync(chapter.Book.Id, chapter.Index, startAt);   // opened or moved: this is the place now
    }

    private void ShowNoText(ReaderChapter chapter)
    {
        _chapter = null;
        _paragraphCount = 0;
        _filePath = chapter.Book.FilePath;
        BookTitle = chapter.Book.Title;
        Author = chapter.Book.Author ?? "";
        ChapterTitle = "";
        PagesText = "";
        PositionText = "";
        Paragraphs = [];
        CanGoPrevious = false;
        CanGoNext = false;
        Error = strings.Get("Reader_NoText");
        if (files.Exists(_filePath))
        {
            CanOpenFile = true;
            FileMessage = null;
        }
        else MarkFileMissing();
    }

    private void MarkFileMissing()
    {
        CanOpenFile = false;
        FileMessage = strings.Get("Reader_FileNotFound");
    }

    /// <summary>Cuts the chapter into paragraphs (blank-line separated) and works out the highlights inside each one.</summary>
    private (IReadOnlyList<ReaderParagraph> Paragraphs, int? FirstPassageIndex) BuildParagraphs(ReaderChapter chapter, bool rtl)
    {
        var text = chapter.Text.Text;
        var passage = chapter.HighlightChunkId is { } id ? chapter.Text.Ranges.FirstOrDefault(r => r.ChunkId == id) : null;
        var paragraphs = new List<ReaderParagraph>();
        int? firstPassage = null;
        var position = 0;
        while (position <= text.Length)
        {
            var end = text.IndexOf("\n\n", position, StringComparison.Ordinal);
            var stop = end < 0 ? text.Length : end;
            var paragraph = text[position..stop];
            if (paragraph.Length > 0)
            {
                TextSpan? span = null;
                if (passage is not null)
                {
                    var from = Math.Max(position, passage.Start);
                    var to = Math.Min(stop, passage.End);
                    if (to > from) span = new TextSpan(from - position, to - from);
                }
                if (span is not null) firstPassage ??= paragraphs.Count;
                paragraphs.Add(new ReaderParagraph(paragraph, ExcerptBuilder.FindMatches(paragraph, _terms), span,
                    rtl || Ask.TextDirection.IsRightToLeft(paragraph), paragraphs.Count));
            }
            if (end < 0) break;
            position = end + 2;
        }
        return (paragraphs, firstPassage);
    }
}
