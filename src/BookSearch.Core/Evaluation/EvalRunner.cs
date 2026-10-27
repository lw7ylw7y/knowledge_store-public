using BookSearch.Core.Search;

namespace BookSearch.Core.Evaluation;

public static class EvalRunner
{
    /// <summary>Runs every question through the search and records where the correct passage landed.</summary>
    /// <param name="bookIdForFile">Maps an expected file name to the book id it was ingested as (null if absent).</param>
    public static async Task<IReadOnlyList<QuestionResult>> RunAsync(
        SearchService search, IEnumerable<EvalQuestion> questions, Func<string, long?> bookIdForFile,
        Action<int, int>? progress = null, CancellationToken ct = default)
    {
        var list = questions.ToList();
        var results = new List<QuestionResult>();
        foreach (var (q, i) in list.Select((q, i) => (q, i)))
        {
            ct.ThrowIfCancellationRequested();
            var result = await search.SearchAsync(q.Question, ct).ConfigureAwait(false);

            int? rank = null;
            if (q.ExpectedFile is not null && bookIdForFile(q.ExpectedFile) is { } bookId)
                for (var h = 0; h < result.Hits.Count && rank is null; h++)
                {
                    var hit = result.Hits[h];
                    if (hit.BookId == bookId &&
                        (q.ExpectedContains.Count == 0 || q.ExpectedContains.Any(c => hit.Text.Contains(c, StringComparison.OrdinalIgnoreCase))))
                        rank = h + 1;
                }

            var top = result.Hits.FirstOrDefault();
            results.Add(new QuestionResult(q, rank, result.BestSimilarity, result.ExactKeywordMatch, result.NotCovered,
                result.Timings.Total, top is null ? null : $"{top.BookTitle} / {top.SectionTitle}"));
            progress?.Invoke(i + 1, list.Count);
        }
        return results;
    }
}
