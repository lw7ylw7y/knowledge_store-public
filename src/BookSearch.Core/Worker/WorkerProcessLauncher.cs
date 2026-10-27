using System.Diagnostics;

namespace BookSearch.Core.Worker;

/// <summary>Starts <c>BookSearch.Worker</c> as a hidden child process.</summary>
public sealed class WorkerProcessLauncher(string workerExePath, IReadOnlyList<string> arguments) : IWorkerLauncher
{
    private sealed class Running(Process process) : IWorkerProcess
    {
        public Task<int> Exited { get; } = WaitAsync(process);

        private static async Task<int> WaitAsync(Process process)
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            var code = process.ExitCode;
            process.Dispose();
            return code;
        }
    }

    public IWorkerProcess Start()
    {
        var info = new ProcessStartInfo(workerExePath) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        return new Running(Process.Start(info) ?? throw new InvalidOperationException("Could not start the worker."));
    }
}
