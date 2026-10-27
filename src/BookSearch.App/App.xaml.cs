using BookSearch.Models;
using BookSearch.Models.Management;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace BookSearch.App;

public partial class App : Application
{

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => CrashLog.Write(e.Exception);
    }

    /// <summary>Services shared by the pages. View models and pages are created on demand and receive these.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>The main window, needed by the file pickers.</summary>
    public static Window MainWindow { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var paths = AppPaths.Default();
        Directory.CreateDirectory(paths.DataFolder);

        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddSingleton<ISettingsStore>(new SettingsStore(paths.SettingsPath));
        services.AddSingleton<IStrings, ResourceStrings>();
        services.AddSingleton(ModelManifest.LoadDefault());
        services.AddSingleton(new ModelStore(paths.ModelsFolder));
        services.AddSingleton(new ModelDownloader());
        services.AddSingleton<IModelProvisioner>(sp => new GatedModelProvisioner(
            new ModelProvisioner(sp.GetRequiredService<ModelManifest>(), sp.GetRequiredService<ModelStore>(), sp.GetRequiredService<ModelDownloader>(),
                new RuntimeInstaller(paths, sp.GetRequiredService<ModelStore>(), AppContext.BaseDirectory)),
            () => sp.GetRequiredService<ISettingsStore>().Load().DownloadsAllowed));
        Services = services.BuildServiceProvider();

        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
