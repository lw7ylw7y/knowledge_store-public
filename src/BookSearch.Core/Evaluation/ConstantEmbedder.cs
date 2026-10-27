using BookSearch.Core.Data;
using BookSearch.Core.Ingest;

namespace BookSearch.Core.Evaluation;

/// <summary>
/// Gives every passage the same vector, so a library can be built without an embedding model when only keyword search
/// is being evaluated. Vector results from such a library are meaningless.
/// </summary>
public sealed class ConstantEmbedder : IEmbedder
{
    public string ModelId => "none (keyword only)";

    public Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        IReadOnlyList<float[]> vectors = texts.Select(_ =>
        {
            var v = new float[Database.EmbeddingDimension];
            v[0] = 1;
            return v;
        }).ToList();
        return Task.FromResult(vectors);
    }
}
