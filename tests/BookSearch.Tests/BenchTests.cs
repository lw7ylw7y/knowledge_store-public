using System.Net;
using System.Text;
using BookSearch.Core.Data;
using BookSearch.Core.Evaluation;
using BookSearch.Core.Search;
using BookSearch.Models;
using BookSearch.Models.Bench;

namespace BookSearch.Tests;

/// <summary>The release measurement tools (ST-22): the public-domain test library and the benchmark on an existing library.</summary>
public class BenchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bench-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class Catalogue(Dictionary<string, string> pages) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            if (pages.TryGetValue(url, out var body))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static string Item(int id, string title, string? copyright = "false", bool epub = true, string languages = "[\"en\"]") => $$"""
        {"id": {{id}}, "title": "{{title}}", "authors": [{"name": "Author, {{id}}"}], "languages": {{languages}}, "copyright": {{copyright ?? "null"}},
         "download_count": {{1000 - id}}, "formats": { {{(epub ? "\"application/epub+zip\": \"https://www.gutenberg.org/ebooks/" + id + ".epub3.images\"," : "")}} "text/html": "x" } }
        """;

    private static string Page(string? next, params string[] items) =>
        $$"""{"count": 99, "next": {{(next is null ? "null" : "\"" + next + "\"")}}, "results": [{{string.Join(",", items)}}]}""";

    private const string First = "https://gutendex.com/books/?languages=en&sort=popular";
    private const string Second = "https://gutendex.com/books/?languages=en&page=2&sort=popular";

    [Fact]
    public async Task Corpus_List_KeepsOnlyPublicDomainSingleLanguageEpubsAndFollowsThePages()
    {
        var handler = new Catalogue(new()
        {
            [First] = Page(Second, Item(1, "Alpha"), Item(2, "Copyrighted", copyright: "true"), Item(3, "Unknown status", copyright: null),
                Item(4, "No epub", epub: false), Item(5, "Two languages", languages: "[\"en\",\"fr\"]")),
            [Second] = Page(null, Item(6, "Beta"), Item(1, "Alpha again"), Item(7, "Gamma")),
        });
        var corpus = new GutenbergCorpus(new HttpClient(handler), TimeSpan.Zero);

        var books = await corpus.ListAsync("en", 10);

        Assert.Equal([1, 6, 7], books.Select(b => b.Id));                       // a book listed twice counts once
        Assert.Equal([First, Second], handler.Requests);
        Assert.Equal("https://www.gutenberg.org/ebooks/1.epub.noimages", books[0].EpubUrl);   // the small version without pictures
        Assert.Equal("Author, 1", books[0].Author);
    }

    [Fact]
    public async Task Corpus_List_StopsAtTheRequestedCount()
    {
        var handler = new Catalogue(new() { [First] = Page(Second, Item(1, "A"), Item(2, "B"), Item(3, "C")), [Second] = Page(null, Item(4, "D")) });

        var books = await new GutenbergCorpus(new HttpClient(handler), TimeSpan.Zero).ListAsync("en", 2);

        Assert.Equal([1, 2], books.Select(b => b.Id));
        Assert.Equal([First], handler.Requests);                                // the second page was never needed
    }

    [Fact]
    public async Task Corpus_Download_SavesFilesSkipsExistingOnesAndWritesAList()
    {
        var handler = new Catalogue(new()
        {
            ["https://www.gutenberg.org/ebooks/1.epub.noimages"] = "book one",
            ["https://www.gutenberg.org/ebooks/2.epub.noimages"] = "book two",
        });
        var corpus = new GutenbergCorpus(new HttpClient(handler), TimeSpan.Zero);
        CorpusBook[] books =
        [
            new(1, "The First: A Book!", "A", "en", "https://www.gutenberg.org/ebooks/1.epub.noimages", 5),
            new(2, "Second", "B", "en", "https://www.gutenberg.org/ebooks/2.epub.noimages", 4),
        ];
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, books[0].FileName), "already here");        // an earlier run got this one

        var fetched = await corpus.DownloadAsync(books, _dir);

        Assert.Equal(1, fetched);
        Assert.Equal("already here", File.ReadAllText(Path.Combine(_dir, "1-the-first-a-book.epub")));
        Assert.Equal("book two", File.ReadAllText(Path.Combine(_dir, "2-second.epub")));
        Assert.Empty(Directory.GetFiles(_dir, "*.part"));
        Assert.Contains("\"Title\": \"Second\"", File.ReadAllText(Path.Combine(_dir, "corpus.json")));
        Assert.Single(handler.Requests);                                                  // only the missing one was requested
    }

    [Fact]
    public async Task Corpus_Download_ServerError_LeavesNoHalfFile()
    {
        var corpus = new GutenbergCorpus(new HttpClient(new Catalogue(new())), TimeSpan.Zero);
        CorpusBook[] books = [new(9, "Missing", "A", "en", "https://www.gutenberg.org/ebooks/9.epub.noimages", 1)];

        await Assert.ThrowsAsync<HttpRequestException>(() => corpus.DownloadAsync(books, _dir));

        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Theory]
    [InlineData(1342, "Pride and Prejudice", "1342-pride-and-prejudice.epub")]
    [InlineData(5, "!!!", "5-book.epub")]
    [InlineData(7, "Don Quijote de la Mancha: edición ilustrada", "7-don-quijote-de-la-mancha-edición-ilustrada.epub")]
    public void Corpus_FileNames_AreReadableAndUnique(int id, string title, string expected) =>
        Assert.Equal(expected, new CorpusBook(id, title, "", "en", "u", 0).FileName);

    // ---- the benchmark on an existing library ------------------------------------------------------------------

    [Fact]
    public void Bench_ExistingLibrary_ReportsSearchTimesAndLeavesIndexingSpeedUnmeasured()
    {
        var result = new BenchResult(new DateTime(2026, 10, 9), "test PC", "embedder", null, 200, 52_000, double.NaN,
            Enumerable.Range(1, 100).Select(i => i * 20.0).ToList(), [], [], []);

        var markdown = result.ToMarkdown();

        Assert.Contains("| Indexing speed | not measured (an existing library was used)", markdown);
        Assert.Contains("| Search p95 | 1900 ms (p50 1000 ms, 100 searches) | 10000 ms or less | pass |", markdown);
        Assert.Contains("200 books, 52000 chunks", markdown);
        Assert.DoesNotContain("FAIL", markdown);
    }

    [SkippableFact]
    public async Task Bench_200Books_SearchP95Under10s()
    {
        // Integration, run on the user's PC: BOOKSEARCH_BENCH_DATA is the app's data folder (library.db with about 200 books
        // indexed through the app) and BOOKSEARCH_M0_CONFIG points at the embedding model. The app should be closed.
        var data = Environment.GetEnvironmentVariable("BOOKSEARCH_BENCH_DATA");
        var config = Environment.GetEnvironmentVariable("BOOKSEARCH_M0_CONFIG");
        Skip.If(string.IsNullOrEmpty(data) || string.IsNullOrEmpty(config), "BOOKSEARCH_BENCH_DATA and BOOKSEARCH_M0_CONFIG are not set");

        using var conn = Database.Open(Path.Combine(data!, "library.db"));
        Migrator.Migrate(conn);
        var books = new BookRepository(conn).List().Count(b => b.Status == BookStatus.Ready);
        Skip.If(books < 200, $"the library has {books} indexed books; the measurement needs 200");

        await using var embedding = new LazyEmbeddingServer(M0Config.Load(config!));
        var search = new SearchService(conn, embedding);
        var questions = EvalQuestions.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "questions.json")).Where(q => !q.IsOutOfLibrary).ToList();
        await search.SearchAsync(questions[0].Question);                                  // the first search starts the server
        var times = new List<double>();
        foreach (var q in questions) times.Add((await search.SearchAsync(q.Question)).Timings.Total);

        Assert.True(EvalMetrics.Percentile(times, 95) <= 10_000, $"search p95 was {EvalMetrics.Percentile(times, 95):F0} ms");
    }
}
