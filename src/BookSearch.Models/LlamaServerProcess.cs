using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using BookSearch.Core;

namespace BookSearch.Models;

public enum ServerRole { Embedding, Chat }

public sealed record LlamaServerOptions(
    string ServerExePath,
    string ModelPath,
    ServerRole Role,
    int Threads,
    int ContextSize = 0,
    IReadOnlyList<string>? ExtraArgs = null)
{
    public int EffectiveContextSize => ContextSize > 0 ? ContextSize : Role == ServerRole.Embedding ? 2048 : 4096;
}

/// <summary>
/// A llama.cpp <c>llama-server</c> child process bound to 127.0.0.1 on a free port and protected by a random API key,
/// so other programs on the machine can't use it. Disposing it kills the process tree.
/// </summary>
public sealed class LlamaServerProcess : IServerHandle
{
    private readonly LlamaServerOptions _options;
    private Process? _process;
    private readonly Queue<string> _recentLog = new();

    public LlamaServerProcess(LlamaServerOptions options) => _options = options;

    public Uri BaseUri { get; private set; } = null!;
    public string ApiKey { get; private set; } = "";
    public bool HasExited => _process?.HasExited ?? true;

    /// <summary>The command line (without the executable), as separate arguments.</summary>
    public static IReadOnlyList<string> BuildArguments(LlamaServerOptions options, int port, string apiKey)
    {
        var args = new List<string>
        {
            "-m", options.ModelPath,
            "--host", "127.0.0.1",
            "--port", port.ToString(),
            "--api-key", apiKey,
            "-t", options.Threads.ToString(),
            "-c", options.EffectiveContextSize.ToString(),
        };
        if (options.Role == ServerRole.Embedding)
            args.AddRange(["--embedding", "--pooling", "last"]);
        if (options.ExtraArgs is { Count: > 0 }) args.AddRange(options.ExtraArgs);   // per-model flags from the manifest
        return args;
    }

    public static string NewApiKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    public static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    /// <summary>Starts the server and returns once <c>/health</c> reports ready.</summary>
    public async Task StartAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (_process is not null) throw new InvalidOperationException("Server already started.");
        var port = FindFreePort();
        ApiKey = NewApiKey();
        BaseUri = new Uri($"http://127.0.0.1:{port}/");

        var info = new ProcessStartInfo(_options.ServerExePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in BuildArguments(_options, port, ApiKey)) info.ArgumentList.Add(arg);

        try
        {
            _process = Process.Start(info) ?? throw new ModelUnavailableException("Could not start llama-server.");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new ModelUnavailableException($"Could not start {_options.ServerExePath}: {e.Message}", e);
        }
        ChildProcessJob.Add(_process);   // Windows: the OS kills the server if this app dies
        _process.OutputDataReceived += (_, e) => Remember(e.Data);
        _process.ErrorDataReceived += (_, e) => Remember(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        try
        {
            using var http = CreateHttpClient();
            await WaitUntilHealthyAsync(http, () => _process.HasExited, timeout ?? TimeSpan.FromMinutes(2), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await DisposeAsync().ConfigureAwait(false);   // the caller cancelled: stop the server and pass that on
            throw;
        }
        catch
        {
            var log = string.Join(Environment.NewLine, _recentLog);
            await DisposeAsync().ConfigureAwait(false);
            throw new ModelUnavailableException($"llama-server did not become ready.{Environment.NewLine}{log}");
        }
    }

    /// <summary>Polls <c>/health</c>; the server answers 503 while the model loads.</summary>
    internal static async Task WaitUntilHealthyAsync(
        HttpClient http, Func<bool> hasExited, TimeSpan timeout, CancellationToken ct, TimeSpan? pollInterval = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (hasExited()) throw new InvalidOperationException("llama-server exited during startup.");
            try
            {
                using var response = await http.GetAsync("health", ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { /* not listening yet */ }

            if (DateTime.UtcNow > deadline) throw new TimeoutException("llama-server did not become healthy in time.");
            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>An <see cref="HttpClient"/> pointed at this server with the API key attached.</summary>
    public HttpClient CreateHttpClient()
    {
        var http = new HttpClient { BaseAddress = BaseUri, Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        return http;
    }

    private void Remember(string? line)
    {
        if (line is null) return;
        lock (_recentLog)
        {
            _recentLog.Enqueue(line);
            while (_recentLog.Count > 20) _recentLog.Dequeue();
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* already gone */ }
        _process?.Dispose();
        _process = null;
        return ValueTask.CompletedTask;
    }
}
