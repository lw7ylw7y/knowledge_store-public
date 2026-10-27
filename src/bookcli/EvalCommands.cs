using System.Runtime.InteropServices;
using BookSearch.Core.Chunking;
using BookSearch.Core.Data;
using BookSearch.Core.Evaluation;
using BookSearch.Core.Ingest;
using BookSearch.Core.Search;
using BookSearch.Core.Summarize;
using BookSearch.Models;
using BookSearch.Parsing;
using Microsoft.Data.Sqlite;

/// <summary>M0 exit tooling: retrieval evaluation, benchmark, grounding sheet and the image-only calibration helper.</summary>
internal static class EvalCommands
{
    /// <summary>The evaluation keeps measuring with five results and two reserved slots (the app shows 3; recall@3 is read from the ranks).</summary>
    private static readonly SearchOptions EvalOptions = new() { TopN = 5 };

    private static readonly string[] BookExtensions = [".epub", ".pdf", ".txt", ".md"];

    // ---- shared plumbing --------------------------------------------------------------------------------

    private sealed class Options
    {
        private readonly Dictionary<string, string?> _values = new();
        public List<string> Positional { get; } = [];

        public Options(string[] args, params string[] flags)
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--")) { Positional.Add(args[i]); continue; }
                if (flags.Contains(args[i])) _values[args[i]] = "true";
                else if (i + 1 < args.Length) _values[args[i]] = args[++i];
                else throw new ArgumentException($"{args[i]} needs a value.");
            }
        }

        public string? Get(string name) => _values.GetValueOrDefault(name);
        public bool Has(string name) => _values.ContainsKey(name);
        public int Int(string name, int fallback) => int.TryParse(Get(name), out var n) && n > 0 ? n : fallback;
        public double Double(string name, double fallback) =>
            double.TryParse(Get(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : fallback;
    }

    private static string DataFolder(Options o) =>
        o.Get("--data") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BookSearch");

    private sealed class ScratchLibrary(string dir, SqliteConnection conn) : IDisposable
    {
        public SqliteConnection Conn { get; } = conn;
        public void Dispose()
        {
            Conn.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static ScratchLibrary NewScratch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "booksearch-eval-" + Guid.NewGuid().ToString("N"));
        var conn = Database.Open(Path.Combine(dir, "library.db"));
        Migrator.Migrate(conn);
        return new ScratchLibrary(dir, conn);
    }

    private static (M0Config? Config, string? Error) LoadConfig(Options o)
    {
        try
        {
            return (M0Config.Load(o.Get("--config") ?? Path.Combine(DataFolder(o), "config.json")), null);
        }
        catch (FileNotFoundException e)
        {
            return (null, e.Message);
        }
    }

    /// <summary>Adds and indexes every supported file; returns the books' ids by file name, and the totals.</summary>
    private static async Task<(Dictionary<string, long> Ids, int Books, long Chunks, double Seconds)> IngestAll(
        IngestService service, IEnumerable<string> files, CancellationToken ct)
    {
        var ids = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long chunks = 0;
        double seconds = 0;
        var list = files.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var name = Path.GetFileName(list[i]);
            var added = await service.AddAsync(list[i], ct);
            var result = await service.IngestAsync(added.BookId, null, ct);
            ids[name] = added.BookId;
            if (result.Status == BookStatus.Ready)
            {
                var t = result.Timings;
                var sec = (t.Extract + t.Chunk + t.Embed + t.Write).TotalSeconds;
                chunks += t.Chunks;
                seconds += sec;
                Console.WriteLine($"  [{i + 1}/{list.Count}] {name}: {t.Chunks} chunks in {sec:F1} s");
            }
            else
                Console.WriteLine($"  [{i + 1}/{list.Count}] {name}: {result.Status}{(result.Reason is null ? "" : " (" + result.Reason + ")")}");
        }
        return (ids, list.Count, chunks, seconds);
    }

    private static IEnumerable<string> BookFiles(string folder) =>
        Directory.GetFiles(folder).Where(f => BookExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f);

    private static CancellationTokenSource CancelOnCtrlC()
    {
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        return cts;
    }

    private static string Stamp() => DateTime.Now.ToString("yyyy-MM-dd-HHmm");

    // ---- eval -------------------------------------------------------------------------------------------

    public static async Task<int> Eval(string[] args)
    {
        var o = new Options(args, "--keyword-only", "--use-library", "--classic-search");
        var fixtures = o.Get("--fixtures") ?? Path.Combine("tests", "fixtures");
        var questionsPath = o.Get("--questions") ?? Path.Combine(fixtures, "questions.json");
        var outDir = o.Get("--out") ?? "eval-reports";
        var keywordOnly = o.Has("--keyword-only");
        var threshold = o.Double("--threshold", new SearchOptions().CoverageThreshold);
        if (!File.Exists(questionsPath))
        {
            Console.Error.WriteLine($"Questions file not found: {questionsPath}");
            return 1;
        }
        var questions = EvalQuestions.Load(questionsPath);

        LazyEmbeddingServer? embedding = null;
        if (!keywordOnly)
        {
            var (config, error) = LoadConfig(o);
            if (config is null)
            {
                Console.Error.WriteLine(error + "\n(or use --keyword-only to evaluate keyword search without a model)");
                return 1;
            }
            embedding = new LazyEmbeddingServer(config);
        }
        await using var embeddingScope = embedding;
        using var cts = CancelOnCtrlC();

        ScratchLibrary? scratch = null;
        SqliteConnection conn;
        try
        {
            Dictionary<string, long> ids;
            if (o.Has("--use-library"))
            {
                conn = Database.Open(Path.Combine(DataFolder(o), "library.db"));
                Migrator.Migrate(conn);
                ids = new BookRepository(conn).List().ToDictionary(b => Path.GetFileName(b.FilePath), b => b.Id, StringComparer.OrdinalIgnoreCase);
                Console.WriteLine($"Using the existing library ({ids.Count} books).");
            }
            else
            {
                scratch = NewScratch();
                conn = scratch.Conn;
                IEmbedder embedder = keywordOnly ? new ConstantEmbedder() : embedding!;
                ITokenizer tokenizer = keywordOnly ? new ApproximateTokenizer() : embedding!;
                var files = BookFiles(fixtures).ToList();
                Console.WriteLine($"Indexing {files.Count} books from {fixtures} into a scratch library{(keywordOnly ? " (no vectors)" : "")}...");
                (ids, _, _, _) = await IngestAll(new IngestService(conn, embedder, tokenizer), files, cts.Token);
            }

            var chunkCount = new ChunkRepository(conn).CountChunks();
            var bookCount = new BookRepository(conn).List().Count(b => b.Status == BookStatus.Ready);
            var search = new SearchService(conn, keywordOnly ? null : embedding, (o.Has("--classic-search") ? SearchOptions.Classic : EvalOptions) with { CoverageThreshold = threshold, TopN = 5 });
            Console.WriteLine($"Running {questions.Count} questions...");
            var results = await EvalRunner.RunAsync(search, questions, f => ids.TryGetValue(f, out var id) ? id : null,
                (done, total) => Console.Write($"\r  {done}/{total}"), cts.Token);
            Console.WriteLine();

            var report = new EvalReport(DateTime.Now, keywordOnly ? "none (keyword search only)" : embedding!.ModelId, bookCount, chunkCount, threshold, results);
            Directory.CreateDirectory(outDir);
            var path = Path.Combine(outDir, $"eval-{Stamp()}.md");
            File.WriteAllText(path, report.ToMarkdown());

            var recall = EvalMetrics.RecallAtK(results, 5);
            var notCovered = EvalMetrics.NotCoveredAccuracy(results);
            Console.WriteLine();
            Console.WriteLine($"recall@5               {recall:P0}   (target {EvalReport.RecallTarget:P0})");
            Console.WriteLine($"recall@3               {EvalMetrics.RecallAtK(results, 3):P0}   (what the app shows now)");
            Console.WriteLine($"MRR                    {EvalMetrics.Mrr(results):F2}");
            Console.WriteLine($"not-covered accuracy   {notCovered:P0}   (target {EvalReport.NotCoveredTarget:P0})");
            Console.WriteLine($"answerable refused     {EvalMetrics.FalseRefusalRate(results):P0}");
            if (!keywordOnly && EvalMetrics.SuggestThreshold(EvalMetrics.Sweep(results)) is { } best)
                Console.WriteLine($"suggested threshold    {best.Threshold:F3}   (current {threshold:F2})");
            Console.WriteLine($"\nReport written to {path}");
            conn.Dispose();
            return recall >= EvalReport.RecallTarget && notCovered >= EvalReport.NotCoveredTarget ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\nStopped.");
            return 130;
        }
        finally
        {
            scratch?.Dispose();
        }
    }

    // ---- bench ------------------------------------------------------------------------------------------

    public static async Task<int> Bench(string[] args)
    {
        var o = new Options(args, "--summarize", "--use-library");
        var fixtures = o.Get("--fixtures") ?? Path.Combine("tests", "fixtures");
        var questionsPath = o.Get("--questions") ?? Path.Combine(fixtures, "questions.json");
        var outDir = o.Get("--out") ?? "eval-reports";
        var runs = o.Int("--runs", 3);
        var bookNames = (o.Get("--books") ?? "alice.epub,pride.epub,quijote.epub").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var (config, error) = LoadConfig(o);
        if (config is null)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        var summarize = o.Has("--summarize");
        if (summarize && string.IsNullOrEmpty(config.LlmModel))
        {
            Console.Error.WriteLine("--summarize needs \"llmModel\" in config.json.");
            return 1;
        }
        var questions = EvalQuestions.Load(questionsPath).Where(q => !q.IsOutOfLibrary).ToList();

        await using var embedding = new LazyEmbeddingServer(config);
        await using var llm = new LazyChatServer(config);
        using var cts = CancelOnCtrlC();
        var useLibrary = o.Has("--use-library");
        using var scratch = useLibrary ? null : NewScratch();
        SqliteConnection? library = null;
        try
        {
            int books;
            long chunks;
            double rate;
            SqliteConnection conn;
            if (useLibrary)
            {
                // Time the searches on a library that is already indexed (the installed app's data, for example): close the app first.
                var dbPath = Path.Combine(DataFolder(o), "library.db");
                if (!File.Exists(dbPath))
                {
                    Console.Error.WriteLine($"No library at {dbPath}.");
                    return 1;
                }
                conn = library = Database.Open(dbPath);
                Migrator.Migrate(conn);
                books = new BookRepository(conn).List().Count(b => b.Status == BookStatus.Ready);
                chunks = new ChunkRepository(conn).CountChunks();
                rate = double.NaN;
                Console.WriteLine($"Using the existing library: {books} indexed books, {chunks} chunks.");
            }
            else
            {
                conn = scratch!.Conn;
                Console.WriteLine("Indexing for the speed measurement...");
                var files = bookNames.Select(n => Path.Combine(fixtures, n)).Where(File.Exists).ToList();
                (_, books, chunks, var seconds) = await IngestAll(new IngestService(conn, embedding, embedding), files, cts.Token);
                rate = seconds > 0 ? chunks / seconds : 0;
                Console.WriteLine($"  {chunks} chunks in {seconds:F1} s = {rate:F2} chunks/s");
            }

            var search = new SearchService(conn, embedding);
            Console.WriteLine($"Timing {questions.Count * runs} searches...");
            var searchMs = new List<double>();
            for (var run = 0; run < runs; run++)
                foreach (var q in questions)
                    searchMs.Add((await search.SearchAsync(q.Question, cts.Token)).Timings.Total);

            List<double> cold = [], warmFirst = [], warmTotal = [];
            if (summarize)
            {
                var service = new SummaryService(llm, embedding, SentenceSplitters.CreateDefault());
                var picks = questions.Where(q => q.Language == "en" && q.Type == "answerable").Take(3).ToList();
                Console.WriteLine("Summarize: first run (loads the model)...");
                for (var i = -1; i < picks.Count; i++)
                {
                    var q = picks[Math.Max(i, 0)];
                    var hits = await search.SearchAsync(q.Question, cts.Token);
                    var run = await service.SummarizeAsync(q.Question, hits, ct: cts.Token);
                    if (run.Refused) continue;
                    if (i < 0) cold.Add(run.Total.TotalSeconds);
                    else
                    {
                        warmFirst.Add(run.TimeToFirstToken.TotalSeconds);
                        warmTotal.Add(run.Total.TotalSeconds);
                        Console.WriteLine($"  warm run {i + 1}: first token {run.TimeToFirstToken.TotalSeconds:F1} s, total {run.Total.TotalSeconds:F1} s");
                    }
                }
            }

            var machine = $"{RuntimeInformation.OSDescription}, {Environment.ProcessorCount} logical cores, " +
                          $"{GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024):F0} GB RAM";
            var result = new BenchResult(DateTime.Now, machine, embedding.ModelId,
                summarize ? Path.GetFileName(config.LlmModel) : null, books, chunks, rate, searchMs, cold, warmFirst, warmTotal);
            Directory.CreateDirectory(outDir);
            var path = Path.Combine(outDir, $"bench-{Stamp()}.md");
            File.WriteAllText(path, result.ToMarkdown());
            Console.WriteLine();
            Console.WriteLine(result.ToMarkdown());
            Console.WriteLine($"Report written to {path}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\nStopped.");
            return 130;
        }
        finally
        {
            library?.Dispose();
        }
    }

    // ---- corpus -----------------------------------------------------------------------------------------

    /// <summary>Downloads a test library of public-domain books (Project Gutenberg, through gutendex.com) for the release measurements.</summary>
    public static async Task<int> Corpus(string[] args)
    {
        var o = new Options(args);
        var folder = o.Get("--out") ?? "bench-library";
        var count = o.Int("--count", 200);
        var language = o.Get("--language") ?? "en";
        using var cts = CancelOnCtrlC();
        using var http = BookSearch.Models.Bench.GutenbergCorpus.CreateHttpClient();
        var corpus = new BookSearch.Models.Bench.GutenbergCorpus(http);
        try
        {
            Console.WriteLine($"Listing the {count} most downloaded public-domain {language} books...");
            var books = await corpus.ListAsync(language, count, cts.Token);
            Console.WriteLine($"Downloading {books.Count} books to {Path.GetFullPath(folder)} (one every 2 s; books already there are kept)...");
            var fetched = await corpus.DownloadAsync(books, folder, (done, total, book, downloaded) =>
                Console.WriteLine($"[{done}/{total}] {book.Title}{(downloaded ? "" : " (already there)")}"), cts.Token);
            Console.WriteLine($"Done: {fetched} downloaded, {books.Count - fetched} already there. A list is in corpus.json.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\nStopped. Run the command again to continue.");
            return 130;
        }
        catch (HttpRequestException e)
        {
            Console.Error.WriteLine($"Download failed: {e.Message}. Run the command again to continue.");
            return 1;
        }
    }

    // ---- grounding --------------------------------------------------------------------------------------

    public static async Task<int> Grounding(string[] args)
    {
        var o = new Options(args);
        if (o.Get("--score") is { } sheet)
        {
            if (!File.Exists(sheet))
            {
                Console.Error.WriteLine($"Sheet not found: {sheet}");
                return 1;
            }
            var score = GroundingSheet.Score(GroundingSheet.Read(sheet));
            Console.WriteLine($"Supported: {score.Supported}, not supported: {score.Unsupported}, not yet checked: {score.Unchecked}");
            Console.WriteLine($"Supported share of checked sentences: {score.Share:P0} (target {GroundingScore.Target:P0})");
            Console.WriteLine(score.Unchecked > 0 ? "The review is not finished: fill in the Supported column for every row."
                : score.MeetsTarget ? "Target met." : "Target missed.");
            return score.MeetsTarget ? 0 : 1;
        }

        var fixtures = o.Get("--fixtures") ?? Path.Combine("tests", "fixtures");
        var questionsPath = o.Get("--questions") ?? Path.Combine(fixtures, "questions.json");
        var count = o.Int("--count", 20);
        var outPath = o.Get("--out") ?? Path.Combine("eval-reports", $"grounding-{Stamp()}.csv");
        var (config, error) = LoadConfig(o);
        if (config is null)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        if (string.IsNullOrEmpty(config.LlmModel))
        {
            Console.Error.WriteLine("config.json has no \"llmModel\".");
            return 1;
        }

        var questions = EvalQuestions.Load(questionsPath).Where(q => !q.IsOutOfLibrary && q.Type != "keyword").Take(count).ToList();
        await using var embedding = new LazyEmbeddingServer(config);
        await using var llm = new LazyChatServer(config);
        using var cts = CancelOnCtrlC();
        using var scratch = NewScratch();
        try
        {
            Console.WriteLine("Indexing the fixture books...");
            await IngestAll(new IngestService(scratch.Conn, embedding, embedding), BookFiles(fixtures), cts.Token);
            var search = new SearchService(scratch.Conn, embedding);
            var splitter = SentenceSplitters.CreateDefault();
            var service = new SummaryService(llm, embedding, splitter);

            var rows = new List<GroundingRow>();
            var refused = 0;
            foreach (var (q, i) in questions.Select((q, i) => (q, i)))
            {
                Console.Write($"\rSummarizing {i + 1}/{questions.Count}...");
                var run = await service.SummarizeAsync(q.Question, await search.SearchAsync(q.Question, cts.Token), ct: cts.Token);
                if (run.NotCovered) { refused++; continue; }
                // One passage per summary now: each sentence is checked against the passage it was written from.
                foreach (var passage in run.Passages.Where(p => !p.NotCovered && p.Text.Length > 0))
                    foreach (var (span, n) in splitter.Split(passage.Text, passage.Hit.Language).Select((s, n) => (s, n + 1)))
                        rows.Add(new GroundingRow(q.Id, q.Question, n, passage.Text.Substring(span.Start, span.Length), passage.Number.ToString(),
                            $"[{passage.Number}] {passage.Label}: {passage.SentText}", ""));
            }
            Console.WriteLine();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            GroundingSheet.Write(outPath, rows);
            Console.WriteLine($"{rows.Count} sentences from {questions.Count - refused} summaries written to {outPath}" +
                              (refused > 0 ? $" ({refused} questions got no summary: not covered)" : ""));
            Console.WriteLine("Open it in a spreadsheet, read each sentence against its cited passage, and enter y or n in the last column.");
            Console.WriteLine($"Then run: bookcli grounding --score \"{outPath}\"");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\nStopped.");
            return 130;
        }
    }

    // ---- text density -----------------------------------------------------------------------------------

    public static int TextDensity(string[] args)
    {
        var files = args.Where(a => !a.StartsWith("--")).ToList();
        if (files.Count == 0) return -1;
        foreach (var file in files)
        {
            try
            {
                var chars = PdfExtractor.CharsPerPage(file);
                double Below(int n) => chars.Length == 0 ? 0 : chars.Count(c => c < n) / (double)chars.Length;
                var rejected = Below(PdfExtractor.MinCharsPerTextPage) >= PdfExtractor.ImageOnlyPageShare;
                Console.WriteLine($"{Path.GetFileName(file)}: {chars.Length} pages; under 50 chars {Below(50):P0}, under 100 {Below(100):P0}, " +
                                  $"under 200 {Below(200):P0}; median {chars.Order().ElementAtOrDefault(chars.Length / 2)} chars/page; " +
                                  $"{(rejected ? "WOULD BE REJECTED as image-only" : "accepted")}");
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException)
            {
                Console.WriteLine($"{Path.GetFileName(file)}: could not be read ({e.Message})");
            }
        }
        Console.WriteLine($"\nThe rule rejects a PDF when at least {PdfExtractor.ImageOnlyPageShare:P0} of its pages have fewer than {PdfExtractor.MinCharsPerTextPage} characters.");
        return 0;
    }
}
