namespace BookSearch.Core.Ingest;

/// <summary>Turns passages into unit-length vectors (the embedding server in production, a fake in tests).</summary>
public interface IEmbedder
{
    /// <summary>Stored in <c>book.embedding_model_id</c>, so a model change can force a re-embed.</summary>
    string ModelId { get; }

    /// <summary>Embeds passages (no query prefix). One vector per input, in order.</summary>
    Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}
