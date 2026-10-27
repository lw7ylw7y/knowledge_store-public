using BookSearch.Core;
using BookSearch.Models;
using BookSearch.Models.Management;

namespace BookSearch.App;

/// <summary>
/// Launch options for the app's own model servers (the worker has its own embedding server). Paths are looked up when a
/// server starts, so a missing file shows up as "model unavailable" at that moment instead of failing at start-up.
/// </summary>
public sealed class ServerOptionsFactory(AppPaths paths, ModelManifest manifest, ModelStore store, string appFolder,
    Func<ModelTier> tier, Func<int> chatContext)
{
    public LlamaServerOptions Create(ServerRole role)
    {
        var server = ServerLocator.Find(paths, appFolder)
                     ?? throw new ModelUnavailableException(
                         $"{ServerLocator.FileName} was not found. Put it at {ServerLocator.Candidates(paths, appFolder)[0]}.");
        var entry = role == ServerRole.Embedding ? manifest.Embedding : manifest.LlmFor(tier());
        if (!File.Exists(store.PathFor(entry)))
            throw new ModelUnavailableException($"The model file {entry.File} is not installed.");
        // Half the cores: enough for a question or a summary, and it leaves room for the background indexer.
        var threads = Math.Max(1, Environment.ProcessorCount / 2);
        return role == ServerRole.Embedding
            ? entry.ToServerOptions(server, store, threads)
            : entry.ToServerOptions(server, store, threads, chatContext());
    }
}
