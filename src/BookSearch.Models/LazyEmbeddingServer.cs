using BookSearch.Core.Chunking;
using BookSearch.Core.Ingest;
using BookSearch.Core.Search;

namespace BookSearch.Models;

/// <summary>
/// Starts the embedding server the first time something needs it, so commands that end up not embedding
/// (duplicates, rejected files, nothing to resume) never pay the model load time.
/// </summary>
public sealed class LazyEmbeddingServer(M0Config config) : IEmbedder, IQueryEmbedder, ITokenizer, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LlamaServerProcess? _server;
    private HttpClient? _http;
    private EmbeddingClient? _client;

    public string ModelId => "qwen3-embedding-0.6b";
    public bool Started => _client is not null;

    public async Task<EmbeddingClient> GetClientAsync(CancellationToken ct = default)
    {
        if (_client is not null) return _client;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is not null) return _client;
            Console.Error.WriteLine("Starting embedding server...");
            var server = new LlamaServerProcess(new LlamaServerOptions(
                config.ServerExe, config.EmbeddingModel, ServerRole.Embedding, config.Threads));
            await server.StartAsync(ct: ct).ConfigureAwait(false);
            _server = server;
            _http = server.CreateHttpClient();
            _client = new EmbeddingClient(_http) { ModelId = ModelId };
            return _client;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
        await (await GetClientAsync(ct).ConfigureAwait(false)).EmbedDocumentsAsync(texts, ct).ConfigureAwait(false);

    public async Task<float[]> EmbedQueryAsync(string question, CancellationToken ct = default) =>
        await (await GetClientAsync(ct).ConfigureAwait(false)).EmbedQueryAsync(question, ct).ConfigureAwait(false);

    public IReadOnlyList<int> Count(IReadOnlyList<string> texts) =>
        GetClientAsync().GetAwaiter().GetResult().Count(texts);

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_server is not null) await _server.DisposeAsync().ConfigureAwait(false);
    }
}
