using System.Net.Http.Json;
using BookSearch.Core;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookSearch.Core.Chunking;
using BookSearch.Core.Ingest;
using BookSearch.Core.Search;

namespace BookSearch.Models;

/// <summary>
/// Talks to an embedding <c>llama-server</c>: embeds passages and questions, and (as the chunker's
/// <see cref="ITokenizer"/>) counts tokens with the model's own tokenizer.
/// </summary>
public sealed class EmbeddingClient(HttpClient http, int dimension = EmbeddingClient.ExpectedDimension, int batchSize = 16)
    : ITokenizer, IEmbedder, IQueryEmbedder
{
    public string ModelId { get; init; } = "qwen3-embedding-0.6b";

    public const int ExpectedDimension = 1024;
    public const string QueryInstruction = "Instruct: Given a question, retrieve book passages that answer it\nQuery: ";

    private const int TokenizeConcurrency = 8;

    /// <summary>Embeds a question. Questions, and only questions, get the retrieval instruction prefix.</summary>
    public async Task<float[]> EmbedQueryAsync(string question, CancellationToken ct = default) =>
        (await EmbedAsync([QueryInstruction + question], ct).ConfigureAwait(false))[0];

    /// <summary>Embeds passages with no instruction prefix, in batches. Results are in input order.</summary>
    public async Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var all = new List<float[]>(texts.Count);
        for (var i = 0; i < texts.Count; i += batchSize)
            all.AddRange(await EmbedAsync(texts.Skip(i).Take(batchSize).ToList(), ct).ConfigureAwait(false));
        return all;
    }

    private async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        using var response = await PostAsync(http, "v1/embeddings", new { input = inputs, model = "embedding" }, ct)
            .ConfigureAwait(false);
        await EnsureSuccess(response, "embeddings", ct).ConfigureAwait(false);

        var body = await response.Content.ReadFromJsonAsync<EmbeddingsResponse>(cancellationToken: ct).ConfigureAwait(false)
                   ?? throw new InvalidDataException("Empty embeddings response.");
        if (body.Data.Count != inputs.Count)
            throw new InvalidDataException($"Asked for {inputs.Count} embeddings, got {body.Data.Count}.");

        return body.Data.OrderBy(d => d.Index).Select(d => Normalize(d.Embedding)).ToList();
    }

    private float[] Normalize(float[] vector)
    {
        if (vector.Length != dimension)
            throw new InvalidDataException(
                $"Embedding has {vector.Length} dimensions, expected {dimension}. Is the right embedding model loaded?");
        double sum = 0;
        foreach (var v in vector) sum += (double)v * v;
        var norm = (float)Math.Sqrt(sum);
        if (norm == 0) return vector;
        var result = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++) result[i] = vector[i] / norm;
        return result;
    }

    // ---- tokenizer --------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<int>> CountAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var counts = new int[texts.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, texts.Count),
            new ParallelOptions { MaxDegreeOfParallelism = TokenizeConcurrency, CancellationToken = ct },
            async (i, token) =>
            {
                using var response = await PostAsync(http, "tokenize", new { content = texts[i] }, token)
                    .ConfigureAwait(false);
                await EnsureSuccess(response, "tokenize", token).ConfigureAwait(false);
                var body = await response.Content.ReadFromJsonAsync<TokenizeResponse>(cancellationToken: token)
                    .ConfigureAwait(false) ?? throw new InvalidDataException("Empty tokenize response.");
                counts[i] = body.Tokens.Count;
            }).ConfigureAwait(false);
        return counts;
    }

    /// <summary>Synchronous <see cref="ITokenizer"/> entry point for the chunker (which runs on a worker thread).</summary>
    public IReadOnlyList<int> Count(IReadOnlyList<string> texts) => CountAsync(texts).GetAwaiter().GetResult();

    /// <summary>A buffered JSON body, so requests carry a Content-Length instead of being sent chunked.</summary>
    internal static StringContent JsonBody(object body) =>
        new(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");

    /// <summary>Posts a JSON body; a connection failure becomes a <see cref="ModelUnavailableException"/>.</summary>
    internal static async Task<HttpResponseMessage> PostAsync(HttpClient http, string path, object body, CancellationToken ct)
    {
        try
        {
            return await http.PostAsync(path, JsonBody(body), ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new ModelUnavailableException($"Could not reach the model server: {e.Message}", e);
        }
    }

    internal static async Task EnsureSuccess(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new ModelUnavailableException($"llama-server {what} request failed: {(int)response.StatusCode} {detail}");
    }

    private sealed record EmbeddingsResponse(
        [property: JsonPropertyName("data")] List<EmbeddingItem> Data);

    private sealed record EmbeddingItem(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("embedding")] float[] Embedding);

    private sealed record TokenizeResponse(
        [property: JsonPropertyName("tokens")] List<JsonElement> Tokens);
}
