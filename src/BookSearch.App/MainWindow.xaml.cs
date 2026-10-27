using BookSearch.App.Ask;
using BookSearch.App.History;
using BookSearch.App.Pages;
using BookSearch.App.Reader;
using BookSearch.Models;
using BookSearch.App.Wizard;
using BookSearch.Models.Management;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BookSearch.App;

public sealed partial class MainWindow : Window
{
    private readonly AppPaths _paths = App.Services.GetRequiredService<AppPaths>();
    private readonly ISettingsStore _settingsStore = App.Services.GetRequiredService<ISettingsStore>();
    private readonly IStrings _strings = App.Services.GetRequiredService<IStrings>();
    private readonly ModelManifest _manifest = App.Services.GetRequiredService<ModelManifest>();
    private readonly IModelProvisioner _models = App.Services.GetRequiredService<IModelProvisioner>();
    private readonly long _ramBytes = HardwareTiers.DetectTotalRamBytes();
    private LibraryService? _library;
    private ServerSupervisor? _supervisor;
    private AskService? _ask;
    private ReaderService? _readerService;
    private SearchHistoryStore? _historyStore;
    private SummaryCoordinator? _coordinator;

    public MainWindow()
    {
        InitializeComponent();
        Title = _strings.Get("App_Title");
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Docent.ico")); }   // title bar and taskbar button
        catch (Exception e) { CrashLog.Write(e); }                                                      // the default icon is only a cosmetic loss
        ApplyWindowSize();
        Closed += (_, _) => Shutdown();

        var recommended = HardwareTiers.Select(_ramBytes);
        if (Startup.Decide(_settingsStore.Load(), _manifest, recommended, _models) == StartPage.Wizard) ShowWizard();
        else ShowShell();
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// The smallest supported window (800 x 600) and a comfortable first size, scaled for the display the window opens on (the
    /// minimum is not recomputed if the window is later moved to a display with another scale).
    /// </summary>
    private void ApplyWindowSize()
    {
        try
        {
            var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
            var scale = dpi > 0 ? dpi / 96.0 : 1.0;
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = (int)Math.Round(WindowSizing.MinWidth * scale);
                presenter.PreferredMinimumHeight = (int)Math.Round(WindowSizing.MinHeight * scale);
            }
            // The first size, but never larger than the screen's work area (a 1366 x 768 laptop) and never below the minimum.
            var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var minW = (int)Math.Round(WindowSizing.MinWidth * scale);
            var minH = (int)Math.Round(WindowSizing.MinHeight * scale);
            var width = Math.Max(minW, Math.Min((int)Math.Round(WindowSizing.DefaultWidth * scale), work.Width));
            var height = Math.Max(minH, Math.Min((int)Math.Round(WindowSizing.DefaultHeight * scale), work.Height));
            AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        }
        catch (Exception e) { CrashLog.Write(e); }                                                      // a default window is only a cosmetic loss
    }

    /// <summary>Stops everything the app started: the worker (it sees the app is gone and stops itself) and the model servers.</summary>
    private void Shutdown()
    {
        _coordinator?.Dispose();           // a summary being written stays "writing" in the history and is completed next time
        _library?.Dispose();
        _ask?.Dispose();
        _readerService?.Dispose();
        _historyStore?.Dispose();
        _supervisor?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        // Settings can stop everything and later the window closes: never dispose the same object twice.
        _coordinator = null;
        _library = null;
        _ask = null;
        _readerService = null;
        _historyStore = null;
        _supervisor = null;
    }

    private async Task LoadLibraryAsync(LibraryViewModel library)
    {
        try { await library.LoadAsync(); }
        catch (Exception e) { CrashLog.Write(e); }
    }

    /// <summary>A different model size was chosen: reload with it, or run the wizard when its models aren't installed yet.</summary>
    private void OnTierChanged()
    {
        var recommended = HardwareTiers.Select(_ramBytes);
        if (Startup.Decide(_settingsStore.Load(), _manifest, recommended, _models) == StartPage.Wizard)
        {
            Shutdown();
            ShowWizard();
        }
        else ShowShell();
    }

    /// <summary>Everything was deleted (the processes are already stopped): back to the first-run wizard.</summary>
    private void OnDataDeleted() => ShowWizard();

    private void ShowWizard()
    {
        var wizard = new WizardViewModel(_manifest, _models, _settingsStore, _strings, _ramBytes);
        wizard.Completed += ShowShell;
        Root.Children.Clear();
        Root.Children.Add(new WizardPage(wizard, this));
    }

    private void ShowShell()
    {
        Root.Children.Clear();
        try
        {
            var settings = _settingsStore.Load();
            var status = LibraryDatabase.Initialize(_paths);
            var recommended = HardwareTiers.Select(_ramBytes);
            var dialogs = new AppDialogs(_strings);
            var setup = new WorkerSetup(_paths, _manifest, App.Services.GetRequiredService<ModelStore>(),
                AppContext.BaseDirectory, _strings, _settingsStore);
            Shutdown();
            _library = new LibraryService(_paths, setup, setup.CheckPrerequisites);
            var options = new ServerOptionsFactory(_paths, _manifest, App.Services.GetRequiredService<ModelStore>(),
                AppContext.BaseDirectory, () => _settingsStore.Load().TierOverride ?? recommended,
                () => SettingsLimits.ChatContext(_settingsStore.Load(), HardwareTiers.SelectChatContext(_ramBytes)));
            _supervisor = new ServerSupervisor(new LlamaServerLauncher(options.Create));
            _ask = new AskService(_paths, _supervisor, () => SettingsLimits.ResultCount(_settingsStore.Load()));
            _readerService = new ReaderService(_paths);
            var library = new LibraryViewModel(_library, dialogs, _strings, new ShellFileLauncher());
            library.SetElevated(ProcessElevation.IsElevated());
            var summarizer = new Summarizer(_supervisor, new SupervisedChat(_supervisor), chunks: _ask,
                contextLevel: () => (BookSearch.Core.Summarize.SummaryContextLevel)Math.Clamp(_settingsStore.Load().SummaryContext, 0, 2));
            _historyStore = new SearchHistoryStore(_paths);
            _coordinator = new SummaryCoordinator(_historyStore, summarizer, _ask, () => _settingsStore.Load().AutoSummary);
            var settingsVm = new SettingsViewModel(_paths, _settingsStore, recommended, status, _strings, _coordinator, dialogs,
                _library, new DataEraser(_paths), Shutdown, HardwareTiers.SelectChatContext(_ramBytes));
            settingsVm.TierChanged += OnTierChanged;
            settingsVm.DataDeleted += OnDataDeleted;
            Root.Children.Add(new ShellPage(library, new AskViewModel(_ask, _strings, _coordinator, _settingsStore, _historyStore, dialogs: dialogs,
                    rewriter: new FollowUpRewriter(new SupervisedChat(_supervisor), log: CrashLog.Note,
                        afterSummary: _coordinator.WhenIdleAsync,
                        chatReady: () => _supervisor.GetState(ServerRole.Chat) == ServerState.Ready),
                    translator: new QuestionTranslator(new SupervisedChat(_supervisor), log: CrashLog.Note,
                        afterSummary: _coordinator.WhenIdleAsync,
                        chatReady: () => _supervisor.GetState(ServerRole.Chat) == ServerState.Ready)),
                settingsVm,
                new ReaderViewModel(_readerService, new ShellFileLauncher(), _strings),
                new HistoryViewModel(_historyStore, _coordinator, _strings, dialogs, _settingsStore), dialogs));
            _ = _coordinator.PrewarmAsync();            // load the language model now, so the first summary doesn't wait for it
            _ = _coordinator.ResumeUnfinishedAsync();   // summaries left unfinished when the app last closed are completed now
            _ = LoadLibraryAsync(library);
        }
        catch (Exception e)
        {
            CrashLog.Write(e);
            Root.Children.Add(new StartupErrorPage(e.Message));
        }
    }
}
