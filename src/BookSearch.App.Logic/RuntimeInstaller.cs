using System.IO.Compression;
using BookSearch.Models.Management;

namespace BookSearch.App;

/// <summary>
/// Installs the llama.cpp program (<c>llama-server.exe</c> and its libraries) from the pinned zip in the manifest into
/// <c>bin</c> in the data folder. The zip is downloaded or imported like a model (checked against its SHA-256 first) and is
/// unpacked only after that check. A <c>llama-server.exe</c> put there by hand, or one in <c>bin</c> next to the app, counts
/// as installed and is never replaced by a download.
/// </summary>
public sealed class RuntimeInstaller(AppPaths paths, ModelStore store, string appFolder)
{
    private string BinFolder => Path.Combine(paths.DataFolder, "bin");
    private string Marker => Path.Combine(BinFolder, "llama-server.sha256");

    /// <summary>True when a usable llama-server exists. A downloaded one (it has a marker) must be the pinned build.</summary>
    public bool IsInstalled(ModelEntry runtime)
    {
        if (File.Exists(Path.Combine(BinFolder, ServerLocator.FileName)))
        {
            return !File.Exists(Marker)
                   || File.ReadAllText(Marker).Trim().Equals(runtime.Sha256, StringComparison.OrdinalIgnoreCase);   // a newer pin replaces an older download
        }
        return File.Exists(Path.Combine(appFolder, "bin", ServerLocator.FileName));
    }

    /// <summary>Unpacks the already verified zip into <c>bin</c> (replacing a previous download) and deletes the zip.</summary>
    public void Install(ModelEntry runtime)
    {
        var zip = store.PathFor(runtime);
        var staging = BinFolder + ".new";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        try
        {
            ZipFile.ExtractToDirectory(zip, staging);        // refuses entries that would land outside the folder
            if (!File.Exists(Path.Combine(staging, ServerLocator.FileName)))
                throw new IOException($"{ServerLocator.FileName} is not directly inside {runtime.File}.");
            File.WriteAllText(Path.Combine(staging, "llama-server.sha256"), runtime.Sha256);
            if (Directory.Exists(BinFolder)) Directory.Delete(BinFolder, recursive: true);
            Directory.Move(staging, BinFolder);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
        File.Delete(zip);
    }
}
