using BookSearch.App;
using BookSearch.App.Wizard;
using BookSearch.Models.Management;

namespace BookSearch.Tests;

public class WizardTests : IDisposable
{
    private const long Gb = 1024L * 1024 * 1024;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wizard-" + Guid.NewGuid().ToString("N"));

    private sealed class Strings : IStrings
    {
        public string Get(string key) => key;   // enough to see which key was used
    }

    private sealed class FakeProvisioner(ModelManifest manifest) : IModelProvisioner
    {
        public HashSet<string> Installed { get; } = [];
        public List<string> Downloads { get; } = [];
        public Func<ModelEntry, IProgress<DownloadProgress>, CancellationToken, Task>? OnDownload { get; set; }
        public Func<string, ModelEntry>? OnImport { get; set; }

        public bool IsInstalled(ModelEntry entry) => Installed.Contains(entry.Id);

        public async Task DownloadAsync(ModelEntry entry, IProgress<DownloadProgress> progress, CancellationToken ct)
        {
            Downloads.Add(entry.Id);
            progress.Report(new DownloadProgress(entry.Id, entry.SizeBytes / 2, entry.SizeBytes, false));
            if (OnDownload is not null) await OnDownload(entry, progress, ct);
            Installed.Add(entry.Id);
        }

        public Task<ModelEntry> ImportAsync(string path, IProgress<DownloadProgress> progress, CancellationToken ct)
        {
            var entry = OnImport!(path);
            Installed.Add(entry.Id);
            return Task.FromResult(entry);
        }
    }

    private readonly ModelManifest _manifest = ModelManifest.LoadDefault();
    private SettingsStore Settings => new(Path.Combine(_dir, "settings.json"));

    private WizardViewModel Wizard(FakeProvisioner models, long ram = 16 * Gb) =>
        new(_manifest, models, Settings, new Strings(), ram);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private void InstallAll(FakeProvisioner models, ModelTier tier)
    {
        foreach (var e in _manifest.RequiredFor(tier)) models.Installed.Add(e.Id);
    }

    [Fact]
    public async Task Wizard_DownloadsTurnedOff_ExplainsAndAllowDownloadsTurnsThemOn()
    {
        var store = Settings;
        store.Save(new AppSettings { DownloadsAllowed = false });
        var gated = new GatedModelProvisioner(new FakeProvisioner(_manifest), () => store.Load().DownloadsAllowed);
        var wizard = new WizardViewModel(_manifest, gated, store, new Strings(), 16 * Gb);
        wizard.NextCommand.Execute(null);
        wizard.NextCommand.Execute(null);                              // privacy, hardware, now the models step

        await wizard.DownloadCommand.ExecuteAsync(null);

        Assert.Equal("Wizard_ErrorDownloadsOff", wizard.Error);
        Assert.True(wizard.DownloadsBlocked);

        wizard.AllowDownloadsCommand.Execute(null);

        Assert.True(store.Load().DownloadsAllowed);
        Assert.False(wizard.DownloadsBlocked);
        Assert.Null(wizard.Error);
    }

    // ---- start-up decision --------------------------------------------------------------------------------

    [Fact]
    public void Wizard_ModelsMissing_ShowsWizard()
    {
        var models = new FakeProvisioner(_manifest);
        InstallAll(models, ModelTier.Recommended);
        var completed = new AppSettings { WizardCompleted = true };
        Assert.Equal(StartPage.Library, Startup.Decide(completed, _manifest, ModelTier.Recommended, models));

        models.Installed.Remove(_manifest.LlmFor(ModelTier.Recommended).Id);      // the user deleted a model file
        Assert.Equal(StartPage.Wizard, Startup.Decide(completed, _manifest, ModelTier.Recommended, models));

        InstallAll(models, ModelTier.Recommended);
        models.Installed.Remove(_manifest.Runtime.Id);                            // or the llama.cpp program
        Assert.Equal(StartPage.Wizard, Startup.Decide(completed, _manifest, ModelTier.Recommended, models));

        InstallAll(models, ModelTier.Recommended);
        models.Installed.Remove(_manifest.Embedding.Id);
        Assert.Equal(StartPage.Wizard, Startup.Decide(completed, _manifest, ModelTier.Recommended, models));
    }

    [Fact]
    public void Wizard_ModelsPresent_SkipsToLibrary()
    {
        var models = new FakeProvisioner(_manifest);
        InstallAll(models, ModelTier.Minimum);

        // The user chose the smaller model although this PC would get the larger one.
        var settings = new AppSettings { WizardCompleted = true, TierOverride = ModelTier.Minimum };
        Assert.Equal(StartPage.Library, Startup.Decide(settings, _manifest, ModelTier.Recommended, models));
    }

    [Fact]
    public void Wizard_NeverCompleted_ShowsWizardEvenWithModels()
    {
        var models = new FakeProvisioner(_manifest);
        InstallAll(models, ModelTier.Recommended);
        Assert.Equal(StartPage.Wizard, Startup.Decide(new AppSettings(), _manifest, ModelTier.Recommended, models));
    }

    // ---- tier ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(16, ModelTier.Recommended, false)]
    [InlineData(8, ModelTier.Minimum, false)]
    [InlineData(4, ModelTier.Minimum, true)]
    public void Wizard_Hardware_RecommendsTierAndWarnsBelowMinimum(int ramGb, ModelTier tier, bool warn)
    {
        var wizard = Wizard(new FakeProvisioner(_manifest), ramGb * Gb);

        Assert.Equal(tier, wizard.RecommendedTier);
        Assert.Equal(tier, wizard.SelectedTier);
        Assert.Equal(warn, wizard.BelowMinimum);
        Assert.Single(wizard.TierOptions, o => o.IsRecommended);
        Assert.Equal(tier, wizard.TierOptions.Single(o => o.IsRecommended).Tier);
    }

    [Fact]
    public void Wizard_TierOverride_SavedToSettings()
    {
        var models = new FakeProvisioner(_manifest);
        var wizard = Wizard(models, 16 * Gb);                  // recommends the 4B model
        wizard.NextCommand.Execute(null);                      // privacy -> hardware
        wizard.SelectedTier = ModelTier.Minimum;
        wizard.NextCommand.Execute(null);                      // hardware -> models: saved now, in case the user quits

        Assert.Equal(ModelTier.Minimum, Settings.Load().TierOverride);
        Assert.False(Settings.Load().WizardCompleted);
        Assert.Equal(_manifest.LlmFor(ModelTier.Minimum).Id, wizard.Rows.Single(r => r.Entry.Role == ModelRole.Llm).Entry.Id);

        InstallAll(models, ModelTier.Minimum);
        Assert.Equal(ModelTier.Minimum, Wizard(models).SelectedTier);   // reopening starts from the saved choice
    }

    [Fact]
    public void Wizard_ChoosingTheRecommendedTierAgain_ClearsTheOverride()
    {
        Settings.Save(new AppSettings { TierOverride = ModelTier.Minimum });
        var wizard = Wizard(new FakeProvisioner(_manifest), 16 * Gb);
        wizard.NextCommand.Execute(null);
        wizard.SelectedTier = ModelTier.Recommended;
        wizard.NextCommand.Execute(null);

        Assert.Null(Settings.Load().TierOverride);
    }

    // ---- flow ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Wizard_Download_InstallsBothModelsThenFinishRemembersCompletion()
    {
        var models = new FakeProvisioner(_manifest);
        var wizard = Wizard(models);
        var completed = false;
        wizard.Completed += () => completed = true;

        Assert.True(wizard.IsPrivacyStep);
        Assert.False(wizard.BackCommand.CanExecute(null));
        wizard.NextCommand.Execute(null);
        Assert.True(wizard.IsHardwareStep);
        wizard.NextCommand.Execute(null);
        Assert.True(wizard.IsModelsStep);
        Assert.False(wizard.NextCommand.CanExecute(null));      // can't continue until the models are there

        await wizard.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(3, models.Downloads.Count);               // the llama.cpp program and the two models
        Assert.Equal(_manifest.Runtime.Id, models.Downloads[0]);
        Assert.True(wizard.AllInstalled);
        Assert.All(wizard.Rows, r => Assert.Equal(100, r.Percent));
        Assert.True(wizard.NextCommand.CanExecute(null));
        var finishChanged = 0;
        wizard.FinishCommand.CanExecuteChanged += (_, _) => finishChanged++;   // a button only re-checks when told to
        wizard.NextCommand.Execute(null);
        Assert.True(finishChanged > 0);
        Assert.True(wizard.IsDoneStep);
        Assert.False(wizard.NextCommand.CanExecute(null));

        Assert.False(Settings.Load().WizardCompleted);
        Assert.True(wizard.FinishCommand.CanExecute(null));      // the button must be enabled, not just runnable
        wizard.FinishCommand.Execute(null);
        Assert.True(completed);
        Assert.True(Settings.Load().WizardCompleted);
    }

    [Fact]
    public async Task Wizard_ModelsAlreadyInstalled_NeedsNoDownload()
    {
        var models = new FakeProvisioner(_manifest);
        InstallAll(models, ModelTier.Recommended);
        var wizard = Wizard(models);
        wizard.NextCommand.Execute(null);
        wizard.NextCommand.Execute(null);

        Assert.True(wizard.AllInstalled);
        Assert.False(wizard.DownloadCommand.CanExecute(null));
        Assert.True(wizard.NextCommand.CanExecute(null));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Wizard_NetworkFailure_ShowsErrorAndAllowsRetry()
    {
        var models = new FakeProvisioner(_manifest) { OnDownload = (_, _, _) => throw new HttpRequestException("offline") };
        var wizard = Wizard(models);
        wizard.NextCommand.Execute(null);
        wizard.NextCommand.Execute(null);

        await wizard.DownloadCommand.ExecuteAsync(null);

        Assert.Equal("Wizard_ErrorNetwork", wizard.Error);
        Assert.False(wizard.IsBusy);
        Assert.False(wizard.AllInstalled);
        Assert.True(wizard.DownloadCommand.CanExecute(null));

        models.OnDownload = null;
        await wizard.DownloadCommand.ExecuteAsync(null);
        Assert.Null(wizard.Error);
        Assert.True(wizard.AllInstalled);
    }

    [Fact]
    public async Task Wizard_ChecksumFailure_ShowsError()
    {
        var models = new FakeProvisioner(_manifest)
        {
            OnDownload = (e, _, _) => throw new ChecksumMismatchException(e.File, "a", "b"),
        };
        var wizard = Wizard(models);
        wizard.NextCommand.Execute(null);
        wizard.NextCommand.Execute(null);

        await wizard.DownloadCommand.ExecuteAsync(null);

        Assert.Equal("Wizard_ErrorChecksum", wizard.Error);
    }

    [Fact]
    public async Task Wizard_Cancel_StopsDownloadAndKeepsItResumable()
    {
        var started = new TaskCompletionSource();
        var models = new FakeProvisioner(_manifest)
        {
            OnDownload = async (_, _, ct) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); },
        };
        var wizard = Wizard(models);
        wizard.NextCommand.Execute(null);
        wizard.NextCommand.Execute(null);

        var run = wizard.DownloadCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(wizard.IsBusy);
        Assert.False(wizard.BackCommand.CanExecute(null));      // can't navigate away mid-download
        wizard.CancelCommand.Execute(null);
        await run;

        Assert.False(wizard.IsBusy);
        Assert.Null(wizard.Error);
        Assert.Equal("Wizard_DownloadPaused", wizard.StatusText);
        Assert.True(wizard.DownloadCommand.CanExecute(null));
    }

    [Fact]
    public async Task Wizard_ImportOfOtherTiersModel_SwitchesTier()
    {
        var models = new FakeProvisioner(_manifest);
        var wizard = Wizard(models, 16 * Gb);                  // recommends 4B
        wizard.NextCommand.Execute(null);
        wizard.NextCommand.Execute(null);
        var small = _manifest.LlmFor(ModelTier.Minimum);
        models.OnImport = path => path.EndsWith("emb.gguf") ? _manifest.Embedding : path.EndsWith(".zip") ? _manifest.Runtime : small;

        await wizard.ImportCommand.ExecuteAsync(new[] { "/usb/emb.gguf", "/usb/small.gguf", "/usb/llama.zip" });

        Assert.Equal(ModelTier.Minimum, wizard.SelectedTier);
        Assert.True(wizard.AllInstalled);
        Assert.Null(wizard.Error);
    }

    [Fact]
    public async Task Wizard_DismissError_ClearsIt()
    {
        var models = new FakeProvisioner(_manifest) { OnDownload = (_, _, _) => throw new HttpRequestException("offline") };
        var wizard = Wizard(models);
        wizard.NextCommand.Execute(null);
        wizard.NextCommand.Execute(null);
        await wizard.DownloadCommand.ExecuteAsync(null);
        Assert.True(wizard.HasError);

        wizard.DismissError();

        Assert.False(wizard.HasError);
        Assert.Null(wizard.Error);
    }

    [Fact]
    public async Task Wizard_ImportRejected_ShowsMessage()
    {
        var models = new FakeProvisioner(_manifest) { OnImport = _ => throw new ModelRejectedException("not one of the models", ModelRejection.UnknownModel) };
        var wizard = Wizard(models);
        wizard.NextCommand.Execute(null);
        wizard.NextCommand.Execute(null);

        await wizard.ImportCommand.ExecuteAsync(new[] { "/usb/random.gguf" });

        Assert.Equal("Wizard_ErrorImportUnknown", wizard.Error);      // the app's own (translatable) sentence, not the exception's English text
        Assert.False(wizard.IsBusy);
    }

    [Fact]
    public async Task Wizard_LateProgressReport_DoesNotUndoInstalled()
    {
        var models = new FakeProvisioner(_manifest);
        IProgress<DownloadProgress>? captured = null;
        models.OnDownload = (_, progress, _) => { captured = progress; return Task.CompletedTask; };
        var wizard = Wizard(models);
        wizard.NextCommand.Execute(null);
        wizard.NextCommand.Execute(null);
        await wizard.DownloadCommand.ExecuteAsync(null);

        captured!.Report(new DownloadProgress("x", 1, 10, false));
        await Task.Delay(100);

        Assert.All(wizard.Rows, r => Assert.True(r.IsInstalled));
    }

    // ---- settings --------------------------------------------------------------------------------------------

    [Fact]
    public void Settings_RoundTrip_AndCorruptFileFallsBackToDefaults()
    {
        var store = Settings;
        Assert.Equal(new AppSettings(), store.Load());

        store.Save(new AppSettings { WizardCompleted = true, TierOverride = ModelTier.Minimum });
        Assert.Equal(new AppSettings { WizardCompleted = true, TierOverride = ModelTier.Minimum }, store.Load());
        Assert.Contains("\"Minimum\"", File.ReadAllText(Path.Combine(_dir, "settings.json")));   // readable by hand

        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ not json");
        Assert.Equal(new AppSettings(), store.Load());
    }

    [Fact]
    public void AppPaths_DataFolderVariable_OverridesDefault()
    {
        var old = Environment.GetEnvironmentVariable(AppPaths.DataFolderVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.DataFolderVariable, _dir);
            var paths = AppPaths.Default();
            Assert.Equal(_dir, paths.DataFolder);
            Assert.Equal(Path.Combine(_dir, "models"), paths.ModelsFolder);
        }
        finally { Environment.SetEnvironmentVariable(AppPaths.DataFolderVariable, old); }
    }

    [Fact]
    public void LibraryDatabase_Initialize_CreatesAndReportsEmptyLibrary()
    {
        var status = LibraryDatabase.Initialize(new AppPaths(_dir));

        Assert.Equal(0, status.BookCount);
        Assert.StartsWith("v0.1", status.VecVersion);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
