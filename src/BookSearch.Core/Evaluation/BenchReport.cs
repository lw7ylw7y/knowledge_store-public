using System.Globalization;
using System.Text;

namespace BookSearch.Core.Evaluation;

/// <param name="IndexChunksPerSecond">Chunks per second over extract, chunk, embed and write.</param>
public sealed record BenchResult(
    DateTime When, string Machine, string Embedder, string? Llm, int Books, long Chunks, double IndexChunksPerSecond,
    IReadOnlyList<double> SearchMs, IReadOnlyList<double> ColdSummarySeconds, IReadOnlyList<double> WarmFirstTokenSeconds,
    IReadOnlyList<double> WarmTotalSeconds)
{
    /// <summary>An average book is about 100k words, or roughly 260 chunks.</summary>
    public const int ChunksPerAverageBook = 260;

    /// <summary>The indexing speed is <see cref="double.NaN"/> when the benchmark searched an existing library instead of indexing one.</summary>
    public double MinutesPerAverageBook => IndexChunksPerSecond <= 0 ? double.PositiveInfinity : ChunksPerAverageBook / IndexChunksPerSecond / 60;

    public string ToMarkdown()
    {
        var inv = CultureInfo.InvariantCulture;
        string Check(bool ok) => ok ? "pass" : "FAIL";
        var p95 = EvalMetrics.Percentile(SearchMs, 95);
        var sb = new StringBuilder();
        sb.AppendLine($"# Benchmark, {When:yyyy-MM-dd HH:mm}").AppendLine();
        sb.AppendLine($"- Machine: {Machine}");
        sb.AppendLine($"- Embedder: {Embedder}; language model: {Llm ?? "not measured"}");
        sb.AppendLine($"- Library: {Books} books, {Chunks} chunks").AppendLine();
        sb.AppendLine("Targets are for the minimum-spec machine (8 GB RAM, no GPU).").AppendLine();
        sb.AppendLine("| Measure | Result | Target | |").AppendLine("| --- | --- | --- | --- |");
        if (double.IsNaN(IndexChunksPerSecond))
            sb.AppendLine("| Indexing speed | not measured (an existing library was used) | 3 min or less per book (about 1.5 chunks/s) | |");
        else
            sb.AppendLine($"| Indexing speed | {IndexChunksPerSecond.ToString("F2", inv)} chunks/s, about {MinutesPerAverageBook.ToString("F1", inv)} min per 100k-word book | 3 min or less per book (about 1.5 chunks/s) | {Check(MinutesPerAverageBook <= 3)} |");
        sb.AppendLine($"| Search p95 | {p95.ToString("F0", inv)} ms (p50 {EvalMetrics.Percentile(SearchMs, 50).ToString("F0", inv)} ms, {SearchMs.Count} searches) | 10000 ms or less | {Check(p95 <= 10_000)} |");
        if (WarmFirstTokenSeconds.Count > 0)
        {
            var first = EvalMetrics.Percentile(WarmFirstTokenSeconds, 95);
            var total = EvalMetrics.Percentile(WarmTotalSeconds, 95);
            sb.AppendLine($"| Summarize first token (model loaded) | {first.ToString("F1", inv)} s | 15 s or less | {Check(first <= 15)} |");
            sb.AppendLine($"| Summarize total (model loaded) | {total.ToString("F1", inv)} s | 30 s or less | {Check(total <= 30)} |");
            if (ColdSummarySeconds.Count > 0)
                sb.AppendLine($"| Summarize first run, including model load | {ColdSummarySeconds[0].ToString("F1", inv)} s | (no target; the app keeps the model loaded) | |");
        }
        return sb.ToString();
    }
}
