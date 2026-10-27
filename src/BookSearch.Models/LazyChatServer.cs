using System.Runtime.CompilerServices;
using BookSearch.Core.Summarize;

namespace BookSearch.Models;

/// <summary>
/// Starts the chat model's llama-server the first time Summarize needs it, so searching never waits for (or loads)
/// the language model.
/// </summary>
public sealed class LazyChatServer(M0Config config) : IChatModel, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LlamaServerProcess? _server;
    private HttpClient? _http;
    private ChatClient? _client;

    public async Task<ChatClient> GetClientAsync(CancellationToken ct = default)
    {
        if (_client is not null) return _client;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is not null) return _client;
            if (string.IsNullOrEmpty(config.LlmModel))
                throw new FileNotFoundException("config.json has no \"llmModel\". Add the path to a Qwen3.5 GGUF file to use Summarize.");
            if (!File.Exists(config.LlmModel))
                throw new FileNotFoundException($"llmModel points to a missing file: {config.LlmModel}", config.LlmModel);

            Console.Error.WriteLine("Loading the language model...");
            var server = new LlamaServerProcess(new LlamaServerOptions(
                config.ServerExe, config.LlmModel, ServerRole.Chat, config.Threads));
            await server.StartAsync(ct: ct).ConfigureAwait(false);
            _server = server;
            _http = server.CreateHttpClient();
            _client = new ChatClient(_http);
            return _client;
        }
        finally { _gate.Release(); }
    }

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct).ConfigureAwait(false);
        await foreach (var token in client.StreamAsync(messages, options, ct).ConfigureAwait(false))
            yield return token;
    }

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_server is not null) await _server.DisposeAsync().ConfigureAwait(false);
    }
}
