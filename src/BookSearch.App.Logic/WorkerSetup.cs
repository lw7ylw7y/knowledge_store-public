using BookSearch.Core.Worker;
using BookSearch.Models.Management;

namespace BookSearch.App;

/// <summary>
/// Everything needed to run <c>BookSearch.Worker.exe</c> from the app: whether it can run yet, its command line, and
/// starting it. The paths are looked up on every start, so putting <c>llama-server.exe</c> in place later just works.
/// </summary>
public sealed class WorkerSetup(AppPaths paths, ModelManifest manifest, ModelStore store, string appFolder, IStrings strings,
    ISettingsStore? settings = null)
    : IWorkerLauncher
{
    public string WorkerExe { get; } = Path.Combine(appFolder, "BookSearch.Worker.exe");

    /// <summary>Why indexing can't run right now, or null if it can.</summary>
    public string? CheckPrerequisites()
    {
        if (!File.Exists(WorkerExe)) return strings.Format("Library_NoWorker", WorkerExe);
        if (ServerLocator.Find(paths, appFolder) is null)
            return strings.Format("Library_NoServer", ServerLocator.Candidates(paths, appFolder)[0]);
        if (!File.Exists(store.PathFor(manifest.Embedding))) return strings.Get("Library_NoEmbeddingModel");
        return null;
    }

    /// <summary>The chunk size and overlap from Settings are read when the worker starts, so a change applies from the next start.</summary>
    public IReadOnlyList<string> BuildArguments(string serverExe, int parentPid)
    {
        List<string> args =
        [
            "--data", paths.DataFolder,
            "--server-exe", serverExe,
            "--embedding-model", store.PathFor(manifest.Embedding),
            "--parent-pid", parentPid.ToString(),
        ];
        if (settings?.Load() is { } s)
        {
            args.AddRange(["--chunk-tokens", SettingsLimits.ChunkTokens(s).ToString(),
                           "--chunk-overlap", SettingsLimits.OverlapTokens(s).ToString()]);
        }
        return args;
    }

    public IWorkerProcess Start()
    {
        var server = ServerLocator.Find(paths, appFolder)
                     ?? throw new FileNotFoundException("llama-server.exe was not found.", ServerLocator.FileName);
        return new WorkerProcessLauncher(WorkerExe, BuildArguments(server, Environment.ProcessId)).Start();
    }
}
