using BookSearch.Core.Worker;

namespace BookSearch.Models;

/// <summary>
/// Applies the user's CPU cap to the embedding server. The cap becomes the server's thread count, which llama-server
/// only reads at start, so when the cap changes the server is stopped and the next request starts it with the new count.
/// </summary>
public sealed class EmbeddingThrottle(ServerSupervisor supervisor, int cores)
{
    /// <summary>Threads the embedding server should be started with right now.</summary>
    public int Threads { get; private set; } = Math.Max(1, cores);

    /// <summary>Call between batches (no request in flight) with the current cap from <c>worker_control</c>.</summary>
    public async Task ApplyAsync(int capPercent)
    {
        var wanted = CpuCap.ThreadsFor(capPercent, cores);
        if (wanted == Threads) return;
        Threads = wanted;
        await supervisor.StopAsync(ServerRole.Embedding).ConfigureAwait(false);
    }
}
