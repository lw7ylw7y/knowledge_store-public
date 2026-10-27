using BookSearch.Core.Chunking;
using BookSearch.Core.Data;
using BookSearch.Core.Ingest;
using BookSearch.Core.Chunking;
using BookSearch.Core.Search;
using BookSearch.Core.Summarize;
using BookSearch.Models;
using BookSearch.Parsing;

return await Dispatch(args);

static async Task<int> Dispatch(string[] args) => args switch
{
    ["init", .. var rest] => Init(rest),
    ["extract", .. var rest] => Extract(rest),
    ["chunk", .. var rest] => ChunkCommand(rest),
    ["serve", "embed", .. var rest] => await ServeEmbed(rest),
    ["embed", .. var rest] => await EmbedCommand(rest),
    ["add", .. var rest] => await AddCommand(rest),
    ["resume", .. var rest] => await ResumeCommand(rest),
    ["remove", .. var rest] => RemoveCommand(rest),
    ["list", .. var rest] => ListCommand(rest),
    ["search", .. var rest] => await SearchCommand(rest),
    ["summarize", .. var rest] => await SummarizeCommand(rest),
    ["models", .. var rest] => await ModelCommands.Run(rest),
    ["eval", .. var rest] => await EvalCommands.Eval(rest),
    ["bench", .. var rest] => await EvalCommands.Bench(rest),
    ["corpus", .. var rest] => await EvalCommands.Corpus(rest),
    ["grounding", .. var rest] => await EvalCommands.Grounding(rest),
    ["textdensity", .. var rest] => EvalCommands.TextDensity(rest) < 0 ? Usage() : 0,
    ["stats", .. var rest] => StatsCommand(rest),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: bookcli init [--data <folder>]");
    Console.Error.WriteLine("       bookcli extract <file> [--page <n>]");
    Console.Error.WriteLine("       bookcli chunk <file> [--show <n>] [--stats]");
    Console.Error.WriteLine("       bookcli serve embed [--config <config.json>]");
    Console.Error.WriteLine("       bookcli embed <text> [--query] [--config <config.json>]");
    Console.Error.WriteLine("       bookcli add <files...>     bookcli resume     bookcli remove <id>");
    Console.Error.WriteLine("       bookcli list     bookcli stats     (all accept --data <folder> and --config <config.json>)");
    Console.Error.WriteLine("       bookcli search <question> [--top <n>] [--keyword-only]");
    Console.Error.WriteLine("       bookcli summarize <question> [--passages <n>]   (needs llmModel in config.json)");
    Console.Error.WriteLine("       bookcli models list|download|verify|import   (choose, download and check the model files)");
    Console.Error.WriteLine("       bookcli eval [--fixtures <dir>] [--keyword-only] [--use-library] [--threshold <x>] [--out <dir>]");
    Console.Error.WriteLine("       bookcli bench [--summarize] [--runs <n>] [--books a.epub,b.epub] [--use-library [--data <folder>]]");
    Console.Error.WriteLine("       bookcli corpus [--out <folder>] [--count <n>] [--language <code>]   (public-domain test library from Project Gutenberg)");
    Console.Error.WriteLine("       bookcli grounding [--count <n>] [--out <sheet.csv>]     bookcli grounding --score <sheet.csv>");
    Console.Error.WriteLine("       bookcli textdensity <pdf...>");
    Console.Error.WriteLine("  config defaults to <data folder>/config.json; see the sample printed when it is missing.");
    return 2;
}

static int Init(string[] args)
{
    var data = DefaultDataFolder();
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--data" && i + 1 < args.Length) data = args[++i];
        else return Usage();
    }

    var dbPath = Path.Combine(data, "library.db");
    using var conn = Database.Open(dbPath);
    var result = Migrator.Migrate(conn);

    Console.WriteLine(result.Changed
        ? $"Migrated schema {result.FromVersion} -> {result.ToVersion}."
        : "already up to date");
    Console.WriteLine($"Database:       {dbPath}");
    Console.WriteLine($"Schema version: {result.ToVersion}");
    Console.WriteLine($"sqlite-vec:     {Database.VecVersion(conn)}");
    return 0;
}

static int Extract(string[] args)
{
    string? file = null;
    int? page = null;
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--page" && i + 1 < args.Length && int.TryParse(args[++i], out var n)) page = n;
        else if (file is null && !args[i].StartsWith("--")) file = args[i];
        else return Usage();
    }
    if (file is null) return Usage();
    if (!File.Exists(file))
    {
        Console.Error.WriteLine($"File not found: {file}");
        return 1;
    }

    var result = Extractor.Extract(file);
    if (result.Rejection is { } rejection)
    {
        Console.WriteLine($"rejected ({rejection.Code}): {rejection.Reason}");
        return 1;
    }

    var book = result.Book!;
    if (page is not null)
    {
        if (book.PageCount is null)
        {
            Console.Error.WriteLine("--page only applies to PDF files.");
            return 1;
        }
        var onPage = book.Blocks.Where(b => b.Page == page).ToList();
        if (onPage.Count == 0)
        {
            Console.Error.WriteLine($"No text on page {page} (book has {book.PageCount} pages).");
            return 1;
        }
        Console.WriteLine(string.Join("\n\n", onPage.Select(b => b.Text)));
        return 0;
    }

    Console.WriteLine($"Title:    {book.Metadata.Title}");
    Console.WriteLine($"Author:   {book.Metadata.Author ?? "(unknown)"}");
    Console.WriteLine($"Format:   {book.Format}");
    Console.WriteLine($"Language: {book.Language ?? "und"}");
    if (book.PageCount is { } pages) Console.WriteLine($"Pages:    {pages}");
    Console.WriteLine($"Blocks:   {book.Blocks.Count}");
    Console.WriteLine($"Chapters ({book.Sections.Count}):");
    foreach (var s in book.Sections)
        Console.WriteLine($"  {new string(' ', Math.Max(0, s.Level - 1) * 2)}{s.Order + 1}. {s.Title ?? "(untitled)"}");

    var preview = string.Join(" ", book.Blocks.Select(b => b.Text));
    Console.WriteLine();
    Console.WriteLine(preview.Length <= 300 ? preview : preview[..300] + "…");
    return 0;
}

static int ChunkCommand(string[] args)
{
    string? file = null;
    var show = 0;
    var stats = false;
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--show" && i + 1 < args.Length && int.TryParse(args[++i], out var n)) show = n;
        else if (args[i] == "--stats") stats = true;
        else if (file is null && !args[i].StartsWith("--")) file = args[i];
        else return Usage();
    }
    if (file is null || (show == 0 && !stats)) return Usage();
    if (!File.Exists(file))
    {
        Console.Error.WriteLine($"File not found: {file}");
        return 1;
    }

    var result = Extractor.Extract(file);
    if (result.Rejection is { } rejection)
    {
        Console.WriteLine($"rejected ({rejection.Code}): {rejection.Reason}");
        return 1;
    }
    var book = result.Book!;

    // Token counts are estimated until the embedding server's tokenizer arrives (ST-04).
    var splitter = SentenceSplitters.CreateDefault();
    var chunks = new Chunker(new ApproximateTokenizer(), splitter).Chunk(book);
    Console.WriteLine($"{book.Metadata.Title} - {chunks.Count} chunks " +
                      $"(sentence splitter: {(splitter is IcuSentenceSplitter ? "ICU" : "punctuation fallback")}, " +
                      "approximate token counts)");

    var titles = book.Sections.ToDictionary(s => s.Order, s => s.Title ?? "(untitled)");
    foreach (var c in chunks.Take(show))
    {
        var where = c.PageStart is null ? c.Location ?? "-"
            : c.PageStart == c.PageEnd ? $"p. {c.PageStart}" : $"pp. {c.PageStart}-{c.PageEnd}";
        Console.WriteLine();
        Console.WriteLine($"--- chunk {c.Index} | {c.TokenCount} tokens | {titles[c.SectionOrder]} | {where}");
        Console.WriteLine(c.Text);
    }

    if (stats && chunks.Count > 0)
    {
        var tokens = chunks.Select(c => c.TokenCount).ToList();
        var words = chunks.Sum(c => c.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
        Console.WriteLine();
        Console.WriteLine($"Chunks: {chunks.Count}");
        Console.WriteLine($"Tokens per chunk: min {tokens.Min()}, avg {tokens.Average():F0}, max {tokens.Max()}");
        Console.WriteLine($"Chunks per 100k words: {chunks.Count * 100_000.0 / Math.Max(1, words):F0}");
    }
    return 0;
}

static async Task<LlamaServerProcess?> StartEmbeddingServer(string? configPath)
{
    M0Config config;
    try
    {
        config = M0Config.Load(configPath ?? Path.Combine(DefaultDataFolder(), "config.json"));
    }
    catch (FileNotFoundException e)
    {
        Console.Error.WriteLine(e.Message);
        return null;
    }
    var server = new LlamaServerProcess(new LlamaServerOptions(
        config.ServerExe, config.EmbeddingModel, ServerRole.Embedding, config.Threads));

    // Never leave a model server behind: stop it on Ctrl+C and on any normal exit.
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; server.DisposeAsync().AsTask().GetAwaiter().GetResult(); };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => server.DisposeAsync().AsTask().GetAwaiter().GetResult();

    Console.Error.WriteLine("Starting embedding server...");
    try
    {
        await server.StartAsync();
    }
    catch (Exception e) when (e is InvalidOperationException or TimeoutException)
    {
        Console.Error.WriteLine(e.Message);
        return null;
    }
    return server;
}

static string? ConfigArg(ref string[] args)
{
    var i = Array.IndexOf(args, "--config");
    if (i < 0 || i + 1 >= args.Length) return null;
    var path = args[i + 1];
    args = args.Where((_, n) => n != i && n != i + 1).ToArray();
    return path;
}

static async Task<int> ServeEmbed(string[] args)
{
    var configPath = ConfigArg(ref args);
    if (args.Length > 0) return Usage();

    await using var server = await StartEmbeddingServer(configPath);
    if (server is null) return 1;

    Console.WriteLine($"Embedding server ready at {server.BaseUri} (listening on 127.0.0.1 only)");
    Console.WriteLine($"API key: {server.ApiKey}");
    Console.WriteLine("Press Ctrl+C to stop.");
    while (!server.HasExited) await Task.Delay(500);
    Console.Error.WriteLine("The server stopped.");
    return server.HasExited ? 0 : 1;
}

static async Task<int> EmbedCommand(string[] args)
{
    var configPath = ConfigArg(ref args);
    var asQuery = args.Contains("--query");
    var texts = args.Where(a => a != "--query").ToList();
    if (texts.Count != 1) return Usage();

    var started = System.Diagnostics.Stopwatch.StartNew();
    await using var server = await StartEmbeddingServer(configPath);
    if (server is null) return 1;
    var startup = started.Elapsed;

    using var http = server.CreateHttpClient();
    var client = new EmbeddingClient(http);
    var timer = System.Diagnostics.Stopwatch.StartNew();
    var vector = asQuery
        ? await client.EmbedQueryAsync(texts[0])
        : (await client.EmbedDocumentsAsync([texts[0]]))[0];
    timer.Stop();

    Console.WriteLine($"Dimension: {vector.Length}");
    Console.WriteLine($"Embedding time: {timer.Elapsed.TotalMilliseconds:F0} ms (server startup {startup.TotalSeconds:F1} s)");
    Console.WriteLine($"First values: {string.Join(", ", vector.Take(4).Select(v => v.ToString("F4")))}");
    return 0;
}

// ---- library commands ---------------------------------------------------------------------------------

/// <summary>Pulls "--data" and "--config" out of the arguments.</summary>
static (string Data, string? Config, string[] Remaining) CommonArgs(string[] args)
{
    var data = DefaultDataFolder();
    var config = ConfigArg(ref args);
    var i = Array.IndexOf(args, "--data");
    if (i >= 0 && i + 1 < args.Length)
    {
        data = args[i + 1];
        args = args.Where((_, n) => n != i && n != i + 1).ToArray();
    }
    return (data, config, args);
}

static Microsoft.Data.Sqlite.SqliteConnection OpenLibrary(string data)
{
    var conn = Database.Open(Path.Combine(data, "library.db"));
    Migrator.Migrate(conn);
    return conn;
}

static LazyEmbeddingServer? LoadEmbedding(string data, string? configPath)
{
    try
    {
        return new LazyEmbeddingServer(M0Config.Load(configPath ?? Path.Combine(data, "config.json")));
    }
    catch (FileNotFoundException e)
    {
        Console.Error.WriteLine(e.Message);
        return null;
    }
}

static CancellationTokenSource CancelOnCtrlC()
{
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    return cts;
}

static async Task<int> AddCommand(string[] args)
{
    var (data, configPath, files) = CommonArgs(args);
    if (files.Length == 0) return Usage();
    await using var embedding = LoadEmbedding(data, configPath);
    if (embedding is null) return 1;
    using var conn = OpenLibrary(data);
    using var cts = CancelOnCtrlC();
    var service = new IngestService(conn, embedding, embedding);

    var failures = 0;
    for (var n = 0; n < files.Length; n++)
    {
        var label = $"[{n + 1}/{files.Length}] {Path.GetFileName(files[n])}";
        try
        {
            var added = await service.AddAsync(files[n], cts.Token);
            if (added.Duplicate)
            {
                var hint = added.Status is BookStatus.Queued or BookStatus.Indexing ? "; run `bookcli resume` to finish it" : "";
                Console.WriteLine($"{label}: duplicate, skipped (book {added.BookId}, {added.Status}{(added.Reason is null ? "" : ": " + added.Reason)}{hint})");
                continue;
            }
            failures += await IngestWithProgress(service, added.BookId, label, cts.Token);
        }
        catch (FileNotFoundException)
        {
            Console.WriteLine($"{label}: file not found");
            failures++;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"\n{label}: interrupted. Run `bookcli resume` to continue.");
            return 130;
        }
        catch (Exception e) when (e is BookSearch.Core.ModelUnavailableException or InvalidDataException or InvalidOperationException or TimeoutException)
        {
            Console.WriteLine($"\n{label}: stopped by an embedding server error: {e.Message}\nProgress is saved; run `bookcli resume` to continue.");
            return 1;
        }
    }
    return failures == 0 ? 0 : 1;
}

/// <summary>Indexes one book printing a live progress line; returns 1 if it ended rejected or failed.</summary>
static async Task<int> IngestWithProgress(IngestService service, long bookId, string label, CancellationToken ct)
{
    var drawn = false;
    var progress = new Progress<IngestProgress>(p =>
    {
        if (p.ChunksTotal > 0)
            Console.Write($"\r{label}: indexing {100 * p.ChunksDone / p.ChunksTotal}% ({p.ChunksDone}/{p.ChunksTotal} chunks)   ");
        drawn |= p.ChunksTotal > 0;
    });
    var started = System.Diagnostics.Stopwatch.StartNew();
    var result = await service.IngestAsync(bookId, progress, ct);
    if (drawn) Console.Write("\r" + new string(' ', 100) + "\r");

    if (result.Status == BookStatus.Ready)
    {
        var t = result.Timings;
        Console.WriteLine($"{label}: ready, {t.Chunks} chunks in {started.Elapsed.TotalMinutes:F1} min " +
                          $"(extract {t.Extract.TotalSeconds:F1}s, chunk {t.Chunk.TotalSeconds:F1}s, embed {t.Embed.TotalSeconds:F1}s, write {t.Write.TotalSeconds:F1}s)");
        return 0;
    }
    Console.WriteLine($"{label}: {result.Status}: {result.Reason}");
    return 1;
}

static async Task<int> ResumeCommand(string[] args)
{
    var (data, configPath, rest) = CommonArgs(args);
    if (rest.Length > 0) return Usage();
    using var conn = OpenLibrary(data);
    var jobs = new IngestJobRepository(conn).ListUnfinished();
    if (jobs.Count == 0)
    {
        Console.WriteLine("nothing to resume");
        return 0;
    }

    await using var embedding = LoadEmbedding(data, configPath);
    if (embedding is null) return 1;
    using var cts = CancelOnCtrlC();
    var service = new IngestService(conn, embedding, embedding);
    var books = new BookRepository(conn);

    var failures = 0;
    foreach (var (job, n) in jobs.Select((j, i) => (j, i)))
    {
        var book = books.GetById(job.BookId)!;
        var label = $"[{n + 1}/{jobs.Count}] {Path.GetFileName(book.FilePath)}";
        try
        {
            failures += await IngestWithProgress(service, job.BookId, label, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"\n{label}: interrupted. Run `bookcli resume` to continue.");
            return 130;
        }
        catch (Exception e) when (e is BookSearch.Core.ModelUnavailableException or InvalidDataException or InvalidOperationException or TimeoutException)
        {
            Console.WriteLine($"\n{label}: stopped by an embedding server error: {e.Message}\nProgress is saved; run `bookcli resume` to continue.");
            return 1;
        }
    }
    return failures == 0 ? 0 : 1;
}

static async Task<int> SearchCommand(string[] args)
{
    var (data, configPath, rest) = CommonArgs(args);
    var keywordOnly = rest.Contains("--keyword-only");
    var classic = rest.Contains("--classic-search");   // the ranking from before cross-language handling, for comparison
    var topN = new SearchOptions().TopN;
    var words = new List<string>();
    for (var i = 0; i < rest.Length; i++)
    {
        if (rest[i] == "--keyword-only" || rest[i] == "--classic-search") continue;
        if (rest[i] == "--top" && i + 1 < rest.Length && int.TryParse(rest[++i], out var n) && n > 0) topN = n;
        else if (rest[i].StartsWith("--")) return Usage();
        else words.Add(rest[i]);
    }
    var question = string.Join(" ", words).Trim();
    if (question.Length == 0) return Usage();

    using var conn = OpenLibrary(data);
    LazyEmbeddingServer? embedding = null;
    if (!keywordOnly)
    {
        embedding = LoadEmbedding(data, configPath);
        if (embedding is null) Console.Error.WriteLine("(continuing with keyword search only)\n");
    }

    await using var _ = embedding;
    using var cts = CancelOnCtrlC();
    var service = new SearchService(conn, embedding, (classic ? SearchOptions.Classic : new SearchOptions()) with { TopN = topN });
    SearchResult result;
    try
    {
        result = await service.SearchAsync(question, cts.Token);
    }
    catch (OperationCanceledException)
    {
        return 130;
    }

    if (result.Warning is not null) Console.WriteLine($"! {result.Warning}\n");
    if (result.NotCovered)
        Console.WriteLine(result.Hits.Count == 0
            ? "Your library doesn't cover this topic: nothing matched.\n"
            : $"Your library doesn't seem to cover this topic (best similarity {result.BestSimilarity:F2}, below {new SearchOptions().CoverageThreshold:F2}). Nearest passages:\n");

    var color = !Console.IsOutputRedirected;
    foreach (var (hit, n) in result.Hits.Select((h, i) => (h, i + 1)))
    {
        var where = hit.PageStart is null ? "" : hit.PageStart == hit.PageEnd ? $", p. {hit.PageStart}" : $", pp. {hit.PageStart}-{hit.PageEnd}";
        var relevance = hit.Similarity is { } sim ? $"similarity {sim:F2}" : "keyword match";
        Console.WriteLine($"{n}. {hit.BookTitle}{(hit.Author is null ? "" : " - " + hit.Author)}");
        Console.WriteLine($"   {hit.SectionTitle ?? "(untitled)"}{where}   [{relevance} | vector #{hit.VectorRank?.ToString() ?? "-"} | keyword #{hit.KeywordRank?.ToString() ?? hit.TrigramRank?.ToString() ?? "-"} | score {hit.Score:F4}]");
        Console.WriteLine("   " + Highlight(hit.Excerpt, hit.Highlights, color));
        Console.WriteLine();
    }

    var t = result.Timings;
    Console.WriteLine($"{result.Hits.Count} results in {t.Total:F0} ms " +
                      $"(keyword {t.Keyword:F0}, embed {t.Embed:F0}, vector {t.Vector:F0}, fuse {t.Fuse:F0}, load {t.Load:F0})");
    return 0;
}

static async Task<int> SummarizeCommand(string[] args)
{
    var (data, configPath, rest) = CommonArgs(args);
    int? passages = null;                       // null: every result the search returns
    var words = new List<string>();
    for (var i = 0; i < rest.Length; i++)
    {
        if (rest[i] == "--passages" && i + 1 < rest.Length && int.TryParse(rest[++i], out var n) && n > 0) passages = n;
        else if (rest[i].StartsWith("--")) return Usage();
        else words.Add(rest[i]);
    }
    var question = string.Join(" ", words).Trim();
    if (question.Length == 0) return Usage();

    M0Config config;
    try
    {
        config = M0Config.Load(configPath ?? Path.Combine(data, "config.json"));
    }
    catch (FileNotFoundException e)
    {
        Console.Error.WriteLine(e.Message);
        return 1;
    }
    if (string.IsNullOrEmpty(config.LlmModel))
    {
        Console.Error.WriteLine("config.json has no \"llmModel\". Add the path to a Qwen3.5 GGUF file to use Summarize.");
        return 1;
    }

    using var conn = OpenLibrary(data);
    await using var embedding = new LazyEmbeddingServer(config);
    await using var llm = new LazyChatServer(config);
    using var cts = CancelOnCtrlC();
    var splitter = SentenceSplitters.CreateDefault();

    try
    {
        var search = await new SearchService(conn, embedding).SearchAsync(question, cts.Token);
        if (search.Warning is not null) Console.WriteLine($"! {search.Warning}\n");
        if (search.NotCovered)
        {
            Console.WriteLine(search.Hits.Count == 0
                ? "Your library doesn't cover this topic: nothing matched. Summarize is not available."
                : $"Your library doesn't seem to cover this topic (best similarity {search.BestSimilarity:F2}). Summarize is not available; nearest passages:");
            foreach (var (hit, n) in search.Hits.Select((h, i) => (h, i + 1)))
                Console.WriteLine($"  {n}. {SummaryPrompt.Label(hit)}");
            return 0;
        }

        if (passages is { } limit) search = search with { Hits = search.Hits.Take(limit).ToList() };
        var service = new SummaryService(llm, embedding, splitter, new SummaryOptions(), new ConnectionChunkSource(conn));
        Console.WriteLine("One summary per result, in the order of the list:\n");
        var run = await service.SummarizeAsync(question, search,
            onPassageStarted: i => Console.Write($"[{i + 1}] {SummaryPrompt.Label(search.Hits[i])}\n    "),
            onToken: (_, token) => Console.Write(token.Replace("\n", "\n    ")),
            onPassage: p => Console.WriteLine(p.NotCovered ? "\n    (" + PassageSummary.NotCoveredMessage + ")\n" : "\n"),
            ct: cts.Token);

        var prompt = run.Passages.Sum(p => p.PromptTokens);
        Console.WriteLine($"Time to first token {run.TimeToFirstToken.TotalSeconds:F1} s, total {run.Total.TotalSeconds:F1} s " +
                          $"({run.Passages.Count} passages, prompts about {prompt} tokens in all)");
        return 0;
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\nStopped.");
        return 130;
    }
    catch (Exception e) when (e is BookSearch.Core.ModelUnavailableException or InvalidOperationException or InvalidDataException or TimeoutException)
    {
        Console.Error.WriteLine($"\nThe language model stopped with an error: {e.Message}");
        return 1;
    }
}

static string Highlight(string excerpt, IReadOnlyList<TextSpan> spans, bool color)
{
    var sb = new System.Text.StringBuilder();
    var at = 0;
    foreach (var span in spans.OrderBy(sp => sp.Start))
    {
        sb.Append(excerpt, at, span.Start - at);
        sb.Append(color ? "\u001b[1;33m" : "«").Append(excerpt, span.Start, span.Length).Append(color ? "\u001b[0m" : "»");
        at = span.End;
    }
    return sb.Append(excerpt, at, excerpt.Length - at).ToString();
}

static int RemoveCommand(string[] args)
{
    var (data, _, rest) = CommonArgs(args);
    if (rest.Length != 1 || !long.TryParse(rest[0], out var id)) return Usage();
    using var conn = OpenLibrary(data);
    var books = new BookRepository(conn);
    var book = books.GetById(id);
    if (book is null)
    {
        Console.Error.WriteLine($"No book with id {id}.");
        return 1;
    }
    var chunks = new ChunkRepository(conn).CountChunks(id);
    books.Delete(id);
    Console.WriteLine($"Removed \"{book.Title}\" (book {id}): {chunks} chunks and their vectors deleted.");
    return 0;
}

static int ListCommand(string[] args)
{
    var (data, _, rest) = CommonArgs(args);
    if (rest.Length > 0) return Usage();
    using var conn = OpenLibrary(data);
    var chunks = new ChunkRepository(conn);
    var books = new BookRepository(conn).List();
    if (books.Count == 0) Console.WriteLine("The library is empty.");
    foreach (var b in books)
        Console.WriteLine($"{b.Id,4}  {b.Status,-9} {chunks.CountChunks(b.Id),6} chunks  {b.Format,-4} {b.Language ?? "-",-3} {b.Title}" +
                          (b.StatusReason is null ? "" : $"  ({b.StatusReason})"));
    return 0;
}

static int StatsCommand(string[] args)
{
    var (data, _, rest) = CommonArgs(args);
    if (rest.Length > 0) return Usage();
    using var conn = OpenLibrary(data);
    var chunks = new ChunkRepository(conn);
    var books = new BookRepository(conn).List();

    Console.WriteLine($"Books: {books.Count} (" + string.Join(", ",
        books.GroupBy(b => b.Status).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}")) + ")");
    Console.WriteLine($"Chunks: {chunks.CountChunks()}");
    Console.WriteLine($"Vectors: {chunks.CountVectors()}");
    Console.WriteLine($"Keyword index rows: {chunks.CountFts()} (trigram: {chunks.CountFtsCjk()})");
    using var dup = conn.CreateCommand();
    dup.CommandText = "SELECT COUNT(*) FROM (SELECT book_id, chunk_index FROM chunk GROUP BY 1, 2 HAVING COUNT(*) > 1)";
    var duplicates = (long)dup.ExecuteScalar()!;
    Console.WriteLine($"Duplicate chunk indexes: {duplicates}");
    var unfinished = new IngestJobRepository(conn).ListUnfinished();
    Console.WriteLine($"Unfinished jobs: {unfinished.Count}");
    var consistent = chunks.CountChunks() == chunks.CountVectors() && chunks.CountChunks() == chunks.CountFts();
    Console.WriteLine(consistent ? "Indexes consistent: chunks, vectors and keyword rows match." : "WARNING: chunk, vector and keyword counts differ.");
    return consistent && duplicates == 0 ? 0 : 1;
}

static string DefaultDataFolder() =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BookSearch");
