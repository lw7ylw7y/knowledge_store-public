using System.Diagnostics;
using BookSearch.Core.Data;
using BookSearch.Core.Ingest;
using BookSearch.Core.Worker;
using BookSearch.Models;

// BookSearch.Worker: indexes queued books in the background, then exits.
//   BookSearch.Worker [--data <folder>] [--config <config.json> | --server-exe <path> --embedding-model <path>]
//                     [--parent-pid <id>] [--chunk-tokens <n>] [--chunk-overlap <n>]
// The app passes --server-exe and --embedding-model; --config is for use from a console. With --parent-pid the worker
// stops cleanly (resumable, not counted as a crash) when that process exits, so closing the app never leaves it behind.
// Exit codes: 0 = queue empty, 1 = could not start (bad arguments or config), 130 = stopped on request.

var dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BookSearch");
string? configPath = null, serverExe = null, embeddingModel = null;
int? parentPid = null;
var chunking = new BookSearch.Core.Chunking.ChunkOptions();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--data" && i + 1 < args.Length) dataFolder = args[++i];
    else if (args[i] == "--config" && i + 1 < args.Length) configPath = args[++i];
    else if (args[i] == "--server-exe" && i + 1 < args.Length) serverExe = args[++i];
    else if (args[i] == "--embedding-model" && i + 1 < args.Length) embeddingModel = args[++i];
    else if (args[i] == "--parent-pid" && i + 1 < args.Length && int.TryParse(args[i + 1], out var pid)) { parentPid = pid; i++; }
    else if (args[i] == "--chunk-tokens" && i + 1 < args.Length && int.TryParse(args[i + 1], out var tokens) && tokens > 0)
    { chunking = chunking with { TargetTokens = tokens }; i++; }
    else if (args[i] == "--chunk-overlap" && i + 1 < args.Length && int.TryParse(args[i + 1], out var overlap) && overlap >= 0)
    { chunking = chunking with { OverlapTokens = overlap }; i++; }
    else { Console.Error.WriteLine($"Unknown argument: {args[i]}"); return 1; }
}

M0Config config;
try
{
    config = serverExe is not null && embeddingModel is not null
        ? new M0Config { ServerExe = serverExe, EmbeddingModel = embeddingModel }
        : M0Config.Load(configPath ?? Path.Combine(dataFolder, "config.json"));
    foreach (var file in new[] { config.ServerExe, config.EmbeddingModel })
        if (!File.Exists(file)) throw new FileNotFoundException($"File not found: {file}", file);
}
catch (FileNotFoundException e) { Console.Error.WriteLine(e.Message); return 1; }

try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; }
catch (Exception e) when (e is PlatformNotSupportedException or InvalidOperationException or System.ComponentModel.Win32Exception)
{ /* best effort; llama-server children inherit the priority where the OS allows it */ }

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
if (parentPid is { } parent)
{
    try
    {
        var parentProcess = Process.GetProcessById(parent);
        _ = parentProcess.WaitForExitAsync().ContinueWith(_ => cts.Cancel());   // the app is gone: stop cleanly
    }
    catch (ArgumentException) { return 130; }   // the parent already exited
}

using var conn = Database.Open(Path.Combine(dataFolder, "library.db"));
Migrator.Migrate(conn);

var control = new WorkerControlRepository(conn);
ServerSupervisor? supervisor = null;
var throttle = new Lazy<EmbeddingThrottle>(() => new EmbeddingThrottle(supervisor!, Environment.ProcessorCount));
supervisor = new ServerSupervisor(new LlamaServerLauncher(role => new LlamaServerOptions(
    config.ServerExe, config.EmbeddingModel, role, throttle.Value.Threads)));
await using var _s = supervisor;
await using var embedder = new SupervisedEmbedder(supervisor);

IngestWorker? worker = null;
var ingest = new IngestService(conn, embedder, embedder, options: new IngestOptions { Chunking = chunking }, beforeBatch: async ct =>
{
    await worker!.WaitWhilePausedAsync(ct);
    await throttle.Value.ApplyAsync(control.Get().CpuCapPercent);
});
worker = new IngestWorker(conn, ingest);
await throttle.Value.ApplyAsync(control.Get().CpuCapPercent);

try
{
    var progress = new Progress<IngestProgress>(p =>
        Console.WriteLine($"book {p.BookId}: {p.Stage} {p.ChunksDone}/{p.ChunksTotal}"));   // counts only, never book text
    var result = await worker.RunAsync(progress, cts.Token);
    Console.WriteLine($"Done: {result.Indexed} indexed, {result.Failed} failed.");
    return 0;
}
catch (OperationCanceledException)
{
    return 130;
}
