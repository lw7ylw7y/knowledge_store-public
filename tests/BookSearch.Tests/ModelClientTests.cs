using System.Net;
using System.Text;
using System.Text.Json;
using BookSearch.Core;
using BookSearch.Core.Summarize;
using BookSearch.Models;

namespace BookSearch.Tests;

/// <summary>Answers every request from a delegate and records what was sent.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        lock (Requests) Requests.Add((request, body));
        return respond(request, body);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public class ModelClientTests
{
    private static HttpClient Client(FakeHandler handler, string? key = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9999/") };
        if (key is not null) http.DefaultRequestHeaders.Authorization = new("Bearer", key);
        return http;
    }

    private static float[] Vec(float first, int dim = EmbeddingClient.ExpectedDimension)
    {
        var v = new float[dim];
        v[0] = first;
        v[1] = 1;
        return v;
    }

    /// <summary>An embeddings endpoint that returns <paramref name="make"/>(i) for the i-th input, in order.</summary>
    private static FakeHandler EmbeddingServer(Func<int, float[]>? make = null, bool reverse = false) =>
        new((_, body) =>
        {
            var inputs = JsonDocument.Parse(body).RootElement.GetProperty("input").GetArrayLength();
            var items = Enumerable.Range(0, inputs)
                .Select(i => new { index = i, embedding = (make ?? (n => Vec(n + 1)))(i) });
            if (reverse) items = items.Reverse();
            return FakeHandler.Json(JsonSerializer.Serialize(new { data = items }));
        });

    private static string[] SentInputs(FakeHandler h, int request) =>
        JsonDocument.Parse(h.Requests[request].Body).RootElement.GetProperty("input")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();

    // ---- server process -----------------------------------------------------------------------------------

    [Fact]
    public void Server_Args_BindToLoopbackWithKey()
    {
        var args = LlamaServerProcess.BuildArguments(
            new LlamaServerOptions("llama-server", "/m/model.gguf", ServerRole.Embedding, Threads: 6), 8123, "secretkey");

        Assert.Equal("127.0.0.1", args[args.ToList().IndexOf("--host") + 1]);
        Assert.Equal("secretkey", args[args.ToList().IndexOf("--api-key") + 1]);
        Assert.Equal("8123", args[args.ToList().IndexOf("--port") + 1]);
        Assert.Equal("6", args[args.ToList().IndexOf("-t") + 1]);
        Assert.Equal("/m/model.gguf", args[args.ToList().IndexOf("-m") + 1]);
        Assert.DoesNotContain("0.0.0.0", args);
    }

    [Fact]
    public void Server_Args_DependOnRole()
    {
        var embedding = LlamaServerProcess.BuildArguments(new("x", "m", ServerRole.Embedding, 4), 1, "k");
        var chat = LlamaServerProcess.BuildArguments(new("x", "m", ServerRole.Chat, 4), 1, "k");

        Assert.Contains("--embedding", embedding);
        Assert.Equal("last", embedding[embedding.ToList().IndexOf("--pooling") + 1]);
        Assert.Equal("2048", embedding[embedding.ToList().IndexOf("-c") + 1]);
        Assert.DoesNotContain("--embedding", chat);
        Assert.Equal("4096", chat[chat.ToList().IndexOf("-c") + 1]);
    }

    [Fact]
    public void Server_ApiKey_IsRandomAndLong()
    {
        var keys = Enumerable.Range(0, 20).Select(_ => LlamaServerProcess.NewApiKey()).ToList();

        Assert.Equal(20, keys.Distinct().Count());
        Assert.All(keys, k => Assert.True(k.Length >= 32));
    }

    [Fact]
    public void Server_FreePort_IsUsable()
    {
        var port = LlamaServerProcess.FindFreePort();

        Assert.InRange(port, 1024, 65535);
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();
    }

    [Fact]
    public async Task Server_WaitUntilHealthy_RetriesWhileModelLoads()
    {
        var calls = 0;
        var handler = new FakeHandler((_, _) =>
            ++calls < 3 ? FakeHandler.Json("{}", HttpStatusCode.ServiceUnavailable) : FakeHandler.Json("{\"status\":\"ok\"}"));

        await LlamaServerProcess.WaitUntilHealthyAsync(
            Client(handler), () => false, TimeSpan.FromSeconds(5), default, TimeSpan.FromMilliseconds(1));

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Server_WaitUntilHealthy_FailsFastWhenProcessExits()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("{}", HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<InvalidOperationException>(() => LlamaServerProcess.WaitUntilHealthyAsync(
            Client(handler), () => true, TimeSpan.FromSeconds(5), default, TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public async Task Server_WaitUntilHealthy_TimesOut()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("{}", HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<TimeoutException>(() => LlamaServerProcess.WaitUntilHealthyAsync(
            Client(handler), () => false, TimeSpan.FromMilliseconds(30), default, TimeSpan.FromMilliseconds(5)));
    }

    [Fact]
    public async Task Server_Dispose_KillsChildAndStartFailureReportsLog()
    {
        // A "server" that exits immediately must fail StartAsync instead of hanging until the timeout.
        var exe = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        var server = new LlamaServerProcess(new LlamaServerOptions(exe, "ignored", ServerRole.Embedding, 1));

        var ex = await Assert.ThrowsAsync<ModelUnavailableException>(() => server.StartAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains("did not become ready", ex.Message);
        Assert.True(server.HasExited);
    }

    [SkippableFact]
    public async Task Server_CancelledDuringStartup_ThrowsCancellationAndStopsTheProcess()
    {
        Skip.If(OperatingSystem.IsWindows(), "uses a shell script as the stand-in server");
        // A "server" that runs but never answers /health: cancelling must surface as a cancellation, not "did not become ready".
        // `sh -m <script> <more args>` runs the script and ignores the llama-server flags.
        var script = Path.Combine(Path.GetTempPath(), $"never-ready-{Guid.NewGuid():N}.sh");
        File.WriteAllText(script, "#!/bin/sh\nsleep 30\n");
        try
        {
            var server = new LlamaServerProcess(new LlamaServerOptions("/bin/sh", script, ServerRole.Embedding, 1));
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.StartAsync(TimeSpan.FromSeconds(30), cts.Token));

            Assert.True(server.HasExited);
        }
        finally { File.Delete(script); }
    }

    // ---- embeddings ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Embedding_Query_AddsInstructionPrefix()
    {
        var handler = EmbeddingServer();

        await new EmbeddingClient(Client(handler)).EmbedQueryAsync("why is the sky blue?");

        var sent = Assert.Single(SentInputs(handler, 0));
        Assert.StartsWith("Instruct: Given a question, retrieve book passages that answer it", sent);
        Assert.EndsWith("Query: why is the sky blue?", sent);
    }

    [Fact]
    public async Task Embedding_Document_HasNoPrefix()
    {
        var handler = EmbeddingServer();

        await new EmbeddingClient(Client(handler)).EmbedDocumentsAsync(["A passage of text.", "Another."]);

        Assert.Equal(["A passage of text.", "Another."], SentInputs(handler, 0));
    }

    [Fact]
    public async Task Embedding_WrongDimension_Throws()
    {
        var handler = EmbeddingServer(_ => new float[768]);

        await Assert.ThrowsAsync<InvalidDataException>(() => new EmbeddingClient(Client(handler)).EmbedQueryAsync("q"));
    }

    [Fact]
    public async Task Embedding_Vectors_AreNormalized()
    {
        var handler = EmbeddingServer(_ => Vec(3)); // (3, 1, 0, ...) has length sqrt(10)

        var v = (await new EmbeddingClient(Client(handler)).EmbedDocumentsAsync(["x"]))[0];

        Assert.Equal(EmbeddingClient.ExpectedDimension, v.Length);
        Assert.Equal(1.0, Math.Sqrt(v.Sum(x => (double)x * x)), 5);
        Assert.Equal(3 / Math.Sqrt(10), v[0], 5);
    }

    [Fact]
    public async Task Embedding_Documents_AreBatchedAndKeepInputOrder()
    {
        var handler = EmbeddingServer(i => Vec(i + 1), reverse: true); // server answers out of order
        var client = new EmbeddingClient(Client(handler), batchSize: 16);
        var texts = Enumerable.Range(0, 40).Select(i => $"passage {i}").ToList();

        var vectors = await client.EmbedDocumentsAsync(texts);

        Assert.Equal(3, handler.Requests.Count);                              // 16 + 16 + 8
        Assert.Equal([16, 16, 8], Enumerable.Range(0, 3).Select(r => SentInputs(handler, r).Length));
        Assert.Equal(40, vectors.Count);
        // Within each batch the i-th vector is the one the server made for the i-th input.
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (float)(i + 1)).Select(x => x / MathF.Sqrt(x * x + 1)),
            vectors.Take(16).Select(v => v[0]));
    }

    [Fact]
    public async Task Embedding_SendsBearerKeyAndReportsServerErrors()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("{\"error\":\"nope\"}", HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<ModelUnavailableException>(
            () => new EmbeddingClient(Client(handler, "k123")).EmbedQueryAsync("q"));

        Assert.Contains("401", ex.Message);
        Assert.Equal("Bearer k123", handler.Requests[0].Request.Headers.Authorization!.ToString());
    }

    [Fact]
    public void Tokenizer_Count_UsesServerTokenize()
    {
        var handler = new FakeHandler((_, body) =>
        {
            var text = JsonDocument.Parse(body).RootElement.GetProperty("content").GetString()!;
            var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select((_, i) => i);
            return FakeHandler.Json(JsonSerializer.Serialize(new { tokens }));
        });
        var client = new EmbeddingClient(Client(handler));

        var counts = client.Count(["one two three", "four", "", "a b c d e f"]);

        Assert.Equal([3, 1, 0, 6], counts);
        Assert.All(handler.Requests, r => Assert.EndsWith("tokenize", r.Request.RequestUri!.AbsolutePath));
    }

    // ---- chat ---------------------------------------------------------------------------------------------

    private static string Sse(params string[] tokens) =>
        string.Concat(tokens.Select(t =>
            $"data: {JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = t } } } })}\n\n")) +
        "data: [DONE]\n\n";

    private static readonly ChatMessage[] Hello = [ChatMessage.User("hi")];

    [Fact]
    public async Task Chat_Stream_ParsesTokens()
    {
        var sse = ": keep-alive\n\ndata: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n" +
                  Sse("The", " cat", " sat", ".");
        var handler = new FakeHandler((_, _) => new HttpResponseMessage { Content = new StringContent(sse) });

        var tokens = new List<string>();
        await foreach (var t in new ChatClient(Client(handler)).StreamAsync(Hello)) tokens.Add(t);

        Assert.Equal(["The", " cat", " sat", "."], tokens); // role-only and keep-alive lines are skipped
    }

    [Fact]
    public async Task Chat_Request_UsesLibraryGroundingSettings()
    {
        var handler = new FakeHandler((_, _) => new HttpResponseMessage { Content = new StringContent(Sse("ok")) });

        await foreach (var _ in new ChatClient(Client(handler)).StreamAsync(
                           [ChatMessage.System("rules"), ChatMessage.User("question")])) { }

        Assert.EndsWith("v1/chat/completions", handler.Requests[0].Request.RequestUri!.AbsolutePath);
        var root = JsonDocument.Parse(handler.Requests[0].Body).RootElement;
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal(0.2, root.GetProperty("temperature").GetDouble());
        Assert.Equal(250, root.GetProperty("max_tokens").GetInt32());
        Assert.False(root.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        var messages = root.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["system", "user"], messages.Select(m => m.GetProperty("role").GetString()));
    }

    [Fact]
    public async Task Chat_Stream_CancelStopsReading()
    {
        var stream = new EndlessSseStream();
        var handler = new FakeHandler((_, _) => new HttpResponseMessage { Content = new StreamContent(stream) });
        using var cts = new CancellationTokenSource();

        var received = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in new ChatClient(Client(handler)).StreamAsync(Hello, ct: cts.Token))
                if (++received == 3) cts.Cancel();
        });

        Assert.Equal(3, received);
        Assert.True(stream.Disposed, "the response stream should be released when generation is cancelled");
        Assert.True(stream.TokensProduced < 20, "reading should stop soon after cancel");
    }

    [Fact]
    public async Task Chat_ServerError_Throws()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("{\"error\":\"busy\"}", HttpStatusCode.ServiceUnavailable));

        var ex = await Assert.ThrowsAsync<ModelUnavailableException>(async () =>
        {
            await foreach (var _ in new ChatClient(Client(handler)).StreamAsync(Hello)) { }
        });
        Assert.Contains("503", ex.Message);
    }

    private sealed class EndlessSseStream : Stream
    {
        private byte[] _pending = [];
        private int _offset;
        public int TokensProduced { get; private set; }
        public bool Disposed { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_offset >= _pending.Length)
            {
                _pending = Encoding.UTF8.GetBytes(Sse("tok").Replace("data: [DONE]\n\n", ""));
                _offset = 0;
                TokensProduced++;
            }
            var n = Math.Min(count, _pending.Length - _offset);
            Array.Copy(_pending, _offset, buffer, offset, n);
            _offset += n;
            return n;
        }

        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---- config -------------------------------------------------------------------------------------------

    [Fact]
    public void Config_MissingFile_ExplainsHowToCreateIt()
    {
        var ex = Assert.Throws<FileNotFoundException>(() => M0Config.Load("/nonexistent/config.json"));

        Assert.Contains("llama-server", ex.Message);
        Assert.Contains("embeddingModel", ex.Message);
    }

    [Fact]
    public void Config_Load_ReadsPaths()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, "llama-server");
            var model = Path.Combine(dir, "embed.gguf");
            File.WriteAllText(exe, "");
            File.WriteAllText(model, "");
            var path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { serverExe = exe, embeddingModel = model, threads = 3 }));

            var config = M0Config.Load(path);

            Assert.Equal(exe, config.ServerExe);
            Assert.Equal(3, config.Threads);
            Assert.Null(config.LlmModel);
        }
        finally { Directory.Delete(dir, true); }
    }
}
