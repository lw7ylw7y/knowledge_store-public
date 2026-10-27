using BookSearch.Core.Data;

namespace BookSearch.Core.Worker;

/// <summary>One running worker process, as the host sees it.</summary>
public interface IWorkerProcess
{
    /// <summary>Completes with the exit code when the process ends (any non-zero code, or a kill, counts as a crash).</summary>
    Task<int> Exited { get; }
}

public interface IWorkerLauncher
{
    IWorkerProcess Start();
}

/// <summary>
/// Run by the app: starts the worker when there is something to index, restarts it when it crashes, and lets it go
/// when it exits normally. Restarts can't loop forever: <see cref="IngestWorker"/> fails a book that crashed it twice,
/// and a worker that dies <see cref="MaxCrashesWithoutProgress"/> times in a row without changing anything in the
/// queue (it never even started, say) is given up on (<see cref="GaveUp"/>).
/// </summary>
public sealed class WorkerHost(IngestJobRepository jobs, IWorkerLauncher launcher)
{
    public const int MaxCrashesWithoutProgress = 3;

    private readonly object _gate = new();      // guards _running and every use of the repository (one connection)
    private Task? _running;
    private volatile bool _stopped;

    public int Restarts { get; private set; }
    public bool GaveUp { get; private set; }

    /// <summary>The app is closing: the worker was told to stop (it exits on its own), so don't restart it.</summary>
    public void Stop() => _stopped = true;

    /// <summary>Starts the supervising loop if jobs exist and it isn't running. Returns immediately.</summary>
    public Task EnsureRunning()
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false }) return _running;
            if (_stopped || jobs.ListRunnable().Count == 0) return Task.CompletedTask;
            GaveUp = false;
            return _running = Task.Run(SuperviseAsync);
        }
    }

    private async Task SuperviseAsync()
    {
        var stuck = 0;
        while (true)
        {
            var before = QueueSignature();
            int exit;
            try { exit = await launcher.Start().Exited.ConfigureAwait(false); }
            catch (Exception)
            {
                GaveUp = true;   // the worker program can't be started at all
                return;
            }
            if (exit == 0 || _stopped) return;

            var after = QueueSignature();
            if (after.Count == 0) return;
            stuck = after == before ? stuck + 1 : 0;
            if (stuck >= MaxCrashesWithoutProgress) { GaveUp = true; return; }
            Restarts++;
        }
    }

    /// <summary>Changes whenever a book is started, finished, fails, or writes a batch.</summary>
    private (int Count, long Attempts, long Cursor) QueueSignature()
    {
        List<IngestJob> runnable;
        lock (_gate) runnable = jobs.ListRunnable();
        return (runnable.Count, runnable.Sum(j => (long)j.Attempts), runnable.Sum(j => (long)j.NextChunkIndex));
    }
}
