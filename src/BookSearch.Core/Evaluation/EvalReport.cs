using System.Globalization;
using System.Text;

namespace BookSearch.Core.Evaluation;

public sealed record EvalReport(
    DateTime When, string Embedder, int Books, long Chunks, double Threshold, IReadOnlyList<QuestionResult> Results)
{
    public const double RecallTarget = 0.80;
    public const double NotCoveredTarget = 0.80;

    public string ToMarkdown()
    {
        var inv = CultureInfo.InvariantCulture;
        string Pct(double v) => (v * 100).ToString("F0", inv) + "%";
        var answerable = EvalMetrics.Answerable(Results).ToList();
        var outOfLibrary = EvalMetrics.OutOfLibrary(Results).ToList();
        var recall = EvalMetrics.RecallAtK(Results, 5);
        var notCovered = EvalMetrics.NotCoveredAccuracy(Results);
        var sweep = EvalMetrics.Sweep(Results);
        var suggested = EvalMetrics.SuggestThreshold(sweep);
        var vectors = Results.Any(r => r.BestSimilarity is not null);

        var sb = new StringBuilder();
        sb.AppendLine($"# Retrieval evaluation, {When:yyyy-MM-dd HH:mm}").AppendLine();
        sb.AppendLine($"- Embedder: {Embedder}");
        sb.AppendLine($"- Library: {Books} books, {Chunks} chunks");
        sb.AppendLine($"- Coverage threshold used: {Threshold.ToString("F2", inv)}");
        sb.AppendLine($"- Questions: {answerable.Count} answerable, {outOfLibrary.Count} out of library").AppendLine();
        if (!vectors)
            sb.AppendLine("> No vector search ran (keyword only), so the similarity and threshold results below are not meaningful.").AppendLine();

        sb.AppendLine("## Summary").AppendLine();
        sb.AppendLine("| Metric | Result | Target |").AppendLine("| --- | --- | --- |");
        sb.AppendLine($"| recall@5 | {Pct(recall)} ({answerable.Count(r => r.CorrectRank <= 5)}/{answerable.Count}) | {Pct(RecallTarget)} or more |");
        sb.AppendLine($"| recall@3 | {Pct(EvalMetrics.RecallAtK(Results, 3))} ({answerable.Count(r => r.CorrectRank <= 3)}/{answerable.Count}) | what the app shows (ST-24) |");
        sb.AppendLine($"| MRR | {EvalMetrics.Mrr(Results).ToString("F2", inv)} | |");
        sb.AppendLine($"| not-covered accuracy | {Pct(notCovered)} ({outOfLibrary.Count(r => r.NotCovered)}/{outOfLibrary.Count}) | {Pct(NotCoveredTarget)} or more |");
        sb.AppendLine($"| answerable wrongly refused | {Pct(EvalMetrics.FalseRefusalRate(Results))} ({answerable.Count(r => r.NotCovered)}/{answerable.Count}) | as low as possible |");
        sb.AppendLine($"| search time p95 | {EvalMetrics.Percentile(Results.Select(r => r.SearchMs), 95).ToString("F0", inv)} ms | 10000 ms or less |").AppendLine();
        sb.AppendLine(recall >= RecallTarget && notCovered >= NotCoveredTarget
            ? "**Result: both targets met.**"
            : "**Result: a target is missed.**").AppendLine();

        sb.AppendLine("## By question type").AppendLine();
        sb.AppendLine("| Type | Questions | recall@5 | MRR |").AppendLine("| --- | --- | --- | --- |");
        foreach (var group in answerable.GroupBy(r => r.Question.Type).OrderBy(g => g.Key))
            sb.AppendLine($"| {group.Key} | {group.Count()} | {Pct(EvalMetrics.RecallAtK(group, 5))} | {EvalMetrics.Mrr(group).ToString("F2", inv)} |");
        sb.AppendLine().AppendLine("| Language | Questions | recall@5 |").AppendLine("| --- | --- | --- |");
        foreach (var group in answerable.GroupBy(r => r.Question.Language).OrderBy(g => g.Key))
            sb.AppendLine($"| {group.Key} | {group.Count()} | {Pct(EvalMetrics.RecallAtK(group, 5))} |");
        sb.AppendLine();

        if (vectors)
        {
            sb.AppendLine("## Coverage threshold").AppendLine();
            sb.AppendLine("What each threshold would have done with the similarities recorded in this run (an exact keyword match always counts as covered).").AppendLine();
            sb.AppendLine("| Threshold | Out-of-library refused | Answerable wrongly refused | Balanced |").AppendLine("| --- | --- | --- | --- |");
            foreach (var p in sweep.Where((_, i) => i % 2 == 0))
                sb.AppendLine($"| {p.Threshold.ToString("F2", inv)} | {Pct(p.OutOfLibraryRefused)} | {Pct(p.AnswerableRefused)} | {Pct(p.Balanced)} |");
            sb.AppendLine();
            if (vectors && suggested is not null)
                sb.AppendLine($"Suggested threshold: **{suggested.Threshold.ToString("F3", inv)}** (refuses {Pct(suggested.OutOfLibraryRefused)} of out-of-library questions, wrongly refuses {Pct(suggested.AnswerableRefused)} of answerable ones).").AppendLine();
        }

        sb.AppendLine("## Questions").AppendLine();
        sb.AppendLine("| Id | Type | Rank of correct hit | Best similarity | Refused | Top hit |").AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var r in Results)
            sb.AppendLine($"| {r.Question.Id} | {r.Question.Type} | {(r.Question.IsOutOfLibrary ? "n/a" : r.CorrectRank?.ToString() ?? "miss")} | " +
                          $"{(r.BestSimilarity is { } s ? s.ToString("F2", inv) : "n/a")}{(r.ExactKeywordMatch ? " (exact keyword)" : "")} | " +
                          $"{(r.NotCovered ? "yes" : "no")} | {r.TopHit?.Replace("|", "/") ?? "none"} |");
        sb.AppendLine();

        var misses = answerable.Where(r => r.CorrectRank is null or > 5).ToList();
        if (misses.Count > 0)
        {
            sb.AppendLine("## Misses (correct passage not in the top 5)").AppendLine();
            foreach (var r in misses) sb.AppendLine($"- {r.Question.Id}: {r.Question.Question}");
            sb.AppendLine();
        }
        var wrongRefusals = answerable.Where(r => r.NotCovered).ToList();
        var wrongAnswers = outOfLibrary.Where(r => !r.NotCovered).ToList();
        if (wrongRefusals.Count + wrongAnswers.Count > 0)
        {
            sb.AppendLine("## Coverage mistakes").AppendLine();
            foreach (var r in wrongRefusals) sb.AppendLine($"- Wrongly refused: {r.Question.Id}: {r.Question.Question}");
            foreach (var r in wrongAnswers) sb.AppendLine($"- Wrongly answered: {r.Question.Id}: {r.Question.Question}");
        }
        return sb.ToString();
    }
}
