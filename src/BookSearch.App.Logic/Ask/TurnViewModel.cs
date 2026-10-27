using System.Collections.ObjectModel;
using System.Globalization;
using BookSearch.App.History;
using BookSearch.Core.Data;
using BookSearch.Core.Search;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BookSearch.App.Ask;

/// <summary>
/// One question of the conversation on the Ask page (ST-24): the question and its results, each with its own summary (ST-34).
/// A turn asked in this session is live (results, links, chips). A turn restored from the history has only the question and
/// the saved summary text, because results are not stored.
/// </summary>
public sealed partial class TurnViewModel : ObservableObject
{
    private readonly IStrings _strings;
    private bool _notCoveredDismissed;

    public TurnViewModel(string question, IStrings strings, SummaryViewModel summary)
    {
        Question = question;
        _strings = strings;
        Summary = summary;
        Results.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasResults));
    }

    /// <summary>The history entry of this question; 0 until the search is final and recorded.</summary>
    public long EntryId { get; internal set; }

    public string Question { get; }

    /// <summary>When the question was asked (local time); empty for a live turn until it is recorded.</summary>
    [ObservableProperty] private string _when = "";

    /// <summary>The summaries of the results (they are shown in the result rows); Esc stops the one being written. Live turns only.</summary>
    public SummaryViewModel Summary { get; }

    public ObservableCollection<ResultRow> Results { get; } = [];

    /// <summary>The context chip's remove button: the shell runs the question again without the earlier questions.</summary>
    public event Action<TurnViewModel>? ContextRemoveRequested;

    /// <summary>The pencil button: the question goes back into the question box to be changed and searched again (newest question only).</summary>
    public event Action<TurnViewModel>? EditRequested;

    /// <summary>The retry button: the same question is asked again in place of this one (newest question only).</summary>
    public event Action<TurnViewModel>? RetryRequested;

    // ---- live turn --------------------------------------------------------------------------------------------

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsLive))] private bool _isRestored;
    public bool IsLive => !IsRestored;

    [ObservableProperty] private bool _isSearching;

    /// <summary>The newest question of the page: only it has working chip buttons.</summary>
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(RetryCommand))] private bool _isLatest;
    [ObservableProperty] private bool _sourcesExpanded = true;

    /// <summary>What the search was limited to ("novels, Pride and Prejudice"), or null when it searched every book.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFilter), nameof(HasChips), nameof(HasChipRow), nameof(FilterText))] private string? _filterLabel;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasContext), nameof(HasChips), nameof(HasChipRow), nameof(ContextText))] private string? _contextQuestion;

    /// <summary>The question in the other languages of the library it was also searched in (DCR-11); empty when it was not.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasAlsoSearched), nameof(HasChips), nameof(AlsoSearchedText))] private IReadOnlyList<string> _alsoSearched = [];

    /// <summary>False when the user removed the "Searched as" chip: a retry then searches the question as typed again.</summary>
    internal bool UsedContext { get; set; } = true;

    /// <summary>Another attempt took this question's place; a search of it that is still recording its entry must close it.</summary>
    internal bool Replaced { get; set; }

    /// <summary>The question looked like a follow-up, but the language model could not rewrite it: it was searched as typed.</summary>
    internal bool RewriteUnavailable { get; set; }

    /// <summary>The books are in other languages, but the language model could not translate the question: it was searched in its own language only.</summary>
    internal bool TranslationUnavailable { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowNotCovered))] private bool _notCovered;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasWarning))] private string? _warning;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NoResultsText))] private bool _searchedWithoutResults;

    public bool HasResults => Results.Count > 0;
    /// <summary>
    /// The "Searching in…" chip. It is hidden on the newest question while it says what the filter line at the bottom of the page
    /// already says (UX design issue 15); an older question keeps its chip, because it may have been asked with another filter.
    /// </summary>
    public bool HasFilter => !string.IsNullOrEmpty(FilterLabel) && !(IsLatest && FilterLabel == CurrentFilterLabel?.Invoke());

    /// <summary>What the page's filter says now (null when there is none); set by the page's view model.</summary>
    internal Func<string?>? CurrentFilterLabel { get; set; }

    /// <summary>The page's filter changed: the chip may now show or hide.</summary>
    internal void RefreshFilterChip()
    {
        OnPropertyChanged(nameof(HasFilter));
        OnPropertyChanged(nameof(HasChips));
        OnPropertyChanged(nameof(HasChipRow));
    }

    partial void OnIsLatestChanged(bool value) => RefreshFilterChip();
    public string FilterText => _strings.Format("Ask_FilterChip", FilterLabel ?? "");

    /// <summary>The follow-up was rewritten to stand on its own; the chip shows what was searched.</summary>
    public bool HasContext => !string.IsNullOrEmpty(ContextQuestion);

    /// <summary>The question was also searched in other languages; the chip shows what was searched.</summary>
    public bool HasAlsoSearched => AlsoSearched.Count > 0;
    public string AlsoSearchedText => _strings.Format("Ask_AlsoSearched", string.Join(" · ", AlsoSearched));

    /// <summary>Any chip shows, so the chips (and the gap above them) are there.</summary>
    public bool HasChips => HasChipRow || HasAlsoSearched;

    /// <summary>The first chip row shows: the filter and the follow-up as rewritten. "Also searched" has a row of its own under it.</summary>
    public bool HasChipRow => HasContext || HasFilter;
    public string ContextText => _strings.Format("Ask_SearchedAs", ContextQuestion ?? "");
    public bool HasWarning => !string.IsNullOrEmpty(Warning);
    public bool ShowNotCovered => NotCovered && !_notCoveredDismissed;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NoResultsText))] private bool _filterMatchedNoBooks;
    public string NoResultsText => !SearchedWithoutResults ? ""
        : FilterMatchedNoBooks ? _strings.Get("Ask_NoBooksInFilter") : _strings.Get("Ask_NoResults");

    /// <summary>Hides the "library doesn't cover this" banner of this question only.</summary>
    public void DismissNotCovered()
    {
        _notCoveredDismissed = true;
        OnPropertyChanged(nameof(ShowNotCovered));
    }

    public void DismissWarning() => Warning = null;

    [RelayCommand] private void RemoveContext() => ContextRemoveRequested?.Invoke(this);
    [RelayCommand(CanExecute = nameof(IsLatest))] private void Edit() => EditRequested?.Invoke(this);
    [RelayCommand(CanExecute = nameof(IsLatest))] private void Retry() => RetryRequested?.Invoke(this);

    internal void Show(SearchResult result, bool starting, string? searchedAs, SearchFilter? filter = null,
        IReadOnlyList<string>? alsoSearched = null)
    {
        Results.Clear();
        foreach (var hit in result.Hits) Results.Add(new ResultRow(hit, result.HighlightTerms, _strings));
        _notCoveredDismissed = false;
        FilterLabel = filter is { IsEmpty: false } ? filter.Describe() : null;
        FilterMatchedNoBooks = result.FilterMatchedNoBooks;
        ContextQuestion = searchedAs;
        AlsoSearched = alsoSearched ?? [];
        NotCovered = result.NotCovered;
        OnPropertyChanged(nameof(ShowNotCovered));
        SearchedWithoutResults = result.Hits.Count == 0;
        Warning = starting ? _strings.Get("Ask_WarningStarting")
            : result.KeywordOnly ? UnavailableWarning()
            : Unavailable(null);
    }

    /// <summary>The warning for a model that could not rewrite or translate the question (null when both worked or were not needed).</summary>
    private string? Unavailable(string? first)
    {
        var parts = new List<string>();
        if (first is not null) parts.Add(first);
        if (RewriteUnavailable) parts.Add(_strings.Get("Ask_WarningNoRewrite"));
        if (TranslationUnavailable) parts.Add(_strings.Get("Ask_WarningNoTranslation"));
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>The meaning-based search could not run; also says so if the question could not be rewritten either.</summary>
    internal string UnavailableWarning() => Unavailable(_strings.Get("Ask_WarningUnavailable"))!;

    // ---- restored turn ----------------------------------------------------------------------------------------

    /// <summary>The saved summary text of a restored turn.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSavedSummary))] private string _savedSummary = "";

    /// <summary>"Writing…", "Stopped" and so on for a restored turn whose summary is not (yet) text.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSavedStatus))] private string _savedStatus = "";

    public bool HasSavedSummary => SavedSummary.Length > 0;
    public bool HasSavedStatus => SavedStatus.Length > 0;

    /// <summary>Whether the saved summary may still change (it was waiting or being written when the app closed).</summary>
    public bool IsUnfinished { get; private set; }

    internal static TurnViewModel Restored(SearchHistoryEntry entry, IStrings strings, SummaryViewModel summary)
    {
        var turn = new TurnViewModel(entry.Question, strings, summary)
        {
            EntryId = entry.Id,
            IsRestored = true,
            SourcesExpanded = false,
        };
        turn.ApplySaved(entry);
        return turn;
    }

    /// <summary>Takes the saved status and text of the entry (called again while a restored summary is being completed).</summary>
    internal void ApplySaved(SearchHistoryEntry entry)
    {
        When = entry.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        var filter = SearchFilter.FromJson(entry.Filter);
        FilterLabel = filter.IsEmpty ? null : filter.Describe();
        ContextQuestion = entry.SearchedAs;
        AlsoSearched = entry.AlsoSearched ?? [];
        SavedSummary = entry.SummaryText ?? "";
        IsUnfinished = entry.Status is SummaryStatus.Waiting or SummaryStatus.Writing;
        SavedStatus = entry.Status switch
        {
            SummaryStatus.Waiting => _strings.Get("History_StatusWaiting"),
            SummaryStatus.Writing => _strings.Get("History_StatusWriting"),
            SummaryStatus.Stopped => _strings.Get("History_StatusStopped"),
            SummaryStatus.Failed => _strings.Get("History_StatusFailed"),
            SummaryStatus.NotCovered => _strings.Get("History_StatusNotCovered"),
            SummaryStatus.None => _strings.Get("History_StatusNone"),
            _ => "",
        };
    }
}
