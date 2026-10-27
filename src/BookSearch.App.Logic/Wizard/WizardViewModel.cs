using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Net.Http;
using BookSearch.Models.Management;

namespace BookSearch.App.Wizard;

public enum WizardStep { Privacy, Hardware, Models, Done }

public enum ModelRowState { Waiting, Downloading, Verifying, Installed }

/// <summary>One model file in the wizard's list, with its download progress.</summary>
public sealed partial class ModelRow(ModelEntry entry, string name, string sizeText) : ObservableObject
{
    public ModelEntry Entry { get; } = entry;
    public string Name { get; } = name;
    public string SizeText { get; } = sizeText;

    [ObservableProperty] private ModelRowState _state;
    [ObservableProperty] private double _percent;          // 0-100, for a ProgressBar
    [ObservableProperty] private string _statusText = "";

    public bool IsInstalled => State == ModelRowState.Installed;
    partial void OnStateChanged(ModelRowState value) => OnPropertyChanged(nameof(IsInstalled));

    internal void MarkInstalled(IStrings strings)
    {
        State = ModelRowState.Installed;
        Percent = 100;
        StatusText = strings.Get("Wizard_RowInstalled");
    }

    internal void Report(DownloadProgress p, IStrings strings)
    {
        if (IsInstalled) return;   // a progress report that arrives late must not undo "installed"
        State = p.Verifying ? ModelRowState.Verifying : ModelRowState.Downloading;
        Percent = p.BytesTotal > 0 ? 100.0 * p.BytesDone / p.BytesTotal : 0;
        StatusText = strings.Format(p.Verifying ? "Wizard_RowVerifying" : "Wizard_RowDownloading",
            p.BytesDone / 1_000_000.0, p.BytesTotal / 1_000_000.0);
    }
}

public sealed record TierOption(ModelTier Tier, string Title, string Description, bool IsRecommended);

/// <summary>
/// First-run wizard: privacy note, hardware check and tier, download or import of the models, done. Holds no UI types, so
/// it is tested directly; the page binds to it.
/// </summary>
public sealed partial class WizardViewModel : ObservableObject
{
    private readonly ModelManifest _manifest;
    private readonly IModelProvisioner _models;
    private readonly ISettingsStore _settings;
    private readonly IStrings _strings;
    private CancellationTokenSource? _cts;

    public WizardViewModel(ModelManifest manifest, IModelProvisioner models, ISettingsStore settings, IStrings strings,
        long totalRamBytes)
    {
        _manifest = manifest;
        _models = models;
        _settings = settings;
        _strings = strings;

        RecommendedTier = HardwareTiers.Select(totalRamBytes);
        BelowMinimum = HardwareTiers.IsBelowMinimum(totalRamBytes);
        RamText = strings.Format("Wizard_RamLine", HardwareTiers.ToGb(totalRamBytes));
        TierOptions = [BuildOption(ModelTier.Minimum), BuildOption(ModelTier.Recommended)];
        _selectedTier = settings.Load().TierOverride ?? RecommendedTier;
        RebuildRows();
    }

    public event Action? Completed;

    public ModelTier RecommendedTier { get; }
    public bool BelowMinimum { get; }
    public string RamText { get; }
    public IReadOnlyList<TierOption> TierOptions { get; }
    public ObservableCollection<ModelRow> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPrivacyStep), nameof(IsHardwareStep), nameof(IsModelsStep), nameof(IsDoneStep), nameof(IsNotDoneStep),
        nameof(CanGoBack), nameof(NextLabel))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(BackCommand), nameof(DownloadCommand), nameof(FinishCommand))]
    private WizardStep _step;

    [ObservableProperty] private ModelTier _selectedTier;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(BackCommand), nameof(DownloadCommand), nameof(FinishCommand))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;
    [ObservableProperty] private string _statusText = "";

    public bool IsPrivacyStep => Step == WizardStep.Privacy;
    public bool IsHardwareStep => Step == WizardStep.Hardware;
    public bool IsModelsStep => Step == WizardStep.Models;
    public bool IsDoneStep => Step == WizardStep.Done;
    public bool IsNotDoneStep => Step != WizardStep.Done;
    public bool CanGoBack => Step is WizardStep.Hardware or WizardStep.Models;
    public bool CanCancel => IsBusy;
    public bool HasError => Error is not null;
    public bool AllInstalled => Rows.Count > 0 && Rows.All(r => r.IsInstalled);
    public string NextLabel => _strings.Get(Step == WizardStep.Privacy ? "Wizard_Accept" : "Wizard_Next");

    partial void OnSelectedTierChanged(ModelTier value)
    {
        RebuildRows();
        NextCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
    }

    // ---- navigation ---------------------------------------------------------------------------------------

    private bool CanNext() => !IsBusy && Step switch
    {
        WizardStep.Privacy or WizardStep.Hardware => true,
        WizardStep.Models => AllInstalled,
        _ => false,
    };

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next()
    {
        Error = null;
        if (Step == WizardStep.Hardware) SaveTier();     // remembered even if the download is abandoned
        Step++;
        if (Step == WizardStep.Models) StatusText = AllInstalled ? _strings.Get("Wizard_AlreadyInstalled") : "";
    }

    [RelayCommand(CanExecute = nameof(CanBack))]
    private void Back()
    {
        Error = null;
        Step--;
    }

    private bool CanBack() => !IsBusy && CanGoBack;

    [RelayCommand(CanExecute = nameof(CanFinish))]
    private void Finish()
    {
        SaveTier();
        _settings.Save(_settings.Load() with { WizardCompleted = true });
        Completed?.Invoke();
    }

    private bool CanFinish() => !IsBusy && Step == WizardStep.Done;

    private void SaveTier() =>
        _settings.Save(_settings.Load() with { TierOverride = SelectedTier == RecommendedTier ? null : SelectedTier });

    // ---- download and import ------------------------------------------------------------------------------

    private bool CanDownload() => !IsBusy && Step == WizardStep.Models && !AllInstalled;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        IsBusy = true;
        Error = null;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        ModelRow? current = null;
        try
        {
            foreach (var row in Rows.Where(r => !r.IsInstalled).ToList())
            {
                current = row;
                row.State = ModelRowState.Downloading;
                row.StatusText = _strings.Get("Wizard_RowStarting");
                await _models.DownloadAsync(row.Entry, new Progress<DownloadProgress>(p => row.Report(p, _strings)), ct)
                    .ConfigureAwait(true);
                row.MarkInstalled(_strings);
            }
            StatusText = _strings.Get("Wizard_AllInstalled");
        }
        catch (OperationCanceledException)
        {
            Reset(current);
            StatusText = _strings.Get("Wizard_DownloadPaused");
        }
        catch (DownloadsDisabledException)
        {
            Reset(current);
            Error = _strings.Get("Wizard_ErrorDownloadsOff");
            DownloadsBlocked = true;
        }
        catch (ChecksumMismatchException)
        {
            Reset(current);
            Error = _strings.Get("Wizard_ErrorChecksum");
        }
        catch (HttpRequestException)
        {
            Reset(current);
            Error = _strings.Get("Wizard_ErrorNetwork");
        }
        catch (IOException e)
        {
            Reset(current);
            Error = _strings.Format("Wizard_ErrorDisk", e.Message);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
            OnPropertyChanged(nameof(AllInstalled));
            NextCommand.NotifyCanExecuteChanged();
            DownloadCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>The download was refused because Settings has downloads off; this lets the user turn them back on from here.</summary>
    [ObservableProperty] private bool _downloadsBlocked;

    [RelayCommand]
    private void AllowDownloads()
    {
        _settings.Save(_settings.Load() with { DownloadsAllowed = true });
        DownloadsBlocked = false;
        Error = null;
    }

    /// <summary>Installs model files picked from disk (offline setup). A language model from another tier switches the tier to match.</summary>
    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync(IReadOnlyList<string>? paths)
    {
        if (paths is not { Count: > 0 }) return;
        IsBusy = true;
        Error = null;
        _cts = new CancellationTokenSource();
        try
        {
            foreach (var path in paths)
            {
                var entry = await _models.ImportAsync(path, new Progress<DownloadProgress>(p =>
                    StatusText = _strings.Format("Wizard_RowVerifying", p.BytesDone / 1_000_000.0, p.BytesTotal / 1_000_000.0)),
                    _cts.Token).ConfigureAwait(true);
                if (entry is { Role: ModelRole.Llm, Tier: { } tier } && tier != SelectedTier && tier != ModelTier.Enhanced)
                    SelectedTier = tier;
                RefreshRows();
                StatusText = _strings.Format("Wizard_Imported", Path.GetFileName(path));
            }
        }
        catch (OperationCanceledException) { StatusText = _strings.Get("Wizard_DownloadPaused"); }
        catch (ModelRejectedException e)
        {
            Error = e.Reason switch
            {
                ModelRejection.WrongFileType => _strings.Get("Wizard_ErrorImportType"),
                ModelRejection.UnknownModel => _strings.Get("Wizard_ErrorImportUnknown"),
                _ => e.Message,
            };
        }
        catch (FileNotFoundException e) { Error = e.Message; }
        catch (IOException e) { Error = _strings.Format("Wizard_ErrorDisk", e.Message); }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
            OnPropertyChanged(nameof(AllInstalled));
            NextCommand.NotifyCanExecuteChanged();
            DownloadCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanImport() => !IsBusy && Step == WizardStep.Models;

    public void DismissError() => Error = null;

    /// <summary>The file picker could not be opened or failed; show why instead of doing nothing.</summary>
    public void ReportPickerFailure(string detail) => Error = _strings.Format("Wizard_ErrorPicker", detail);

    // ---- helpers ------------------------------------------------------------------------------------------

    private void Reset(ModelRow? row)
    {
        if (row is null || row.IsInstalled) return;
        row.State = ModelRowState.Waiting;
        row.Percent = 0;
        row.StatusText = "";
    }

    private void RebuildRows()
    {
        Rows.Clear();
        foreach (var entry in _manifest.RequiredFor(SelectedTier))
        {
            var row = new ModelRow(entry,
                _strings.Get(entry.Role switch { ModelRole.Runtime => "Wizard_ModelRuntime", ModelRole.Embedding => "Wizard_ModelEmbedding", _ => "Wizard_ModelLlm" }),
                _strings.Format("Wizard_SizeGb", entry.SizeBytes / 1e9));
            if (_models.IsInstalled(entry)) row.MarkInstalled(_strings);
            Rows.Add(row);
        }
        OnPropertyChanged(nameof(AllInstalled));
    }

    private void RefreshRows()
    {
        foreach (var row in Rows)
            if (!row.IsInstalled && _models.IsInstalled(row.Entry)) row.MarkInstalled(_strings);
    }

    private TierOption BuildOption(ModelTier tier)
    {
        var total = _manifest.RequiredFor(tier).Sum(e => e.SizeBytes) / 1e9;
        return new TierOption(tier,
            _strings.Get(tier == ModelTier.Minimum ? "Wizard_TierMinimumTitle" : "Wizard_TierRecommendedTitle"),
            _strings.Format(tier == ModelTier.Minimum ? "Wizard_TierMinimumDescription" : "Wizard_TierRecommendedDescription", total),
            tier == RecommendedTier);
    }
}
