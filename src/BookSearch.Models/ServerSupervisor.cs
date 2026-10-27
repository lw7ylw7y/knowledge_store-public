using BookSearch.Core;

namespace BookSearch.Models;

public enum ServerState { Stopped, Starting, Ready, Failed }

/// <summary>A running model server, as the supervisor sees it (<see cref="LlamaServerProcess"/> in production).</summary>
public interface IServerHandle : IAsyncDisposable
{
    Uri BaseUri { get; }
    string ApiKey { get; }
    bool HasExited { get; }
}

/// <summary>Starts a server and returns once it is healthy; throws <see cref="ModelUnavailableException"/> if it can't.</summary>
public interface IServerLauncher
{
    Task<IServerHandle> StartAsync(ServerRole role, CancellationToken ct);
}

/// <summary>Starts real <c>llama-server</c> processes from per-role options.</summary>
public sealed class LlamaServerLauncher(Func<ServerRole, LlamaServerOptions> optionsFor) : IServerLauncher
{
    public async Task<IServerHandle> StartAsync(ServerRole role, CancellationToken ct)
    {
        var server = new LlamaServerProcess(optionsFor(role));
        await server.StartAsync(ct: ct).ConfigureAwait(false);
        return server;
    }
}

/// <summary>
/// Use of a running server. Keep it until the request (including a streamed reply) is finished: the idle timer
/// never stops a server that has an open lease.
/// </summary>
public sealed class ServerLease : IDisposable
{
    private readonly Action _release;
    private int _released;

    internal ServerLease(Uri baseUri, string apiKey, Action release)
    {
        BaseUri = baseUri;
        ApiKey = apiKey;
        _release = release;
    }

    public Uri BaseUri { get; }
    public string ApiKey { get; }

    /// <summary>An <see cref="HttpClient"/> for this server with the API key attached. The caller disposes it.</summary>
    public HttpClient CreateHttpClient()
    {
        var http = new HttpClient { BaseAddress = BaseUri, Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);
        return http;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) _release();
    }
}

/// <summary>
/// Owns the embedding and chat servers: starts each on first use (one process even for concurrent requests),
/// notices when one has died and restarts it once (if that restart fails the state becomes Failed and the request
/// gets a <see cref="ModelUnavailableException"/>; the next request tries again), and stops each after it has been
/// unused for <see cref="IdleTimeout"/>.
/// </summary>
public sealed class ServerSupervisor : IAsyncDisposable
{
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(10);

    private sealed class Slot
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public IServerHandle? Handle;
        public volatile ServerState State = ServerState.Stopped;
        public int ActiveLeases;
        public long LastUsedTicks;   // UTC ticks of the last start or lease release
    }

    private readonly IServerLauncher _launcher;
    private readonly TimeProvider _clock;
    private readonly Dictionary<ServerRole, Slot> _slots = new()
    {
        [ServerRole.Embedding] = new Slot(),
        [ServerRole.Chat] = new Slot(),
    };
    private readonly ITimer _idleTimer;
    private volatile bool _disposed;

    public ServerSupervisor(IServerLauncher launcher, TimeProvider? clock = null, TimeSpan? idleTimeout = null)
    {
        _launcher = launcher;
        _clock = clock ?? TimeProvider.System;
        IdleTimeout = idleTimeout ?? DefaultIdleTimeout;
        var period = TimeSpan.FromSeconds(Math.Clamp(IdleTimeout.TotalSeconds / 4, 1, 30));
        _idleTimer = _clock.CreateTimer(_ => _ = StopIdleServersAsync(), null, period, period);
    }

    public TimeSpan IdleTimeout { get; }

    /// <summary>Raised on a background thread when a server's state changes (for the UI's status line).</summary>
    public event Action<ServerRole, ServerState>? StateChanged;

    public ServerState GetState(ServerRole role) => _slots[role].State;

    /// <summary>Returns a lease on the role's server, starting or restarting it first if needed.</summary>
    public async Task<ServerLease> AcquireAsync(ServerRole role, CancellationToken ct = default)
    {
        var slot = _slots[role];
        await slot.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (slot.Handle is { HasExited: false } running) return Lease(slot, running);

            if (slot.Handle is not null)   // it was running and has died: clean up, then restart once below
            {
                await slot.Handle.DisposeAsync().ConfigureAwait(false);
                slot.Handle = null;
            }

            SetState(role, slot, ServerState.Starting);
            IServerHandle handle;
            try
            {
                handle = await _launcher.StartAsync(role, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SetState(role, slot, ServerState.Stopped);
                throw;
            }
            catch (Exception e)
            {
                SetState(role, slot, ServerState.Failed);
                throw e as ModelUnavailableException
                      ?? new ModelUnavailableException($"The {role} model server could not be started: {e.Message}", e);
            }

            slot.Handle = handle;
            SetState(role, slot, ServerState.Ready);
            return Lease(slot, handle);
        }
        finally { slot.Gate.Release(); }
    }

    /// <summary>Stops the role's server now (for example to restart it with a different thread count on the next request).</summary>
    public async Task StopAsync(ServerRole role)
    {
        var slot = _slots[role];
        await slot.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (slot.Handle is null) return;
            await slot.Handle.DisposeAsync().ConfigureAwait(false);
            slot.Handle = null;
            SetState(role, slot, ServerState.Stopped);
        }
        finally { slot.Gate.Release(); }
    }

    private ServerLease Lease(Slot slot, IServerHandle handle)
    {
        Interlocked.Increment(ref slot.ActiveLeases);
        return new ServerLease(handle.BaseUri, handle.ApiKey, () =>
        {
            Interlocked.Exchange(ref slot.LastUsedTicks, _clock.GetUtcNow().UtcTicks);
            Interlocked.Decrement(ref slot.ActiveLeases);
        });
    }

    private void SetState(ServerRole role, Slot slot, ServerState state)
    {
        if (slot.State == state) return;
        slot.State = state;
        if (state == ServerState.Ready) Interlocked.Exchange(ref slot.LastUsedTicks, _clock.GetUtcNow().UtcTicks);
        StateChanged?.Invoke(role, state);
    }

    /// <summary>Stops every server that has no open lease and has been unused for the idle timeout.</summary>
    internal async Task StopIdleServersAsync()
    {
        foreach (var (role, slot) in _slots)
        {
            try
            {
                if (!await slot.Gate.WaitAsync(0).ConfigureAwait(false)) continue;   // starting or being acquired: not idle
                try
                {
                    if (_disposed || slot.Handle is null || Volatile.Read(ref slot.ActiveLeases) > 0) continue;
                    var idle = _clock.GetUtcNow().UtcTicks - Interlocked.Read(ref slot.LastUsedTicks);
                    if (idle < IdleTimeout.Ticks) continue;
                    await slot.Handle.DisposeAsync().ConfigureAwait(false);
                    slot.Handle = null;
                    SetState(role, slot, ServerState.Stopped);
                }
                finally { slot.Gate.Release(); }
            }
            catch (ObjectDisposedException) { /* shutting down */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _idleTimer.DisposeAsync().ConfigureAwait(false);
        foreach (var (role, slot) in _slots)
        {
            await slot.Gate.WaitAsync().ConfigureAwait(false);   // let a start in progress finish first
            try
            {
                if (slot.Handle is not null) await slot.Handle.DisposeAsync().ConfigureAwait(false);
                slot.Handle = null;
                SetState(role, slot, ServerState.Stopped);
            }
            finally { slot.Gate.Release(); }
        }
    }
}
