namespace BookSearch.Core.Evaluation;

/// <param name="CorrectRank">1-based rank of the first correct hit among the results shown, or null if none was.</param>
/// <param name="BestSimilarity">Best vector similarity, or null when no vector search ran.</param>
/// <param name="ExactKeywordMatch">A top keyword hit contained every query word (which overrides a low similarity).</param>
public sealed record QuestionResult(
    EvalQuestion Question, int? CorrectRank, double? BestSimilarity, bool ExactKeywordMatch, bool NotCovered,
    double SearchMs, string? TopHit);

public sealed record SweepPoint(double Threshold, double OutOfLibraryRefused, double AnswerableRefused)
{
    /// <summary>Balanced accuracy: how well the threshold refuses what isn't covered and answers what is.</summary>
    public double Balanced => (OutOfLibraryRefused + (1 - AnswerableRefused)) / 2;
}

public static class EvalMetrics
{
    public static IEnumerable<QuestionResult> Answerable(IEnumerable<QuestionResult> results) =>
        results.Where(r => !r.Question.IsOutOfLibrary);

    public static IEnumerable<QuestionResult> OutOfLibrary(IEnumerable<QuestionResult> results) =>
        results.Where(r => r.Question.IsOutOfLibrary);

    /// <summary>Share of answerable questions whose correct passage is among the top <paramref name="k"/> hits.</summary>
    public static double RecallAtK(IEnumerable<QuestionResult> results, int k)
    {
        var answerable = Answerable(results).ToList();
        return answerable.Count == 0 ? 0 : answerable.Count(r => r.CorrectRank is { } rank && rank <= k) / (double)answerable.Count;
    }

    /// <summary>Mean reciprocal rank of the first correct hit (0 when there is none).</summary>
    public static double Mrr(IEnumerable<QuestionResult> results)
    {
        var answerable = Answerable(results).ToList();
        return answerable.Count == 0 ? 0 : answerable.Average(r => r.CorrectRank is { } rank ? 1.0 / rank : 0);
    }

    /// <summary>Share of out-of-library questions that got the not-covered response (NFR-8 asks for 80% or more).</summary>
    public static double NotCoveredAccuracy(IEnumerable<QuestionResult> results)
    {
        var outOfLibrary = OutOfLibrary(results).ToList();
        return outOfLibrary.Count == 0 ? 0 : outOfLibrary.Count(r => r.NotCovered) / (double)outOfLibrary.Count;
    }

    /// <summary>Share of answerable questions wrongly told "not covered".</summary>
    public static double FalseRefusalRate(IEnumerable<QuestionResult> results)
    {
        var answerable = Answerable(results).ToList();
        return answerable.Count == 0 ? 0 : answerable.Count(r => r.NotCovered) / (double)answerable.Count;
    }

    /// <summary>What the search would have said at another coverage threshold (same rule the search service uses).</summary>
    public static bool RefusedAt(QuestionResult result, double threshold) =>
        (result.BestSimilarity is null || result.BestSimilarity < threshold) && !result.ExactKeywordMatch;

    public static IReadOnlyList<SweepPoint> Sweep(IReadOnlyList<QuestionResult> results, double from = 0.10, double to = 0.80, double step = 0.025)
    {
        var answerable = Answerable(results).ToList();
        var outOfLibrary = OutOfLibrary(results).ToList();
        var points = new List<SweepPoint>();
        for (var t = from; t <= to + 1e-9; t += step)
        {
            var threshold = Math.Round(t, 3);
            points.Add(new SweepPoint(threshold,
                outOfLibrary.Count == 0 ? 0 : outOfLibrary.Count(r => RefusedAt(r, threshold)) / (double)outOfLibrary.Count,
                answerable.Count == 0 ? 0 : answerable.Count(r => RefusedAt(r, threshold)) / (double)answerable.Count));
        }
        return points;
    }

    /// <summary>The threshold with the best balanced accuracy (the lowest one when several tie).</summary>
    public static SweepPoint? SuggestThreshold(IReadOnlyList<SweepPoint> sweep) =>
        sweep.OrderByDescending(p => p.Balanced).ThenBy(p => p.Threshold).FirstOrDefault();

    /// <summary>Nearest-rank percentile (p in 0..100): the smallest value that at least p% of values do not exceed.</summary>
    public static double Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0) return 0;
        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Count);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }
}
