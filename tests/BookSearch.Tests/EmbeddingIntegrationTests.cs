using BookSearch.Models;

namespace BookSearch.Tests;

/// <summary>
/// Needs the real llama-server and Qwen3-Embedding-0.6B. Set BOOKSEARCH_M0_CONFIG to the path of a config.json
/// (see M0Config.SampleJson); without it these tests skip.
/// </summary>
public class EmbeddingIntegrationTests : IAsyncLifetime
{
    private LlamaServerProcess? _server;
    private EmbeddingClient? _client;

    public async Task InitializeAsync()
    {
        var path = Environment.GetEnvironmentVariable("BOOKSEARCH_M0_CONFIG");
        if (string.IsNullOrEmpty(path)) return;
        var config = M0Config.Load(path);
        _server = new LlamaServerProcess(new LlamaServerOptions(
            config.ServerExe, config.EmbeddingModel, ServerRole.Embedding, config.Threads));
        await _server.StartAsync();
        _client = new EmbeddingClient(_server.CreateHttpClient());
    }

    public async Task DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync();
    }

    private static double Cosine(float[] a, float[] b) => a.Zip(b, (x, y) => (double)x * y).Sum(); // vectors are unit length

    [SkippableFact]
    public async Task Embed_SimilarSentences_CloserThanUnrelated()
    {
        Skip.If(_client is null, "BOOKSEARCH_M0_CONFIG not set");

        var v = await _client!.EmbedDocumentsAsync(["I lost my keys", "I can't find my keys", "The sun is hot"]);

        Assert.True(Cosine(v[0], v[1]) > Cosine(v[0], v[2]));
    }

    [SkippableFact]
    public async Task Embed_CrossLingual_Matches()
    {
        Skip.If(_client is null, "BOOKSEARCH_M0_CONFIG not set");

        var query = await _client!.EmbedQueryAsync("grief after losing someone");
        var passages = await _client.EmbedDocumentsAsync([
            "El duelo es el dolor profundo que sentimos cuando muere una persona querida.",   // mourning
            "La receta lleva harina, huevos y una pizca de sal; se hornea veinte minutos.",   // unrelated
        ]);

        Assert.True(Cosine(query, passages[0]) > Cosine(query, passages[1]));
    }

    [SkippableFact]
    public async Task Tokenize_MatchesChunkerExpectations()
    {
        Skip.If(_client is null, "BOOKSEARCH_M0_CONFIG not set");

        var counts = await _client!.CountAsync(["hello world", ""]);

        Assert.InRange(counts[0], 2, 4);
        Assert.Equal(0, counts[1]);
    }
}
