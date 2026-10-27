using BookSearch.Core;
using BookSearch.Models;

namespace BookSearch.Tests;

public class ServerSupervisorTests
{
    private sealed class FakeHandle(int id) : IServerHandle
    {
        public Uri BaseUri { get; } = new($"http://127.0.0.1:{9000 + id}/");
        public string ApiKey => "key" + id;
        public bool HasExited { get; set; }
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; HasExited = true; return ValueTask.CompletedTask; }
    }

    private sealed class FakeLauncher : IServerLauncher
    {
        public List<(ServerRole Role, FakeHandle Handle)> Started { get; } = [];
        public int FailFromStart = int.MaxValue;   // the Nth start (1-based) and later throw
        public TimeSpan Delay = TimeSpan.Zero;

        public async Task<IServerHandle> StartAsync(ServerRole role, CancellationToken ct)
        {
            int n;
            lock (Started) n = Started.Count + 1;
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            if (n >= FailFromStart) throw new ModelUnavailableException("llama-server did not become ready.");
            var handle = new FakeHandle(n);
            lock (Started) Started.Add((role, handle));
            return handle;
        }
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Supervisor_FirstRequest_StartsServer()
    {
        var launcher = new FakeLauncher();
        await using var supervisor = new ServerSupervisor(launcher);
        var states = new List<ServerState>();
        supervisor.StateChanged += (role, s) => { if (role == ServerRole.Embedding) states.Add(s); };

        Assert.Equal(ServerState.Stopped, supervisor.GetState(ServerRole.Embedding));
        Assert.Empty(launcher.Started);

        using var lease = await supervisor.AcquireAsync(ServerRole.Embedding);

        Assert.Equal(new Uri("http://127.0.0.1:9001/"), lease.BaseUri);
        Assert.Equal("key1", lease.ApiKey);
        Assert.Equal(ServerState.Ready, supervisor.GetState(ServerRole.Embedding));
        Assert.Equal(ServerState.Stopped, supervisor.GetState(ServerRole.Chat));   // only the needed server starts
        Assert.Equal([ServerState.Starting, ServerState.Ready], states);

        using var again = await supervisor.AcquireAsync(ServerRole.Embedding);
        Assert.Single(launcher.Started);
    }

    [Fact]
    public async Task Supervisor_Crash_RestartsOnceThenFails()
    {
        var launcher = new FakeLauncher { FailFromStart = 3 };
        await using var supervisor = new ServerSupervisor(launcher);

        (await supervisor.AcquireAsync(ServerRole.Chat)).Dispose();
        launcher.Started[0].Handle.HasExited = true;                       // crash 1: the restart works
        using (var lease = await supervisor.AcquireAsync(ServerRole.Chat))
            Assert.Equal("key2", lease.ApiKey);
        Assert.True(launcher.Started[0].Handle.Disposed);                  // the dead process was cleaned up
        Assert.Equal(ServerState.Ready, supervisor.GetState(ServerRole.Chat));

        launcher.Started[1].Handle.HasExited = true;                       // crash 2: the restart fails
        var e = await Assert.ThrowsAsync<ModelUnavailableException>(() => supervisor.AcquireAsync(ServerRole.Chat));
        Assert.Contains("did not become ready", e.Message);
        Assert.Equal(ServerState.Failed, supervisor.GetState(ServerRole.Chat));
        Assert.Equal(2, launcher.Started.Count);
    }

    [Fact]
    public async Task Supervisor_AfterFailure_NextRequestTriesAgain()
    {
        var launcher = new FakeLauncher { FailFromStart = 1 };
        await using var supervisor = new ServerSupervisor(launcher);
        await Assert.ThrowsAsync<ModelUnavailableException>(() => supervisor.AcquireAsync(ServerRole.Chat));
        Assert.Equal(ServerState.Failed, supervisor.GetState(ServerRole.Chat));

        launcher.FailFromStart = int.MaxValue;
        using var lease = await supervisor.AcquireAsync(ServerRole.Chat);
        Assert.Equal(ServerState.Ready, supervisor.GetState(ServerRole.Chat));
    }

    [Fact]
    public async Task Supervisor_Idle10Min_StopsServer()
    {
        var clock = new FakeClock();
        var launcher = new FakeLauncher();
        await using var supervisor = new ServerSupervisor(launcher, clock);
        var lease = await supervisor.AcquireAsync(ServerRole.Chat);

        clock.Now += TimeSpan.FromMinutes(30);
        await supervisor.StopIdleServersAsync();
        Assert.Equal(ServerState.Ready, supervisor.GetState(ServerRole.Chat));   // a lease is still open (a streaming reply)

        lease.Dispose();                                                          // the timeout counts from the release
        clock.Now += TimeSpan.FromMinutes(9);
        await supervisor.StopIdleServersAsync();
        Assert.Equal(ServerState.Ready, supervisor.GetState(ServerRole.Chat));

        clock.Now += TimeSpan.FromMinutes(1);
        await supervisor.StopIdleServersAsync();
        Assert.Equal(ServerState.Stopped, supervisor.GetState(ServerRole.Chat));
        Assert.True(launcher.Started[0].Handle.Disposed);

        using var next = await supervisor.AcquireAsync(ServerRole.Chat);          // and it comes back on demand
        Assert.Equal(2, launcher.Started.Count);
    }

    [Fact]
    public async Task Supervisor_ConcurrentRequests_StartOnlyOneProcess()
    {
        var launcher = new FakeLauncher { Delay = TimeSpan.FromMilliseconds(100) };
        await using var supervisor = new ServerSupervisor(launcher);

        var leases = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => supervisor.AcquireAsync(ServerRole.Embedding)));

        Assert.Single(launcher.Started);
        Assert.All(leases, l => Assert.Equal("key1", l.ApiKey));
        foreach (var l in leases) l.Dispose();
    }

    [Fact]
    public async Task Supervisor_Cancelled_StartIsNotAFailure()
    {
        var launcher = new FakeLauncher { Delay = TimeSpan.FromSeconds(30) };
        await using var supervisor = new ServerSupervisor(launcher);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => supervisor.AcquireAsync(ServerRole.Chat, cts.Token));

        Assert.Equal(ServerState.Stopped, supervisor.GetState(ServerRole.Chat));
    }

    [Fact]
    public async Task Supervisor_Dispose_StopsBothServers()
    {
        var launcher = new FakeLauncher();
        var supervisor = new ServerSupervisor(launcher);
        (await supervisor.AcquireAsync(ServerRole.Embedding)).Dispose();
        (await supervisor.AcquireAsync(ServerRole.Chat)).Dispose();

        await supervisor.DisposeAsync();

        Assert.All(launcher.Started, s => Assert.True(s.Handle.Disposed));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => supervisor.AcquireAsync(ServerRole.Chat));
    }
}
