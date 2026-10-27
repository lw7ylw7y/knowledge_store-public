using BookSearch.Core.Data;
using BookSearch.Core.Evaluation;
using BookSearch.Core.Search;
using BookSearch.Parsing;

namespace BookSearch.Tests;

public class EvalTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static EvalQuestion Q(string id, string type = "answerable", string? file = "a.epub", string language = "en") =>
        new(id, type, language, $"question {id}", type == "out-of-library" ? null : file, []);

    private static QuestionResult R(string id, int? rank, double? sim = 0.6, bool refused = false, string type = "answerable",
        bool exact = false, double ms = 100) =>
        new(Q(id, type), rank, sim, exact, refused, ms, null);

    // ---- metrics on a tiny fixed result set ---------------------------------------------------------------

    [Fact]
    public void Eval_Recall5_ComputedCorrectly()
    {
        // Correct hit at rank 1, 3, none, 6 and 5: three of five are within the top five. Out-of-library results are ignored.
        QuestionResult[] results = [R("a", 1), R("b", 3), R("c", null), R("d", 6), R("e", 5), R("x", null, type: "out-of-library")];

        Assert.Equal(0.6, EvalMetrics.RecallAtK(results, 5), 10);
        Assert.Equal(0.8, EvalMetrics.RecallAtK(results, 8), 10);   // rank 6 now counts too
        Assert.Equal(0.2, EvalMetrics.RecallAtK(results, 1), 10);
    }

    [Fact]
    public void Eval_Mrr_ComputedCorrectly()
    {
        // (1 + 1/2 + 0 + 1/4) / 4
        QuestionResult[] results = [R("a", 1), R("b", 2), R("c", null), R("d", 4), R("x", null, type: "out-of-library")];

        Assert.Equal(0.4375, EvalMetrics.Mrr(results), 10);
        Assert.Equal(0, EvalMetrics.Mrr([]));
    }

    [Fact]
    public void Eval_NotCoveredAccuracyAndFalseRefusals_AreSeparate()
    {
        QuestionResult[] results =
        [
            R("o1", null, refused: true, type: "out-of-library"), R("o2", null, refused: true, type: "out-of-library"),
            R("o3", null, refused: true, type: "out-of-library"), R("o4", null, refused: false, type: "out-of-library"),
            R("a1", 1), R("a2", 2, refused: true), R("a3", 1), R("a4", 1),
        ];

        Assert.Equal(0.75, EvalMetrics.NotCoveredAccuracy(results), 10);
        Assert.Equal(0.25, EvalMetrics.FalseRefusalRate(results), 10);
    }

    [Fact]
    public void Eval_RefusedAt_UsesTheSameRuleAsTheSearchService()
    {
        Assert.True(EvalMetrics.RefusedAt(R("a", 1, sim: 0.30), 0.45));
        Assert.False(EvalMetrics.RefusedAt(R("a", 1, sim: 0.50), 0.45));
        Assert.False(EvalMetrics.RefusedAt(R("a", 1, sim: 0.10, exact: true), 0.45));   // an exact keyword match keeps it covered
        Assert.True(EvalMetrics.RefusedAt(R("a", 1, sim: null), 0.45));                 // no vector search: nothing to compare
    }

    [Fact]
    public void Eval_ThresholdSweep_FindsTheBestSeparatingThreshold()
    {
        // Answerable questions score 0.55-0.80, out-of-library ones 0.10-0.30: any threshold in between is perfect.
        QuestionResult[] results =
        [
            R("a1", 1, sim: 0.80), R("a2", 1, sim: 0.65), R("a3", 2, sim: 0.55),
            R("o1", null, sim: 0.30, type: "out-of-library"), R("o2", null, sim: 0.20, type: "out-of-library"), R("o3", null, sim: 0.10, type: "out-of-library"),
        ];

        var sweep = EvalMetrics.Sweep(results);
        var best = EvalMetrics.SuggestThreshold(sweep)!;

        Assert.Equal(1.0, best.Balanced, 10);
        Assert.InRange(best.Threshold, 0.30, 0.55);
        Assert.Equal(0.325, best.Threshold, 3);                  // the lowest threshold that still separates them
        Assert.Equal(0, sweep.First().OutOfLibraryRefused);      // at 0.10 nothing is refused...
        Assert.Equal(2.0 / 3, sweep.Last().AnswerableRefused, 10); // ...and at 0.80 the two answerable questions below it are
    }

    [Fact]
    public void Bench_P95_ComputedCorrectly()
    {
        Assert.Equal(95, EvalMetrics.Percentile(Enumerable.Range(1, 100).Select(i => (double)i), 95));
        Assert.Equal(50, EvalMetrics.Percentile(Enumerable.Range(1, 100).Select(i => (double)i), 50));
        Assert.Equal(100, EvalMetrics.Percentile(Enumerable.Range(1, 100).Select(i => (double)i), 100));
        Assert.Equal(7, EvalMetrics.Percentile([7.0], 95));
        Assert.Equal(40, EvalMetrics.Percentile([40.0, 10, 30, 20], 95));       // unsorted input, four values
        Assert.Equal(0, EvalMetrics.Percentile([], 95));
    }

    // ---- reports ------------------------------------------------------------------------------------------

    [Fact]
    public void EvalReport_ShowsMetricsTargetsAndMistakes()
    {
        QuestionResult[] results =
        [
            R("a1", 1), R("a2", 3), R("a3", null), R("a4", 2, refused: true),
            R("o1", null, refused: true, type: "out-of-library"), R("o2", null, refused: false, type: "out-of-library"),
        ];

        var markdown = new EvalReport(new DateTime(2026, 10, 5, 14, 30, 0), "test-embedder", 3, 120, 0.45, results).ToMarkdown();

        Assert.Contains("# Retrieval evaluation, 2026-10-05 14:30", markdown);
        Assert.Contains("| recall@5 | 75% (3/4) |", markdown);
        Assert.Contains("| recall@3 | 75% (3/4) |", markdown);                // Eval_ReportsRecallAt3AndAt5: ranks 1, 3 and 2 are within 3
        Assert.Contains("| not-covered accuracy | 50% (1/2) |", markdown);
        Assert.Contains("Result: a target is missed", markdown);
        Assert.Contains("- a3: question a3", markdown);                    // listed under misses
        Assert.Contains("Wrongly refused: a4", markdown);
        Assert.Contains("Wrongly answered: o2", markdown);
        Assert.Contains("Suggested threshold", markdown);
    }

    [Fact]
    public void EvalReport_KeywordOnlyRun_SaysSimilaritiesAreNotMeaningful()
    {
        var markdown = new EvalReport(DateTime.Now, "keyword only", 1, 10, 0.45, [R("a1", 1, sim: null)]).ToMarkdown();

        Assert.Contains("keyword only", markdown);
        Assert.Contains("not meaningful", markdown);
        Assert.DoesNotContain("Suggested threshold", markdown);
    }

    [Fact]
    public void BenchReport_ComparesAgainstTheTargets()
    {
        var fast = new BenchResult(new DateTime(2026, 10, 5), "test PC", "embedder", "llm", 3, 780, IndexChunksPerSecond: 2.6,
            SearchMs: Enumerable.Range(1, 20).Select(i => i * 50.0).ToList(), ColdSummarySeconds: [60.0],
            WarmFirstTokenSeconds: [9.0, 11.0], WarmTotalSeconds: [25.0, 28.0]).ToMarkdown();
        var slow = new BenchResult(new DateTime(2026, 10, 5), "test PC", "embedder", null, 1, 80, IndexChunksPerSecond: 0.5,
            SearchMs: [12_000.0], ColdSummarySeconds: [], WarmFirstTokenSeconds: [], WarmTotalSeconds: []).ToMarkdown();

        Assert.Contains("| Indexing speed | 2.60 chunks/s, about 1.7 min per 100k-word book", fast);
        Assert.DoesNotContain("FAIL", fast);
        Assert.Contains("including model load | 60.0 s", fast);
        Assert.Contains("FAIL", slow);                                      // 8.7 minutes per book, and a 12 s search
        Assert.Contains("not measured", slow);
    }

    // ---- grounding sheet ----------------------------------------------------------------------------------

    [Fact]
    public void GroundingSheet_RoundTripsAwkwardText()
    {
        var path = Path.Combine(Path.GetTempPath(), $"grounding-{Guid.NewGuid():N}.csv");
        try
        {
            GroundingRow[] rows =
            [
                new("q1", "Why, \"exactly\"?", 1, "Alice fell, then \"grew\" tall [1].", "1", "line one\nline two, with comma", ""),
                new("q2", "日本語の質問", 2, "先生と呼んでいた [2]。", "2", "私はその人を常に先生と呼んでいた。", "y"),
            ];

            GroundingSheet.Write(path, rows);
            var read = GroundingSheet.Read(path);

            Assert.Equal(rows, read);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GroundingSheet_Score_CountsCheckedRowsAndTheTarget()
    {
        GroundingRow Row(string supported) => new("q", "q", 1, "s", "1", "p", supported);

        var good = GroundingSheet.Score(Enumerable.Repeat(Row("y"), 18).Concat([Row("n"), Row("N")]));
        var unfinished = GroundingSheet.Score([Row("y"), Row(""), Row("n")]);

        Assert.Equal((18, 2, 0), (good.Supported, good.Unsupported, good.Unchecked));
        Assert.Equal(0.9, good.Share, 10);
        Assert.True(good.MeetsTarget);                          // exactly 90%
        Assert.Equal(1, unfinished.Unchecked);
        Assert.False(unfinished.MeetsTarget);                   // unchecked rows mean the review isn't finished
        Assert.False(GroundingSheet.Score([Row("y"), Row("y"), Row("n")]).MeetsTarget);
    }

    // ---- the question set ---------------------------------------------------------------------------------

    private static readonly IReadOnlyList<EvalQuestion> Questions = EvalQuestions.Load(Fixture("questions.json"));

    [Fact]
    public void Questions_Json_HasTheRequiredMix()
    {
        Assert.True(Questions.Count >= 30);
        Assert.Equal(Questions.Count, Questions.Select(q => q.Id).Distinct().Count());
        foreach (var type in new[] { "answerable", "paraphrase", "cross-language", "keyword", "out-of-library" })
            Assert.Contains(Questions, q => q.Type == type);
        foreach (var language in new[] { "en", "es", "ja", "ar" })
            Assert.Contains(Questions.Where(q => !q.IsOutOfLibrary), q => q.Language == language);
        Assert.True(Questions.Count(q => q.IsOutOfLibrary) >= 5);
        // Cross-language questions really are in a different language from the book they ask about.
        Assert.Contains(Questions, q => q.Type == "cross-language" && q.ExpectedFile == "quijote.epub" && q.Language == "en");
        Assert.Contains(Questions, q => q.Type == "cross-language" && q.ExpectedFile == "pride.epub" && q.Language == "es");
    }

    [Fact]
    public void Questions_Json_AllExpectedBooksExist()
    {
        var files = Questions.Where(q => q.ExpectedFile is not null).Select(q => q.ExpectedFile!).Distinct().ToList();

        Assert.NotEmpty(files);
        Assert.All(files, f => Assert.True(File.Exists(Fixture(f)), $"{f} is not in tests/fixtures"));
    }

    [Fact]
    public void Questions_Json_ExpectedPassagesExistInTheirBooks()
    {
        var textByFile = new Dictionary<string, string>();
        var problems = new List<string>();
        foreach (var q in Questions.Where(q => !q.IsOutOfLibrary))
        {
            if (!textByFile.TryGetValue(q.ExpectedFile!, out var text))
            {
                var book = Extractor.Extract(Fixture(q.ExpectedFile!)).Book!;
                textByFile[q.ExpectedFile!] = text = string.Join("\n", book.Blocks.Select(b => b.Text));
            }
            if (q.ExpectedContains.Count == 0) problems.Add($"{q.Id}: no expectedContains");
            else if (!q.ExpectedContains.Any(c => text.Contains(c, StringComparison.OrdinalIgnoreCase)))
                problems.Add($"{q.Id}: none of [{string.Join(" | ", q.ExpectedContains)}] found in {q.ExpectedFile}");
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Questions_Load_RejectsDuplicateIds()
    {
        var path = Path.Combine(Path.GetTempPath(), $"q-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"questions":[{"id":"a","type":"t","language":"en","question":"x","expectedFile":null,"expectedContains":[]},{"id":"a","type":"t","language":"en","question":"y","expectedFile":null,"expectedContains":[]}]}""");

            Assert.Throws<InvalidDataException>(() => EvalQuestions.Load(path));
        }
        finally { File.Delete(path); }
    }

    // ---- the runner against a small database --------------------------------------------------------------

    [Fact]
    public async Task EvalRunner_RecordsRanksRefusalsAndSimilarities()
    {
        using var db = new TestDb();
        var cooking = db.AddBook("Cooking");
        var farming = db.AddBook("Farming");
        void Add(long book, int i, string text, float[] v) => Database.InTransaction(db.Conn, tx =>
            db.Chunks.WriteBatch([new ChunkWrite(new Chunk { BookId = book, ChunkIndex = i, Text = text, TokenCount = 5 }, v)], tx));
        Add(cooking, 0, "Whisk the eggs and fold in the flour gently.", TestDb.Vec(1, 0));
        Add(cooking, 1, "Bake the loaf until golden brown on top.", TestDb.Vec(0, 1));
        Add(farming, 0, "Plant the wheat seeds in early spring.", TestDb.Vec(0, 0, 1));

        // Questions map to a fixed direction, so similarities are predictable.
        var embedder = new FakeQueryEmbedder(q => q.Contains("batter") ? TestDb.Vec(1, 0.1f) : q.Contains("wheat") ? TestDb.Vec(0, 0, 1) : TestDb.Vec(0, 0, 0, 1));
        var search = new SearchService(db.Conn, embedder);
        EvalQuestion[] questions =
        [
            new("q1", "answerable", "en", "how do I make batter", "cooking.epub", ["flour"]),
            new("q2", "answerable", "en", "when to sow wheat", "farming.epub", ["wheat"]),
            new("q3", "answerable", "en", "how do I make batter", "farming.epub", ["wheat"]),     // wrong book on purpose
            new("o1", "out-of-library", "en", "quantum chromodynamics", null, []),
        ];
        long? IdFor(string file) => file switch { "cooking.epub" => cooking, "farming.epub" => farming, _ => null };

        var progress = new List<int>();
        var results = await EvalRunner.RunAsync(search, questions, IdFor, (done, _) => progress.Add(done));

        Assert.Equal([1, 2, 3, 4], progress);
        Assert.Equal(1, results[0].CorrectRank);
        Assert.Equal(1, results[1].CorrectRank);
        Assert.False(results[0].NotCovered);
        Assert.True(results[0].BestSimilarity > 0.9);
        Assert.NotEqual(1, results[2].CorrectRank);               // the wheat chunk is not what "batter" finds first
        Assert.True(results[3].NotCovered);                       // orthogonal to every chunk
        Assert.Equal(0, results[3].BestSimilarity!.Value, 5);
        Assert.Equal(1.0, EvalMetrics.NotCoveredAccuracy(results));
        Assert.All(results, r => Assert.True(r.SearchMs > 0));
        Assert.Contains("Cooking", results[0].TopHit);
    }

    // ---- image-only calibration ---------------------------------------------------------------------------

    [Fact]
    public void CharsPerPage_SeparatesScannedFromTextPdfs()
    {
        var scanned = PdfExtractor.CharsPerPage(Fixture("scanned.pdf"));
        var text = PdfExtractor.CharsPerPage(Fixture("origin.pdf"));

        Assert.All(scanned, c => Assert.True(c < PdfExtractor.MinCharsPerTextPage));
        Assert.All(text, c => Assert.True(c > PdfExtractor.MinCharsPerTextPage));
        Assert.Equal(8, text.Length);
    }
}
