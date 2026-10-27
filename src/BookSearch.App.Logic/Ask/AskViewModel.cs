using System.Collections.ObjectModel;
using BookSearch.App.History;
using BookSearch.App.Reader;
using BookSearch.Core;
using BookSearch.Core.Data;
using BookSearch.Core.Search;
using BookSearch.Core.Summarize;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BookSearch.App.Ask;

/// <summary>Text with the spans to highlight; the page draws it. <see cref="IsRtl"/> is set for Arabic, Hebrew and similar.</summary>
public sealed record HighlightedText(string Text, IReadOnlyList<TextSpan> Spans, bool IsRtl);

/// <summary>One search result as the list shows it.</summary>
public sealed partial class ResultRow : ObservableObject
{
    private readonly IStrings _strings;

    public ResultRow(SearchHit hit, QueryTerms terms, IStrings strings)
    {
        _strings = strings;
        Hit = hit;
        Title = hit.BookTitle ?? "";
        Author = hit.Author ?? "";
        Where = string.Join(" · ", new[] { hit.SectionTitle, Pages(hit, strings) }.Where(s => !string.IsNullOrWhiteSpace(s)));
        Excerpt = new HighlightedText(hit.Excerpt, hit.Highlights, TextDirection.IsRightToLeft(hit.Excerpt));
        Terms = terms;
    }

    public SearchHit Hit { get; }
    public string Title { get; }
    public string Author { get; }
    public string Where { get; }
    public HighlightedText Excerpt { get; }
    public QueryTerms Terms { get; }

    /// <summary>
    /// What a screen reader says for the result button: the book, author, place and the summary. While the summary is being
    /// written it says "Summarizing…" rather than the growing text, so the name does not change with every word.
    /// </summary>
    public string AutomationName => string.Join(", ", new[]
    {
        Title, Author, Where, Passage is { Status: PassageStatus.Writing } ? _strings.Get("Ask_PassageWriting") : SummaryText,
    }.Where(s => !string.IsNullOrWhiteSpace(s)));

    private PassageState? _passage;

    /// <summary>The state of this result's summary while one is waiting, being written or done; null when none was asked for.</summary>
    public PassageState? Passage
    {
        get => _passage;
        set
        {
            if (ReferenceEquals(_passage, value)) return;
            if (_passage is not null) _passage.PropertyChanged -= OnPassageChanged;
            _passage = value;
            if (_passage is not null) _passage.PropertyChanged += OnPassageChanged;
            RaiseSummaryChanged();
        }
    }

    private void OnPassageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RaiseSummaryChanged();

    private void RaiseSummaryChanged()
    {
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HasSummaryText));
        OnPropertyChanged(nameof(AutomationName));
    }

    /// <summary>
    /// The 2 to 3 sentences written from this result; "Waiting…" or "Summarizing…" before it is there; and, when the model
    /// found nothing in the passage that helps, a plain "doesn't cover your question" (the result stays in the list).
    /// </summary>
    public string SummaryText => Passage switch
    {
        null => "",
        { Status: PassageStatus.Waiting } => WaitingText ?? _strings.Get("Ask_PassageWaiting"),
        { Status: PassageStatus.Writing } p => PassageSummary.IsNotCovered(p.Text) || PassageSummary.MayBeNotCovered(p.Text)
                || string.IsNullOrWhiteSpace(p.Text)
            ? _strings.Get("Ask_PassageWriting") : p.Text,
        { Status: PassageStatus.Empty } => _strings.Get("Ask_SummaryEmpty"),
        { Status: PassageStatus.Written } p => p.Text,
        { Status: PassageStatus.NotCovered } => _strings.Get("Ask_PassageNotCovered"),
        _ => UnfinishedText ?? "",
    };

    public bool HasSummaryText => SummaryText.Length > 0;

    /// <summary>Why the summary is still waiting ("Loading model…", "Waiting for an earlier summary…"); null for the plain "waiting".</summary>
    public string? WaitingText
    {
        get => _waitingText;
        set { if (_waitingText == value) return; _waitingText = value; RaiseSummaryChanged(); }
    }

    /// <summary>Why a summary that was stopped or failed is missing ("Stopped", "could not be written…"); null or empty shows nothing.</summary>
    public string? UnfinishedText
    {
        get => _unfinishedText;
        set { if (_unfinishedText == value) return; _unfinishedText = value; RaiseSummaryChanged(); }
    }

    private string? _waitingText;
    private string? _unfinishedText;

    private static string? Pages(SearchHit h, IStrings strings) =>
        h.PageStart is not { } start ? null
        : h.PageEnd is { } end && end != start ? strings.Format("Ask_Pages", start, end)
        : strings.Format("Ask_Page", start);
}

/// <summary>
/// The Ask page as a conversation (ST-24): the questions of one thread, each followed by its results (each with its own summary), with the
/// banners that say how far to trust them. A question that looks like a follow-up is rewritten by the chat model to stand on its
/// own (DCR-6) and only that is searched; a chip shows what was searched.
/// </summary>
public sealed partial class AskViewModel : ObservableObject
{
    private readonly IAskService _service;
    private readonly IStrings _strings;
    private readonly SummaryCoordinator _coordinator;
    private readonly ISettingsStore _settings;
    private readonly ISearchHistoryStore _history;
    private readonly IDialogs? _dialogs;
    private readonly IFollowUpRewriter? _rewriter;
    private readonly IQuestionTranslator? _translator;
    private readonly Action<Action> _ui;
    private CancellationTokenSource? _cts;
    private TurnViewModel? _inFlight;      // the turn whose search is still running (not yet recorded)
    private bool _incompleteDismissed;   // stays dismissed until nothing is being indexed any more
    private int _searchId;
    private long? _threadId;

    /// <param name="post">Runs an action on the UI thread; by default the synchronization context of the creating thread.</param>
    /// <param name="rewriter">Makes a follow-up stand on its own before it is searched; without one every question is searched as typed.</param>
    public AskViewModel(IAskService service, IStrings strings, SummaryCoordinator coordinator, ISettingsStore settings,
        ISearchHistoryStore history, Action<Action>? post = null, IDialogs? dialogs = null, IFollowUpRewriter? rewriter = null,
        IQuestionTranslator? translator = null)
    {
        _dialogs = dialogs;
        _rewriter = rewriter;
        _translator = translator;
        _filter = SearchFilter.FromJson(settings.Load().Filter);
        _service = service;
        _strings = strings;
        _coordinator = coordinator;
        _settings = settings;
        _history = history;
        var context = SynchronizationContext.Current;
        _ui = post ?? (action =>
        {
            if (context is null) action();
            else context.Post(_ => action(), null);
        });
        Turns.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasTurns));
            OnPropertyChanged(nameof(Latest));
            foreach (var turn in Turns) turn.IsLatest = ReferenceEquals(turn, Latest);
            NewThreadCommand.NotifyCanExecuteChanged();
            if (EditingTurn is not null && !Turns.Contains(EditingTurn)) EditingTurn = null;   // a new thread, or the question was replaced
        };
        coordinator.HistoryChanged += () => _ui(() => _ = RefreshRestoredAsync());
    }

    /// <summary>The user chose a result: the shell opens its chapter in the Reader.</summary>
    public event Action<ReaderRequest>? ResultOpened;

    /// <summary>The "no books yet" state's button: the shell shows the Library page.</summary>
    public event Action? LibraryRequested;

    [RelayCommand]
    private void GoToLibrary() => LibraryRequested?.Invoke();

    /// <summary>The questions of the conversation on screen, oldest first; the newest is at the bottom.</summary>
    public ObservableCollection<TurnViewModel> Turns { get; } = [];

    public bool HasTurns => Turns.Count > 0;

    // ---- editing the last question --------------------------------------------------------------------------------

    /// <summary>The newest question while it is being edited in the question box; searching then replaces it.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEditing)), NotifyCanExecuteChangedFor(nameof(CancelEditCommand))]
    private TurnViewModel? _editingTurn;

    public bool IsEditing => EditingTurn is not null;

    /// <summary>The page puts the cursor in the question box and selects its text.</summary>
    public event Action? FocusQuestionRequested;

    /// <summary>The page puts the cursor in the question box without touching its text (the button that was pressed went away).</summary>
    public event Action? FocusQuestionBoxRequested;

    private void BeginEdit(TurnViewModel turn)
    {
        if (!ReferenceEquals(turn, Latest)) return;            // only the newest question: the ones after it would no longer follow from it
        EditingTurn = turn;
        Question = turn.Question;
        FocusQuestionRequested?.Invoke();
    }

    /// <summary>
    /// The retry button: the newest question, as typed, is asked again in place of the first attempt (a new search and a new
    /// summary, with the current filter and settings). A follow-up whose rewrite worked keeps it, as long as follow-up
    /// handling is still on, so the model is not asked again; one that was searched as typed after a failed rewrite is
    /// rewritten again, and one whose chip the user removed stays as typed. Translations that were made are kept the same way. A question being typed in the box is left alone;
    /// an edit of this same question in progress is dropped, since the retry replaces it.
    /// </summary>
    private void Retry(TurnViewModel turn)
    {
        if (!ReferenceEquals(turn, Latest)) return;
        if (ReferenceEquals(EditingTurn, turn)) { EditingTurn = null; Question = ""; }
        _ = ReplaceAsync(turn, turn.Question, turn.UsedContext, rewritten: turn.ContextQuestion, alsoSearched: turn.AlsoSearched, clearBox: false);
        FocusQuestionBoxRequested?.Invoke();                   // the button that was pressed is about to go away
    }

    [RelayCommand(CanExecute = nameof(IsEditing))]
    private void CancelEdit()
    {
        EditingTurn = null;
        Question = "";
    }

    // ---- the filter (ST-25) -----------------------------------------------------------------------------------

    /// <summary>The tags and books every search is limited to until the user clears it. Kept between runs of the app.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFilter), nameof(FilterSummary))]
    [NotifyCanExecuteChangedFor(nameof(ClearFilterCommand))]
    private SearchFilter _filter;

    partial void OnFilterChanged(SearchFilter value)
    {
        foreach (var turn in Turns) turn.RefreshFilterChip();
    }

    public bool HasFilter => !Filter.IsEmpty;
    public string FilterSummary => HasFilter ? _strings.Format("Ask_FilterActive", Filter.Describe()) : _strings.Get("Ask_FilterNone");

    /// <summary>Opens the list of tags and books to filter by.</summary>
    [RelayCommand]
    private async Task ChooseFilterAsync()
    {
        if (_dialogs is null) return;
        var chosen = await _dialogs.ChooseFilterAsync(await _service.GetFilterOptionsAsync(), Filter);
        if (chosen is not null) SetFilter(chosen);
    }

    [RelayCommand(CanExecute = nameof(HasFilter))]
    private void ClearFilter() => SetFilter(SearchFilter.None);

    private void SetFilter(SearchFilter filter)
    {
        Filter = filter;
        _settings.Save(_settings.Load() with { Filter = filter.ToJson() });
    }

    /// <summary>Drops filter entries that no longer exist: a tag nobody carries any more, a book that was removed or is being re-indexed.</summary>
    private async Task PruneFilterAsync()
    {
        if (Filter.IsEmpty) return;
        var options = await _service.GetFilterOptionsAsync();
        var tags = Filter.Tags.Where(t => options.Tags.Any(o => o.Key == Tags.KeyOf(t))).ToList();
        var books = Filter.Books.Where(b => options.Books.Any(o => o.Id == b.Id)).ToList();
        if (tags.Count != Filter.Tags.Count || books.Count != Filter.Books.Count) SetFilter(new SearchFilter(tags, books));
    }

    /// <summary>
    /// Esc: stops the newest summary that is being written or waiting for its turn. Pressing it again stops the next one.
    /// Does nothing when no summary is running.
    /// </summary>
    [RelayCommand]
    private async Task StopSummaryAsync()
    {
        var running = Turns.LastOrDefault(t => t.Summary.IsRunning);
        if (running?.Summary is { } summary && summary.StopCommand.CanExecute(null)) await summary.StopCommand.ExecuteAsync(null);
    }

    /// <summary>The newest question.</summary>
    public TurnViewModel? Latest => Turns.Count > 0 ? Turns[^1] : null;

    /// <summary>The conversation being shown (null while the page is blank).</summary>
    public long? ThreadId => _threadId;

    [ObservableProperty] private string _question = "";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasIncompleteNotice))] private string? _incompleteNotice;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowNoBooks), nameof(ShowSearchAccent), nameof(ShowStillIndexing), nameof(CanAsk))]
    private LibrarySummary _library = new(0, 0);

    public bool ShowNoBooks => Library is { ReadyBooks: 0, PendingBooks: 0 };

    /// <summary>Search is the page's accent button, except while "Go to Library" is (no books at all).</summary>
    public bool ShowSearchAccent => !ShowNoBooks;
    public bool ShowStillIndexing => Library is { ReadyBooks: 0, PendingBooks: > 0 };
    public bool CanAsk => Library.ReadyBooks > 0;
    public bool HasIncompleteNotice => !string.IsNullOrEmpty(IncompleteNotice);

    /// <summary>Re-reads what the library holds; the page calls this when it is shown.</summary>
    public async Task RefreshAsync()
    {
        _ = _coordinator.PrewarmAsync();                  // the user is on the Ask page: get the model ready in the background
        Library = await _service.GetLibrarySummaryAsync();
        await PruneFilterAsync();
        if (Library.PendingBooks == 0) _incompleteDismissed = false;   // a later round of indexing may warn again
        IncompleteNotice = Library.ReadyBooks > 0 && Library.PendingBooks > 0 && !_incompleteDismissed
            ? _strings.Format("Ask_Incomplete", Library.PendingBooks)
            : null;
    }

    // ---- threads ----------------------------------------------------------------------------------------------

    /// <summary>Shows the conversation remembered from last time (called once when the app starts). Does nothing if there is none.</summary>
    public async Task RestoreAsync()
    {
        if (_settings.Load().CurrentThreadId is { } id) await ShowThreadAsync(id);
    }

    /// <summary>Shows a saved conversation, every question with its saved summary text (the History page's "Continue this thread"); the one already on screen is left as it is.</summary>
    public async Task ShowThreadAsync(long threadId)
    {
        // The conversation already on screen with its live results (the History page's "Continue this thread" on the active thread):
        // keep it as it is; rebuilding it from the history would turn its results into saved text.
        if (_threadId == threadId && Turns.Any(t => t.IsLive)) return;
        var entries = await _history.ListThreadAsync(threadId);
        if (entries.Count == 0) return;
        CancelSearch();
        Turns.Clear();
        foreach (var entry in entries)
            Turns.Add(Wire(TurnViewModel.Restored(entry, _strings, new SummaryViewModel(_strings, _coordinator))));
        SetThread(threadId);
    }

    /// <summary>Starts a fresh conversation. The old one stays in the History.</summary>
    [RelayCommand(CanExecute = nameof(HasTurns))]
    private void NewThread()
    {
        CancelSearch();
        Turns.Clear();
        Question = "";
        SetThread(null);
    }

    private void SetThread(long? id)
    {
        _threadId = id;
        _settings.Save(_settings.Load() with { CurrentThreadId = id });
    }

    private void CancelSearch()
    {
        _cts?.Cancel();
        _searchId++;                                      // whatever the cancelled search does next is ignored
        _inFlight = null;
        IsSearching = false;
    }

    /// <summary>Summaries restored from the history fill in while they are completed after a restart.</summary>
    private async Task RefreshRestoredAsync()
    {
        if (_threadId is not { } id || !Turns.Any(t => t.IsRestored && t.IsUnfinished)) return;
        try
        {
            var entries = await _history.ListThreadAsync(id);
            foreach (var entry in entries)
                Turns.FirstOrDefault(t => t.IsRestored && t.EntryId == entry.Id)?.ApplySaved(entry);
        }
        catch (Exception) { /* the next change reloads */ }
    }

    // ---- asking -----------------------------------------------------------------------------------------------

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SearchAsync()
    {
        var question = Question.Trim();
        if (EditingTurn is { } editing && question.Length > 0)
        {
            EditingTurn = null;
            if (ReferenceEquals(editing, Latest)) return ReplaceAsync(editing, question, useContext: true);   // the edited question takes its place
        }
        return RunSearchAsync(question, useContext: true);
    }

    /// <param name="rewritten">The standalone form an earlier attempt already got (a retry); it is searched without asking the model again.</param>
    /// <param name="alsoSearched">The translations an earlier attempt already got (a retry); they are searched without asking the model again.</param>
    /// <param name="replacing">The newest finished question this one takes the place of (edit, retry, chip removal): it stays on
    /// screen until the new one is added, so nothing is lost if the search can't start, and its history entry is closed at that
    /// moment. A question still being searched is dropped at once instead.</param>
    /// <param name="clearBox">The typed text moves into the conversation; a retry leaves the box alone.</param>
    private async Task RunSearchAsync(string question, bool useContext, string? rewritten = null, TurnViewModel? replacing = null,
        bool clearBox = true, IReadOnlyList<string>? alsoSearched = null)
    {
        if (question.Length == 0) return;

        // A new search replaces the one still running. The old one is made stale first (new id, in-flight question taken),
        // so whatever it does when it is cancelled, on whichever thread, no longer touches the page.
        var abandoned = _inFlight;
        _inFlight = null;
        var id = ++_searchId;
        _cts?.Cancel();
        if (abandoned is not null) { abandoned.Replaced = true; Turns.Remove(abandoned); }   // marked: if it is recording its entry it closes it
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        IsSearching = true;
        try
        {
            await RefreshAsync();
            if (ct.IsCancellationRequested || id != _searchId) return;      // a second press or a newer search got here first
            if (!CanAsk) return;                                            // a finished question stays as it is

            // Each earlier question as it was searched, so a chain of follow-ups keeps its subject (not the one being replaced).
            var previous = Turns.Where(t => !ReferenceEquals(t, replacing)).Select(t => t.ContextQuestion ?? t.Question).ToList();
            var settings = _settings.Load();
            // With automatic summaries off the chat model is not kept loaded: a rewrite would load it cold and wait for it, and
            // with the context setting at 0 follow-up handling is off. Then even a rewrite kept from an earlier attempt is not used.
            var handled = useContext && _rewriter is not null && settings.AutoSummary && SettingsLimits.ContextQuestions(settings) > 0;
            var kept = handled ? rewritten : null;
            var earlier = handled && kept is null
                ? FollowUp.Detect(question, previous, SettingsLimits.ContextQuestions(settings)) : null;
            var turn = NewTurn(question);
            turn.UsedContext = useContext;
            _inFlight = turn;
            turn.IsSearching = true;
            if (replacing is not null) { replacing.Replaced = true; Turns.Remove(replacing); }
            Turns.Add(turn);                                    // one step: the old question is swapped for the new one
            if (replacing is { EntryId: not 0 })
            {
                try { await _coordinator.CloseEntryAsync(replacing.EntryId); }   // stops its summary too
                catch (Exception) { /* the old entry may stay in the History; the question is asked all the same */ }
            }
            if (ct.IsCancellationRequested || id != _searchId) return;
            if (clearBox && Question.Trim() == question) Question = "";     // the question moves into the conversation

            // A follow-up is made to stand on its own first; only that question is searched and summarized. If the model
            // can't do it, the question is searched as typed and the page says so.
            string? searchedAs = kept;
            if (earlier is not null)
            {
                try { searchedAs = await _rewriter!.RewriteAsync(question, earlier, ct); }
                catch (RewriteUnavailableException) { turn.RewriteUnavailable = true; }
                if (ct.IsCancellationRequested || id != _searchId) return;
            }
            var searchText = searchedAs ?? question;
            var filter = Filter;                                   // the filter this question is asked with, whatever happens to it later

            // The question is also written in the other languages of the books searched, so a passage in another language is found
            // by a query in its own language (DCR-11). Like the rewrite this needs the chat model; if it can't, the question is
            // searched in its own language and the page says so.
            IReadOnlyList<string> also = [];
            if (_translator is not null && settings.AutoSummary && settings.SearchOtherLanguages)
            {
                if (alsoSearched is { Count: > 0 }) also = alsoSearched;
                else
                {
                    var languages = TranslationTargets.Choose(searchText, await _service.GetBookLanguagesAsync(filter));
                    if (languages.Count > 0)
                    {
                        try { also = await _translator.TranslateAsync(searchText, languages, ct); }
                        catch (TranslationUnavailableException) { turn.TranslationUnavailable = true; }
                        if (ct.IsCancellationRequested || id != _searchId) return;
                    }
                }
            }

            // Use meaning as well as keywords only if the embedding server is already running; otherwise show the keyword
            // matches at once, start the server, and swap in the full results when it is ready.
            var hybrid = _service.EmbeddingReady;
            var result = await _service.SearchAsync(searchText, hybrid, ct, filter, also);
            if (ct.IsCancellationRequested || id != _searchId) return;
            var starting = !hybrid && result.KeywordOnly;
            turn.Show(result, starting, searchedAs, filter, also);

            if (starting) await UpgradeAsync(turn, searchText, searchedAs, result, filter, also, id, ct);
            else await FinishAsync(turn, question, searchedAs, result, filter, also, id);
        }
        catch (OperationCanceledException) { /* replaced by a newer search */ }
        finally
        {
            if (id == _searchId)
            {
                IsSearching = false;
                if (_inFlight is { } turn) { turn.IsSearching = false; _inFlight = null; }
            }
            if (ReferenceEquals(_cts, cts)) { _cts = null; }
            cts.Dispose();
        }
    }

    private TurnViewModel Wire(TurnViewModel turn)
    {
        turn.CurrentFilterLabel = () => Filter.IsEmpty ? null : Filter.Describe();
        turn.ContextRemoveRequested += t => _ = ReplaceAsync(t, t.Question, useContext: false);   // as typed: its translations are made again from the typed text
        turn.EditRequested += BeginEdit;
        turn.RetryRequested += Retry;
        return turn;
    }

    private TurnViewModel NewTurn(string question)
    {
        var turn = new TurnViewModel(question, _strings, new SummaryViewModel(_strings, _coordinator));
        Wire(turn);
        return turn;
    }

    /// <summary>
    /// The context chip's remove button, the edit button and the retry button: the newest question is asked again (as typed
    /// without its rewrite, as edited, or as before), in place of the first attempt, which is removed from the history once
    /// the new one starts. Only the newest question has the buttons.
    /// </summary>
    private async Task ReplaceAsync(TurnViewModel turn, string question, bool useContext, string? rewritten = null, bool clearBox = true,
        IReadOnlyList<string>? alsoSearched = null)
    {
        if (!ReferenceEquals(turn, Latest)) return;
        try { await RunSearchAsync(question, useContext, rewritten, replacing: turn, clearBox, alsoSearched); }
        catch (Exception) { /* the page is closing */ }
    }

    private async Task UpgradeAsync(TurnViewModel turn, string question, string? searchedAs, SearchResult keywordResult,
        SearchFilter filter, IReadOnlyList<string> also, int id, CancellationToken ct)
    {
        SearchResult final;
        try
        {
            await _service.WarmUpEmbeddingAsync(ct);
            if (ct.IsCancellationRequested || id != _searchId) return;
            final = await _service.SearchAsync(searchedAs ?? question, true, ct, filter, also);
            if (ct.IsCancellationRequested || id != _searchId) return;
            turn.Show(final, starting: false, searchedAs, filter, also);
        }
        catch (ModelUnavailableException)
        {
            if (id != _searchId) return;
            turn.Warning = turn.UnavailableWarning();
            final = keywordResult;                       // the meaning-based search can't run: the keyword results are final
        }
        await FinishAsync(turn, question, searchedAs, final, filter, also, id);
    }

    /// <summary>The results are final: record the question in the thread and queue its summary (when the library covers it).</summary>
    private async Task FinishAsync(TurnViewModel turn, string question, string? searchedAs, SearchResult result, SearchFilter filter,
        IReadOnlyList<string> also, int id)
    {
        var rows = turn.Results.ToList();
        var job = await _coordinator.RecordSearchAsync(question, result, _threadId, searchedAs, filter, also);   // the summary is written for the question that was searched
        if (job.ThreadId is { } thread && thread != _threadId && id == _searchId) SetThread(thread);
        if (id != _searchId)
        {
            if (turn.Replaced) await _coordinator.CloseEntryAsync(job.EntryId);   // recorded after another attempt took its place: nobody can see it
            return;
        }
        turn.EntryId = job.EntryId;
        turn.When = DateTime.Now.ToString("g", System.Globalization.CultureInfo.CurrentCulture);
        turn.IsSearching = false;
        turn.Summary.Attach(job, rows);
    }

    // The banners above the conversation can be closed by the user (each question's own banners are on its TurnViewModel).
    public void DismissIncompleteNotice()
    {
        _incompleteDismissed = true;
        IncompleteNotice = null;
    }

    [RelayCommand]
    private void OpenResult(ResultRow? row)
    {
        if (row is null) return;
        ResultOpened?.Invoke(new ReaderRequest(row.Hit.ChunkId, row.Terms));
    }
}
