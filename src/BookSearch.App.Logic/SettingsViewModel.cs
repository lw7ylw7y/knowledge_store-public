using BookSearch.App.History;
using BookSearch.Models.Management;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BookSearch.App;

public sealed record TierChoice(ModelTier Tier, string Title);
public sealed record ChatContextChoice(int Tokens, string Title);

/// <summary>
/// The Settings page (ST-18): model size, automatic summary, results count, chunk size and overlap, CPU cap, model downloads,
/// "Clear all history" and "Delete all data". Every switch saves at once. Number boxes give a double (NaN when emptied), so
/// the numeric properties are doubles; values are clamped to <see cref="SettingsLimits"/>.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly IStrings _strings;
    private readonly SummaryCoordinator _coordinator;
    private readonly IDialogs _dialogs;
    private readonly IIndexingControl? _indexing;
    private readonly IDataEraser? _eraser;
    private readonly Action? _stopEverything;
    private readonly AppSettings _initial;
    private readonly ModelTier _recommended;
    private readonly LibraryStatus _status;
    private bool _loading = true;

    /// <param name="indexing">Reads and writes the indexing CPU cap; null hides nothing but the value stays at its default.</param>
    /// <param name="eraser">Deletes the data for "Delete all data".</param>
    /// <param name="stopEverything">Stops the worker, the model servers and every database connection before the data is deleted.</param>
    public SettingsViewModel(AppPaths paths, ISettingsStore store, ModelTier recommended, LibraryStatus status, IStrings strings,
        SummaryCoordinator coordinator, IDialogs dialogs, IIndexingControl? indexing = null, IDataEraser? eraser = null,
        Action? stopEverything = null, int recommendedChatContext = HardwareTiers.StandardChatContext)
    {
        _store = store;
        _strings = strings;
        _coordinator = coordinator;
        _dialogs = dialogs;
        _indexing = indexing;
        _eraser = eraser;
        _stopEverything = stopEverything;
        _recommended = recommended;
        _status = status;
        _initial = store.Load();
        DataFolder = paths.DataFolder;
        _autoSummary = _initial.AutoSummary;
        _searchOtherLanguages = _initial.SearchOtherLanguages;
        _downloadsAllowed = _initial.DownloadsAllowed;
        _resultCount = SettingsLimits.ResultCount(_initial);
        _contextQuestions = SettingsLimits.ContextQuestions(_initial);
        _chunkTokens = SettingsLimits.ChunkTokens(_initial);
        _overlapTokens = SettingsLimits.OverlapTokens(_initial);
        _summaryContextIndex = Math.Clamp(_initial.SummaryContext, 0, 2);
        ContextChoices =
        [
            strings.Get("Settings_ContextSmall"),
            strings.Get("Settings_ContextMedium"),
            strings.Get("Settings_ContextLarge"),
        ];
        TierChoices =
        [
            new(ModelTier.Minimum, strings.Get("Wizard_TierMinimumTitle")),
            new(ModelTier.Recommended, strings.Get("Wizard_TierRecommendedTitle")),
        ];
        var current = _initial.TierOverride ?? recommended;
        _selectedTierIndex = TierChoices.ToList().FindIndex(c => c.Tier == current);
        _recommendedChatContext = recommendedChatContext;
        ChatContextChoices =
        [
            new(HardwareTiers.StandardChatContext, strings.Get("Settings_ChatContextStandard")),
            new(HardwareTiers.LargeChatContext, strings.Get("Settings_ChatContextLarge")),
        ];
        var context = SettingsLimits.ChatContext(_initial, recommendedChatContext);
        _selectedChatContextIndex = ChatContextChoices.ToList().FindIndex(c => c.Tokens == context);
        _chatContextNote = ChatContextText(context);
        _loading = false;
    }

    /// <summary>The model size was changed; the app reloads the shell, or shows the wizard when the new models aren't installed.</summary>
    public event Action? TierChanged;

    /// <summary>All data was deleted; the app returns to the first-run wizard.</summary>
    public event Action? DataDeleted;

    public string DataFolder { get; }

    public string Tier => _strings.Format(_initial.TierOverride is { } t
        ? "Settings_TierChosen" : "Settings_TierRecommended", (_initial.TierOverride ?? _recommended).ToString());

    public string Database => _strings.Format("Settings_Database", _status.SchemaVersion, _status.VecVersion);

    public IReadOnlyList<TierChoice> TierChoices { get; }

    /// <summary>Small, medium, large: how much text around each result the summary reads.</summary>
    public IReadOnlyList<string> ContextChoices { get; }

    [ObservableProperty] private int _summaryContextIndex;

    partial void OnSummaryContextIndexChanged(int value)
    {
        if (_loading || value < 0 || value > 2) return;
        Save(s => s with { SummaryContext = value });
    }

    public double MinResultCount => SettingsLimits.MinResultCount;
    public double MaxResultCount => SettingsLimits.MaxResultCount;
    public double MinContextQuestions => SettingsLimits.MinContextQuestions;
    public double MaxContextQuestions => SettingsLimits.MaxContextQuestions;
    public double MinChunkTokens => SettingsLimits.MinChunkTokens;
    public double MaxChunkTokens => SettingsLimits.MaxChunkTokens;
    public double MinOverlapTokens => SettingsLimits.MinOverlapTokens;
    public double MaxOverlapTokens => SettingsLimits.MaxOverlapTokens;
    public double MinCpuPercent => SettingsLimits.MinCpuPercent;
    public double MaxCpuPercent => SettingsLimits.MaxCpuPercent;

    /// <summary>Write a summary in the background after every search. Saved at once; it applies to the next search.</summary>
    [ObservableProperty] private bool _autoSummary;
    [ObservableProperty] private bool _searchOtherLanguages;

    partial void OnAutoSummaryChanged(bool value) => Save(s => s with { AutoSummary = value });
    partial void OnSearchOtherLanguagesChanged(bool value) => Save(s => s with { SearchOtherLanguages = value });

    /// <summary>Allow the model download (the wizard). Off means nothing in the app uses the network.</summary>
    [ObservableProperty] private bool _downloadsAllowed;

    partial void OnDownloadsAllowedChanged(bool value) => Save(s => s with { DownloadsAllowed = value });

    /// <summary>A failure shown in an InfoBar at the top of the page (for example files that could not be deleted).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasMessage))] private string _message = "";

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public void DismissMessage() => Message = "";

    // ---- model size ---------------------------------------------------------------------------------------------

    [ObservableProperty] private int _selectedTierIndex;

    partial void OnSelectedTierIndexChanged(int value)
    {
        if (_loading || value < 0 || value >= TierChoices.Count) return;
        var tier = TierChoices[value].Tier;
        Save(s => s with { TierOverride = tier == _recommended ? null : tier });
        TierChanged?.Invoke();
    }

    // ---- language model memory ---------------------------------------------------------------------------------

    private readonly int _recommendedChatContext;

    /// <summary>The two sizes of the language model's token pool; the recommended one follows the PC's memory.</summary>
    public IReadOnlyList<ChatContextChoice> ChatContextChoices { get; }

    [ObservableProperty] private int _selectedChatContextIndex;

    /// <summary>Whether the size is the recommended one or the user's, and when a change takes effect.</summary>
    [ObservableProperty] private string _chatContextNote;

    partial void OnSelectedChatContextIndexChanged(int value)
    {
        if (_loading || value < 0 || value >= ChatContextChoices.Count) return;
        var tokens = ChatContextChoices[value].Tokens;
        Save(s => s with { ChatContextTokens = tokens == _recommendedChatContext ? null : tokens });
        ChatContextNote = ChatContextText(tokens);
    }

    private string ChatContextText(int tokens)
    {
        var title = ChatContextChoices.First(c => c.Tokens == tokens).Title;
        var who = tokens == _recommendedChatContext ? "Settings_TierRecommended" : "Settings_TierChosen";
        return _strings.Format(who, title) + " " + _strings.Get("Settings_ChatContextApplies");
    }

    // ---- search and indexing ------------------------------------------------------------------------------------

    /// <summary>Passages per search; applies to the next search.</summary>
    [ObservableProperty] private double _resultCount;

    partial void OnResultCountChanged(double value)
    {
        if (_loading || double.IsNaN(value)) return;
        Save(s => s with { ResultCount = (int)Math.Clamp(Math.Round(value), SettingsLimits.MinResultCount, SettingsLimits.MaxResultCount) });
    }

    /// <summary>
    /// How many earlier questions a follow-up uses, for the search and for the summary alike; 0 treats every question on its own.
    /// Applies to the next question.
    /// </summary>
    [ObservableProperty] private double _contextQuestions;

    partial void OnContextQuestionsChanged(double value)
    {
        if (_loading || double.IsNaN(value)) return;
        Save(s => s with { ContextQuestions = (int)Math.Clamp(Math.Round(value), SettingsLimits.MinContextQuestions, SettingsLimits.MaxContextQuestions) });
    }

    /// <summary>Chunk size in tokens for books added from now on. Books already in the library keep their chunks.</summary>
    [ObservableProperty] private double _chunkTokens;

    partial void OnChunkTokensChanged(double value)
    {
        if (_loading || double.IsNaN(value)) return;
        Save(s => s with { ChunkTargetTokens = (int)Math.Clamp(Math.Round(value), SettingsLimits.MinChunkTokens, SettingsLimits.MaxChunkTokens) });
    }

    [ObservableProperty] private double _overlapTokens;

    partial void OnOverlapTokensChanged(double value)
    {
        if (_loading || double.IsNaN(value)) return;
        Save(s => s with { ChunkOverlapTokens = (int)Math.Clamp(Math.Round(value), SettingsLimits.MinOverlapTokens, SettingsLimits.MaxOverlapTokens) });
    }

    /// <summary>Share of the CPU cores indexing may use. The worker reads it before every batch, so it applies at once.</summary>
    [ObservableProperty] private double _cpuCap = SettingsLimits.MaxCpuPercent;

    public bool HasIndexing => _indexing is not null;

    partial void OnCpuCapChanged(double value)
    {
        if (_loading || _indexing is null || double.IsNaN(value)) return;
        var percent = (int)Math.Clamp(Math.Round(value), SettingsLimits.MinCpuPercent, SettingsLimits.MaxCpuPercent);
        _ = _indexing.SetCpuCapAsync(percent);
    }

    /// <summary>Reads the CPU cap from the database; called when the page opens.</summary>
    public async Task LoadAsync()
    {
        if (_indexing is null) return;
        var cap = await _indexing.GetCpuCapAsync();
        _loading = true;
        CpuCap = cap;
        _loading = false;
    }

    // ---- data ---------------------------------------------------------------------------------------------------

    /// <summary>Deletes every search-history entry (after asking) and drops summaries still waiting or being written.</summary>
    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        if (await _dialogs.ConfirmClearHistoryAsync()) await _coordinator.ClearAsync();
    }

    /// <summary>Asks, stops everything that uses the files, deletes the data (and the models if chosen) and returns to the wizard.</summary>
    [RelayCommand]
    private async Task DeleteAllDataAsync()
    {
        var choice = await _dialogs.ConfirmDeleteAllAsync();
        if (choice == DeleteAllChoice.Cancel || _eraser is null) return;
        Message = "";
        try
        {
            _stopEverything?.Invoke();
            await Task.Run(() => _eraser.Erase(choice == DeleteAllChoice.DeleteModels));
        }
        catch (IOException e)
        {
            Message = _strings.Format("Settings_DeleteFailed", e.Message);   // a file is still in use; whatever was deleted stays deleted
            return;
        }
        DataDeleted?.Invoke();
    }

    private void Save(Func<AppSettings, AppSettings> change) => _store.Save(change(_store.Load()));
}
