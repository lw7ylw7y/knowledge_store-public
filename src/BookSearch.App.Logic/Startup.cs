using BookSearch.Models.Management;

namespace BookSearch.App;

public enum StartPage { Wizard, Library }

/// <summary>Decides whether the first-run wizard runs: until it has been completed, and again whenever a model file is gone.</summary>
public static class Startup
{
    public static StartPage Decide(AppSettings settings, ModelManifest manifest, ModelTier recommended, IModelProvisioner models)
    {
        if (!settings.WizardCompleted) return StartPage.Wizard;
        var tier = settings.TierOverride ?? recommended;
        return manifest.RequiredFor(tier).All(models.IsInstalled) ? StartPage.Library : StartPage.Wizard;
    }
}

/// <summary>What the wizard and start-up need from the model files; <see cref="ModelProvisioner"/> is the real one.</summary>
public interface IModelProvisioner
{
    /// <summary>Quick check at start-up: the file exists and has the pinned size. The full hash is checked after a download or import.</summary>
    bool IsInstalled(ModelEntry entry);

    Task DownloadAsync(ModelEntry entry, IProgress<DownloadProgress> progress, CancellationToken ct);

    /// <summary>Installs a model file chosen from disk (offline setup) and returns which model it was.</summary>
    Task<ModelEntry> ImportAsync(string path, IProgress<DownloadProgress> progress, CancellationToken ct);
}

/// <summary>Refuses downloads while "Allow model downloads" is off (NFR-9); importing a file from disk still works.</summary>
public sealed class GatedModelProvisioner(IModelProvisioner inner, Func<bool> downloadsAllowed) : IModelProvisioner
{
    public bool IsInstalled(ModelEntry entry) => inner.IsInstalled(entry);

    public Task DownloadAsync(ModelEntry entry, IProgress<DownloadProgress> progress, CancellationToken ct) =>
        downloadsAllowed() ? inner.DownloadAsync(entry, progress, ct) : Task.FromException(new DownloadsDisabledException());

    public Task<ModelEntry> ImportAsync(string path, IProgress<DownloadProgress> progress, CancellationToken ct) =>
        inner.ImportAsync(path, progress, ct);
}

public sealed class ModelProvisioner(ModelManifest manifest, ModelStore store, ModelDownloader downloader, RuntimeInstaller runtime)
    : IModelProvisioner
{
    public bool IsInstalled(ModelEntry entry)
    {
        if (entry.Role == ModelRole.Runtime) return runtime.IsInstalled(entry);
        var info = new FileInfo(store.PathFor(entry));
        return info.Exists && info.Length == entry.SizeBytes;
    }

    public async Task DownloadAsync(ModelEntry entry, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        await downloader.DownloadAsync(entry, store, progress, ct).ConfigureAwait(false);
        if (entry.Role == ModelRole.Runtime) await Task.Run(() => runtime.Install(entry), ct).ConfigureAwait(false);   // hash already checked
    }

    public async Task<ModelEntry> ImportAsync(string path, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        var entry = await ModelImporter.ImportAsync(path, manifest, store, progress, ct).ConfigureAwait(false);
        if (entry.Role == ModelRole.Runtime) await Task.Run(() => runtime.Install(entry), ct).ConfigureAwait(false);
        return entry;
    }
}
