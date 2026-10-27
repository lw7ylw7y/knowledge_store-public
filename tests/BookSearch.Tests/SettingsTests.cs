using BookSearch.App;
using BookSearch.Models.Management;
using static BookSearch.Tests.SummaryKit;

namespace BookSearch.Tests;

/// <summary>The completed Settings page (ST-18): saved values, the worker's chunk arguments, the CPU cap, downloads and Delete all data.</summary>
public class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class FakeIndexing : IIndexingControl
    {
        public int Cap { get; set; } = 100;
        public Task<int> GetCpuCapAsync() => Task.FromResult(Cap);
        public Task SetCpuCapAsync(int percent) { Cap = percent; return Task.CompletedTask; }
    }

    private sealed class FakeEraser(List<string> log) : IDataEraser
    {
        public bool? DeleteModels { get; private set; }
        public Exception? Fail { get; set; }
        public void Erase(bool deleteModels)
        {
            log.Add("erase");
            if (Fail is not null) throw Fail;
            DeleteModels = deleteModels;
        }
    }

    private SettingsViewModel Vm(Settings store, Dialogs? dialogs = null, IIndexingControl? indexing = null, IDataEraser? eraser = null,
        Action? stop = null)
    {
        var coordinator = Coordinator(new FakeSummarizer(), new InMemoryHistory());
        return new SettingsViewModel(new AppPaths(_dir), store, ModelTier.Recommended, new LibraryStatus(0, "v0.1.9", 3),
            new Strings(), coordinator, dialogs ?? new Dialogs(), indexing, eraser, stop);
    }

    // ---- saved values -----------------------------------------------------------------------------------------------

    [Fact]
    public void Settings_ResultCount_IsSavedAndClamped()
    {
        var store = new Settings();
        var vm = Vm(store);
        Assert.Equal(3, vm.ResultCount);

        vm.ResultCount = 5;
        Assert.Equal(5, store.Load().ResultCount);
        vm.ResultCount = 50;
        Assert.Equal(SettingsLimits.MaxResultCount, store.Load().ResultCount);
        vm.ResultCount = double.NaN;                                   // an emptied number box changes nothing
        Assert.Equal(SettingsLimits.MaxResultCount, store.Load().ResultCount);
    }

    [Fact]
    public void Settings_ContextQuestions_DefaultsToOneIsSavedAndClamped()
    {
        var store = new Settings();
        var vm = Vm(store);
        Assert.Equal(1, vm.ContextQuestions);

        vm.ContextQuestions = 3;
        Assert.Equal(3, store.Load().ContextQuestions);
        vm.ContextQuestions = 0;
        Assert.Equal(0, store.Load().ContextQuestions);                // 0 is allowed: no context at all
        vm.ContextQuestions = 9;
        Assert.Equal(SettingsLimits.MaxContextQuestions, store.Load().ContextQuestions);
        vm.ContextQuestions = double.NaN;
        Assert.Equal(SettingsLimits.MaxContextQuestions, store.Load().ContextQuestions);
        Assert.Equal(2, SettingsLimits.ContextQuestions(new AppSettings { ContextQuestions = 2 }));
        Assert.Equal(0, SettingsLimits.ContextQuestions(new AppSettings { ContextQuestions = -4 }));   // a bad settings.json is clamped
    }

    [Fact]
    public void Settings_OverlapNeverExceedsAThirdOfTheChunk()
    {
        var settings = new AppSettings { ChunkTargetTokens = 200, ChunkOverlapTokens = 200 };
        Assert.Equal(66, SettingsLimits.OverlapTokens(settings));
        Assert.Equal(1000, SettingsLimits.ChunkTokens(new AppSettings { ChunkTargetTokens = 5000 }));
    }

    [Fact]
    public void Settings_ChangeChunkSize_IsPassedToTheWorkerAtItsNextStart()
    {
        var store = new Settings();
        var manifest = ModelManifest.LoadDefault();
        var setup = new WorkerSetup(new AppPaths(_dir), manifest, new ModelStore(Path.Combine(_dir, "models")), _dir, new Strings(), store);
        var vm = Vm(store);

        Assert.Equal(["--chunk-tokens", "500", "--chunk-overlap", "75"], setup.BuildArguments("s.exe", 1).Skip(8));

        vm.ChunkTokens = 800;
        vm.OverlapTokens = 100;

        Assert.Equal(["--chunk-tokens", "800", "--chunk-overlap", "100"], setup.BuildArguments("s.exe", 1).Skip(8));
    }

    [Fact]
    public async Task Settings_CpuCap_IsReadOnOpenAndWrittenOnChange()
    {
        var indexing = new FakeIndexing { Cap = 40 };
        var vm = Vm(new Settings(), indexing: indexing);

        await vm.LoadAsync();
        Assert.Equal(40, vm.CpuCap);
        Assert.Equal(40, indexing.Cap);                                // loading doesn't write back

        vm.CpuCap = 70;
        Assert.Equal(70, indexing.Cap);
        vm.CpuCap = 0;                                                 // below the minimum
        Assert.Equal(SettingsLimits.MinCpuPercent, indexing.Cap);
    }

    [Fact]
    public void Settings_ChangeModelSize_SavesTheChoiceAndRaisesTierChanged()
    {
        var store = new Settings();
        var vm = Vm(store);
        var raised = 0;
        vm.TierChanged += () => raised++;
        Assert.Equal(1, vm.SelectedTierIndex);                         // Recommended

        vm.SelectedTierIndex = 0;

        Assert.Equal(ModelTier.Minimum, store.Load().TierOverride);
        Assert.Equal(1, raised);
        vm.SelectedTierIndex = 1;                                      // back to the recommended one: no override
        Assert.Null(store.Load().TierOverride);
    }

    [Fact]
    public void Settings_ChatContext_FollowsTheRecommendationUntilChosen()
    {
        var store = new Settings();
        var coordinator = Coordinator(new FakeSummarizer(), new InMemoryHistory());
        var vm = new SettingsViewModel(new AppPaths(_dir), store, ModelTier.Recommended, new LibraryStatus(0, "v0.1.9", 3),
            new Strings(), coordinator, new Dialogs(), recommendedChatContext: HardwareTiers.LargeChatContext);
        Assert.Equal([HardwareTiers.StandardChatContext, HardwareTiers.LargeChatContext], vm.ChatContextChoices.Select(c => c.Tokens));
        Assert.Equal(1, vm.SelectedChatContextIndex);                                  // the recommended size
        Assert.Contains("Settings_TierRecommended", vm.ChatContextNote);

        vm.SelectedChatContextIndex = 0;

        Assert.Equal(HardwareTiers.StandardChatContext, store.Load().ChatContextTokens);
        Assert.Contains("Settings_TierChosen", vm.ChatContextNote);
        Assert.Equal(HardwareTiers.StandardChatContext, SettingsLimits.ChatContext(store.Load(), HardwareTiers.LargeChatContext));
        vm.SelectedChatContextIndex = 1;                                               // back to the recommended one: no override
        Assert.Null(store.Load().ChatContextTokens);
    }

    [Fact]
    public void Settings_ChatContext_IgnoresAValueThatIsNotOneOfTheSizes() =>
        Assert.Equal(8192, SettingsLimits.ChatContext(new AppSettings { ChatContextTokens = 1234 }, 8192));

    // ---- downloads --------------------------------------------------------------------------------------------------

    private sealed class CountingProvisioner : IModelProvisioner
    {
        public int Downloads, Imports;
        public bool IsInstalled(ModelEntry entry) => false;
        public Task DownloadAsync(ModelEntry entry, IProgress<DownloadProgress> progress, CancellationToken ct) { Downloads++; return Task.CompletedTask; }
        public Task<ModelEntry> ImportAsync(string path, IProgress<DownloadProgress> progress, CancellationToken ct)
        {
            Imports++;
            return Task.FromResult(ModelManifest.LoadDefault().Embedding);
        }
    }

    [Fact]
    public async Task Settings_DownloadsOff_BlocksDownloadButNotImport()
    {
        var store = new Settings();
        var inner = new CountingProvisioner();
        var gate = new GatedModelProvisioner(inner, () => store.Load().DownloadsAllowed);
        var entry = ModelManifest.LoadDefault().Embedding;
        var vm = Vm(store);

        vm.DownloadsAllowed = false;

        await Assert.ThrowsAsync<DownloadsDisabledException>(() => gate.DownloadAsync(entry, new Progress<DownloadProgress>(), default));
        await gate.ImportAsync("x.gguf", new Progress<DownloadProgress>(), default);
        Assert.Equal(0, inner.Downloads);
        Assert.Equal(1, inner.Imports);

        vm.DownloadsAllowed = true;
        await gate.DownloadAsync(entry, new Progress<DownloadProgress>(), default);
        Assert.Equal(1, inner.Downloads);
    }

    // ---- delete all data --------------------------------------------------------------------------------------------

    private void Populate(AppPaths paths)
    {
        Directory.CreateDirectory(paths.ModelsFolder);
        Directory.CreateDirectory(paths.LogFolder);
        foreach (var f in new[] { paths.DatabasePath, paths.DatabasePath + "-wal", paths.DatabasePath + "-shm", paths.SettingsPath,
                     Path.Combine(paths.ModelsFolder, "m.gguf"), Path.Combine(paths.LogFolder, "crash.log"), Path.Combine(_dir, "other.txt") })
            File.WriteAllText(f, "x");
    }

    [Fact]
    public void Settings_DeleteAll_KeepModels_RemovesOnlyDbAndLogs()
    {
        var paths = new AppPaths(_dir);
        Populate(paths);

        new DataEraser(paths).Erase(deleteModels: false);

        Assert.False(File.Exists(paths.DatabasePath));
        Assert.False(File.Exists(paths.DatabasePath + "-wal"));
        Assert.False(File.Exists(paths.DatabasePath + "-shm"));
        Assert.False(File.Exists(paths.SettingsPath));
        Assert.False(Directory.Exists(paths.LogFolder));
        Assert.True(File.Exists(Path.Combine(paths.ModelsFolder, "m.gguf")));
        Assert.True(File.Exists(Path.Combine(_dir, "other.txt")));      // not ours: left alone
    }

    [Fact]
    public void Settings_DeleteAll_WithModels_RemovesTheModelsToo()
    {
        var paths = new AppPaths(_dir);
        Populate(paths);

        new DataEraser(paths).Erase(deleteModels: true);

        Assert.False(Directory.Exists(paths.ModelsFolder));
        Assert.True(File.Exists(Path.Combine(_dir, "other.txt")));
    }

    [Fact]
    public async Task Settings_DeleteAll_StopsEverythingFirstThenErasesThenReturnsToWizard()
    {
        var log = new List<string>();
        var eraser = new FakeEraser(log);
        var dialogs = new Dialogs { DeleteAllAnswer = DeleteAllChoice.DeleteModels };
        var vm = Vm(new Settings(), dialogs, eraser: eraser, stop: () => log.Add("stop"));
        vm.DataDeleted += () => log.Add("wizard");

        await vm.DeleteAllDataCommand.ExecuteAsync(null);

        Assert.Equal(["stop", "erase", "wizard"], log);
        Assert.True(eraser.DeleteModels);
    }

    [Fact]
    public async Task Settings_DeleteAll_Cancelled_DoesNothing()
    {
        var log = new List<string>();
        var vm = Vm(new Settings(), new Dialogs(), eraser: new FakeEraser(log), stop: () => log.Add("stop"));
        vm.DataDeleted += () => log.Add("wizard");

        await vm.DeleteAllDataCommand.ExecuteAsync(null);

        Assert.Empty(log);
    }

    [Fact]
    public async Task Settings_DeleteAll_FileInUse_ShowsMessageAndStaysOnSettings()
    {
        var log = new List<string>();
        var eraser = new FakeEraser(log) { Fail = new IOException("library.db is in use") };
        var vm = Vm(new Settings(), new Dialogs { DeleteAllAnswer = DeleteAllChoice.KeepModels }, eraser: eraser);
        vm.DataDeleted += () => log.Add("wizard");
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await vm.DeleteAllDataCommand.ExecuteAsync(null);

        Assert.Equal(["erase"], log);
        Assert.Contains("Settings_DeleteFailed", vm.Message);
        Assert.True(vm.HasMessage);                                                    // shown in an InfoBar
        Assert.Contains(nameof(SettingsViewModel.HasMessage), raised);                 // ...which opens because the change is announced
        vm.DismissMessage();                                                           // its close button
        Assert.False(vm.HasMessage);
    }
}
