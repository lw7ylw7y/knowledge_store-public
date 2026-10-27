namespace BookSearch.Core.Search;

/// <summary>Embeds a question into the same vector space as the stored passages.</summary>
public interface IQueryEmbedder
{
    /// <summary>A unit-length vector for the question (the retrieval instruction prefix is the implementation's job).</summary>
    Task<float[]> EmbedQueryAsync(string question, CancellationToken ct = default);
}
