using BookSearch.Core.Chunking;
using BookSearch.Core.Ingest;
using BookSearch.Core.Search;

namespace BookSearch.Models;

/// <summary>
/// The embedding model as the rest of the app sees it, backed by <see cref="ServerSupervisor"/>: every call takes a
/// lease (so the server is started, restarted or kept alive as needed) and releases it when the call ends.
/// </summary>
public sealed class SupervisedEmbedder(ServerSupervisor supervisor) : IEmbedder, IQueryEmbedder, ITokenizer, IAsyncDisposable
{
    private readonly object _gate = new();
    private Uri? _baseUri;
    private HttpClient? _http;
    private EmbeddingClient? _client;

    public string ModelId => "qwen3-embedding-0.6b";

    public async Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        using var lease = await supervisor.AcquireAsync(ServerRole.Embedding, ct).ConfigureAwait(false);
        return await ClientFor(lease).EmbedDocumentsAsync(texts, ct).ConfigureAwait(false);
    }

    public async Task<float[]> EmbedQueryAsync(string question, CancellationToken ct = default)
    {
        using var lease = await supervisor.AcquireAsync(ServerRole.Embedding, ct).ConfigureAwait(false);
        return await ClientFor(lease).EmbedQueryAsync(question, ct).ConfigureAwait(false);
    }

    public IReadOnlyList<int> Count(IReadOnlyList<string> texts)
    {
        using var lease = supervisor.AcquireAsync(ServerRole.Embedding).GetAwaiter().GetResult();
        return ClientFor(lease).Count(texts);
    }

    // One client per server process: a restarted server has a new port and key, so the client is rebuilt.
    private EmbeddingClient ClientFor(ServerLease lease)
    {
        lock (_gate)
        {
            if (_client is null || _baseUri != lease.BaseUri)
            {
                _http?.Dispose();
                _http = lease.CreateHttpClient();
                _client = new EmbeddingClient(_http) { ModelId = ModelId };
                _baseUri = lease.BaseUri;
            }
            return _client;
        }
    }

    public ValueTask DisposeAsync()
    {
        _http?.Dispose();
        return ValueTask.CompletedTask;
    }
}
