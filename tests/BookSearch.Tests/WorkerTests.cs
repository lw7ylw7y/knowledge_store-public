using BookSearch.Core;
using BookSearch.Core.Chunking;
using BookSearch.Core.Data;
using BookSearch.Core.Ingest;
using BookSearch.Core.Worker;
using BookSearch.Models;

namespace BookSearch.Tests;

public class WorkerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeEmbedder _embedder = new();
    private readonly List<string> _tempFiles = [];

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private IngestWorker Worker(IEmbedder? embedder = null, int batchSize = 2, Action? onBatch = null)
    {
        IngestWorker? worker = null;
        var service = new IngestService(_db.Conn, embedder ?? _embedder, new WordCountTokenizer(),
            new PunctuationSentenceSplitter(), new IngestOptions { BatchSize = batchSize },
            beforeBatch: async ct => { onBatch?.Invoke(); await worker!.WaitWhilePausedAsync(ct); });
        return worker = new IngestWorker(_db.Conn, service) { PausePollInterval = TimeSpan.FromMilliseconds(20) };
    }

    private IngestService Adder() => new(_db.Conn, _embedder, new WordCountTokenizer(), new PunctuationSentenceSplitter());

    private async Task<long> QueueAsync(string fixture)
    {
        var path = Path.Combine(Path.GetTempPath(), $"worker-{Guid.NewGuid():N}-{fixture}");
        File.Copy(Fixture(fixture), path);
        _tempFiles.Add(path);
        return (await Adder().AddAsync(path)).BookId;
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var f in _tempFiles) File.Delete(f);
    }

    [Fact]
    public async Task Worker_EmptyQueue_Exits()
    {
        var result = await Worker().RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new WorkerRunResult(0, 0), result);
        Assert.Equal(0, _embedder.Calls);
    }

    [Fact]
    public async Task Worker_QueuedBooks_AreIndexedOneAfterAnotherThenExits()
    {
        var a = await QueueAsync("notes.md");
        var b = await QueueAsync("plain.txt");

        var result = await Worker(batchSize: 16).RunAsync();

        Assert.Equal(2, result.Indexed);
        Assert.Equal(BookStatus.Ready, _db.Books.GetById(a)!.Status);
        Assert.Equal(BookStatus.Ready, _db.Books.GetById(b)!.Status);
        Assert.Empty(_db.Jobs.ListRunnable());
    }

    [Fact]
    public async Task Worker_Pause_StopsAfterCurrentBatch()
    {
        var id = await QueueAsync("alice.epub");
        using var other = Database.Open(Path.Combine(Path.GetDirectoryName(_db.Conn.DataSource)!, "library.db"));
        var control = new WorkerControlRepository(other);
        var batches = 0;
        var embedder = new FakeEmbedder { BeforeCall = n => { if (n == 2) control.SetPaused(true); } };

        var run = Task.Run(() => Worker(embedder, batchSize: 2, onBatch: () => batches++).RunAsync());
        while (embedder.Calls < 2) await Task.Delay(20).WaitAsync(TimeSpan.FromSeconds(60));   // extraction and chunking come first
        await Task.Delay(300);                                              // time for a wrongly started batch 3

        Assert.False(run.IsCompleted);
        Assert.Equal(2, embedder.Calls);                                   // batch 2 was embedded; batch 3 never started
        Assert.Equal(4, new ChunkRepository(other).CountChunks(id));        // and its rows were written before pausing
        Assert.Equal(BookStatus.Indexing, new BookRepository(other).GetById(id)!.Status);

        control.SetPaused(false);
        var result = await run.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(1, result.Indexed);
        Assert.Equal(BookStatus.Ready, new BookRepository(other).GetById(id)!.Status);
    }

    [Fact]
    public async Task Worker_TwoCrashesOnSameBook_MarksFailed()
    {
        var crasher = await QueueAsync("alice.epub");
        var fine = await QueueAsync("notes.md");
        _db.Jobs.BeginAttempt(crasher);   // what two killed worker processes leave behind
        _db.Jobs.BeginAttempt(crasher);

        var result = await Worker(batchSize: 16).RunAsync();

        var book = _db.Books.GetById(crasher)!;
        Assert.Equal(BookStatus.Failed, book.Status);
        Assert.Contains("stopped the background worker twice", book.StatusReason);
        Assert.Equal(BookStatus.Ready, _db.Books.GetById(fine)!.Status);   // the next book is not held up
        Assert.Equal(new WorkerRunResult(1, 1), result);
        Assert.Equal(0, _db.Chunks.CountChunks(crasher));
    }

    [Fact]
    public async Task Worker_OneCrash_RetriesAndProgressClearsTheCount()
    {
        var id = await QueueAsync("notes.md");
        _db.Jobs.BeginAttempt(id);   // one earlier crash

        await Worker(batchSize: 1).RunAsync();

        Assert.Equal(BookStatus.Ready, _db.Books.GetById(id)!.Status);
        Assert.Equal(0, _db.Jobs.Get(id)!.Attempts);
    }

    [Fact]
    public async Task Worker_Cancelled_IsNotCountedAsACrash()
    {
        var id = await QueueAsync("alice.epub");
        using var cts = new CancellationTokenSource();
        var embedder = new FakeEmbedder { BeforeCall = _ => cts.Cancel() };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Worker(embedder, batchSize: 2).RunAsync(ct: cts.Token));

        Assert.Equal(0, _db.Jobs.Get(id)!.Attempts);
        Assert.Equal(BookStatus.Indexing, _db.Books.GetById(id)!.Status);   // resumable
    }

    [Fact]
    public async Task Worker_ModelUnavailable_FailsThatBookAndMovesOn()
    {
        var id = await QueueAsync("notes.md");
        var embedder = new FakeEmbedder { BeforeCall = _ => throw new ModelUnavailableException("server did not start") };

        var result = await Worker(embedder).RunAsync();

        Assert.Equal(new WorkerRunResult(0, 1), result);
        var book = _db.Books.GetById(id)!;
        Assert.Equal(BookStatus.Failed, book.Status);
        Assert.Contains("embedding model is not available", book.StatusReason);
    }

    [Theory]
    [InlineData(100, 8, 8)]
    [InlineData(50, 8, 4)]
    [InlineData(25, 8, 2)]
    [InlineData(25, 2, 1)]    // 0.5 rounds to 1, never 0
    [InlineData(1, 16, 1)]
    [InlineData(100, 1, 1)]
    public void Worker_CpuCap_SetsThreadCount(int cap, int cores, int expected) =>
        Assert.Equal(expected, CpuCap.ThreadsFor(cap, cores));

    [Fact]
    public async Task Worker_CpuCapChange_RestartsEmbeddingServerWithNewThreadCount()
    {
        var threadsSeen = new List<int>();
        IServerHandle? last = null;
        var launcher = new LambdaLauncher(role =>
        {
            var handle = new Handle();
            last = handle;
            return handle;
        });
        await using var supervisor = new ServerSupervisor(launcher);
        var throttle = new EmbeddingThrottle(supervisor, cores: 8);
        threadsSeen.Add(throttle.Threads);

        using (await supervisor.AcquireAsync(ServerRole.Embedding)) { }
        await throttle.ApplyAsync(100);                                   // unchanged: the server stays up
        Assert.Equal(ServerState.Ready, supervisor.GetState(ServerRole.Embedding));

        await throttle.ApplyAsync(25);                                    // changed: stopped, restarts on next use
        Assert.Equal(2, throttle.Threads);
        Assert.Equal(ServerState.Stopped, supervisor.GetState(ServerRole.Embedding));
        Assert.Equal(8, threadsSeen[0]);
        Assert.Equal(1, launcher.Starts);
        using (await supervisor.AcquireAsync(ServerRole.Embedding)) { }
        Assert.Equal(2, launcher.Starts);
        Assert.NotNull(last);
    }

    private sealed class Handle : IServerHandle
    {
        public Uri BaseUri { get; } = new("http://127.0.0.1:9100/");
        public string ApiKey => "k";
        public bool HasExited { get; private set; }
        public ValueTask DisposeAsync() { HasExited = true; return ValueTask.CompletedTask; }
    }

    private sealed class LambdaLauncher(Func<ServerRole, IServerHandle> make) : IServerLauncher
    {
        public int Starts { get; private set; }
        public Task<IServerHandle> StartAsync(ServerRole role, CancellationToken ct)
        {
            Starts++;
            return Task.FromResult(make(role));
        }
    }

    // ---- poller -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Poller_ReadsProgress_EmitsChangedRowsOnly()
    {
        var a = await QueueAsync("notes.md");
        var b = await QueueAsync("plain.txt");
        var poller = new IngestProgressPoller(_db.Conn);

        var first = poller.Poll();
        Assert.Equal([a, b], first.Select(p => p.BookId));
        Assert.All(first, p => Assert.Equal(BookStatus.Queued, p.Status));

        Assert.Empty(poller.Poll());                                         // nothing changed

        _db.Jobs.SetStage(b, IngestStage.Embed, progress: 0.5);
        var second = poller.Poll();
        var changed = Assert.Single(second);
        Assert.Equal(b, changed.BookId);
        Assert.Equal(0.5, changed.Progress);

        await Worker(batchSize: 16).RunAsync();
        Assert.Equal([a, b], poller.Poll().Select(p => p.BookId));          // both finished
        Assert.Empty(poller.Poll());

        _db.Books.Delete(a);
        Assert.Empty(poller.Poll());                                         // a removed book is simply forgotten
    }

    // ---- host ---------------------------------------------------------------------------------------------

    private sealed class FakeWorkerLauncher(Func<int, int> exitCodeForStart, Action<int>? onStart = null) : IWorkerLauncher
    {
        public int Starts { get; private set; }
        public IWorkerProcess Start()
        {
            var n = ++Starts;
            onStart?.Invoke(n);
            return new Proc(Task.FromResult(exitCodeForStart(n)));
        }
        private sealed record Proc(Task<int> Exited) : IWorkerProcess;
    }

    [Fact]
    public async Task Host_NoJobs_DoesNotStartWorker()
    {
        var launcher = new FakeWorkerLauncher(_ => 0);
        await new WorkerHost(_db.Jobs, launcher).EnsureRunning();
        Assert.Equal(0, launcher.Starts);
    }

    [Fact]
    public async Task Host_Crash_RestartsWorkerUntilQueueIsEmpty()
    {
        var id = await QueueAsync("notes.md");
        var launcher = new FakeWorkerLauncher(n => n == 1 ? 137 : 0, onStart: n =>
        {
            if (n == 1) _db.Jobs.BeginAttempt(id);                           // the crashed run had started the book
        });
        var host = new WorkerHost(_db.Jobs, launcher);

        await host.EnsureRunning().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, launcher.Starts);
        Assert.Equal(1, host.Restarts);
        Assert.False(host.GaveUp);
    }

    [Fact]
    public async Task Host_WorkerThatNeverGetsGoing_IsGivenUp()
    {
        await QueueAsync("notes.md");
        var launcher = new FakeWorkerLauncher(_ => 1);                       // dies at once, touches nothing
        var host = new WorkerHost(_db.Jobs, launcher);

        await host.EnsureRunning().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(host.GaveUp);
        Assert.Equal(WorkerHost.MaxCrashesWithoutProgress, launcher.Starts);
    }

    [Fact]
    public async Task Host_WorkerExecutableMissing_IsGivenUp()
    {
        await QueueAsync("notes.md");
        var host = new WorkerHost(_db.Jobs, new ThrowingLauncher());

        await host.EnsureRunning().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(host.GaveUp);
    }

    private sealed class ThrowingLauncher : IWorkerLauncher
    {
        public IWorkerProcess Start() => throw new System.ComponentModel.Win32Exception("not found");
    }
}
