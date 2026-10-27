using BookSearch.Core.Chunking;
using BookSearch.Core.Evaluation;
using BookSearch.Core.Ingest;
using BookSearch.Core.Data;
using BookSearch.Core.Search;
using BookSearch.Core.Worker;
using BookSearch.Models;
using Xunit;

namespace BookSearch.Tests;

/// <summary>Other scripts end to end (ST-19): Thai and Arabic through extract, chunk, store and search, and the cross-language eval.</summary>
public class WorldReadinessTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly List<string> _files = [];

    public void Dispose()
    {
        _db.Dispose();
        foreach (var f in _files) File.Delete(f);
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    /// <summary>Copies a file to a temp name (so each test gets its own file hash) and runs it through the real ingest with stand-in models.</summary>
    private async Task<long> IngestAsync(string path)
    {
        var copy = Path.Combine(Path.GetTempPath(), $"world-{Guid.NewGuid():N}{Path.GetExtension(path)}");
        File.Copy(path, copy);
        _files.Add(copy);
        var ingest = new IngestService(_db.Conn, new FakeEmbedder(), new WordCountTokenizer(), new PunctuationSentenceSplitter());
        var added = await ingest.AddAsync(copy);
        await new IngestWorker(_db.Conn, ingest).RunAsync();
        return added.BookId;
    }

    [Fact]
    public async Task Fts_ThaiWord_FoundByTrigram()
    {
        // Thai is written without spaces, so only the trigram index can find a word inside a sentence.
        var thai = Path.Combine(Path.GetTempPath(), $"thai-{Guid.NewGuid():N}.txt");
        File.WriteAllText(thai, "ฉันชอบอ่านหนังสือทุกวันก่อนนอน เพราะมันทำให้ใจสงบ\n\nวันหนึ่งฉันพบหนังสือเก่าที่ตลาดริมแม่น้ำ และซื้อมันมาในราคาถูก\n");
        _files.Add(thai);
        var bookId = await IngestAsync(thai);
        Assert.Equal("th", new BookRepository(_db.Conn).GetById(bookId)!.Language);

        var result = await new SearchService(_db.Conn, null).SearchAsync("หนังสือเก่า");

        Assert.True(result.QueriedTrigramIndex);
        var hit = Assert.Single(result.Hits);
        Assert.Contains("หนังสือเก่า", hit.Excerpt);
        Assert.NotEmpty(hit.Highlights);
    }

    [Fact]
    public async Task Fts_JapaneseKeyword_FoundByTrigramInTheFixtureBook()
    {
        await IngestAsync(Fixture("kokoro.epub"));

        var result = await new SearchService(_db.Conn, null).SearchAsync("海水浴");

        Assert.Contains(result.Hits, h => h.Excerpt.Contains("海水浴"));
    }

    [Fact]
    public async Task Excerpt_ArabicText_PreservesCharacters()
    {
        var bookId = await IngestAsync(Fixture("arabic.txt"));
        var source = File.ReadAllText(Fixture("arabic.txt"));

        // Every stored chunk is Arabic text taken from the file, with nothing replaced.
        var stored = string.Join(" ", _db.Chunks.SectionsInReadingOrder(bookId).SelectMany(sec => _db.Chunks.ListBySection(bookId, sec)).Select(c => c.Text));
        Assert.DoesNotContain('�', stored);
        Assert.Contains("كان يا ما كان في قديم الزمان", stored);
        Assert.All(new[] { "الكتب", "المكتبات", "كتابا قديما" }, word => Assert.Contains(word, source));

        var result = await new SearchService(_db.Conn, null).SearchAsync("كتابا قديما");

        var hit = Assert.Single(result.Hits);
        Assert.DoesNotContain('�', hit.Excerpt);
        Assert.Contains("كتابا قديما", hit.Excerpt);
        Assert.NotEmpty(hit.Highlights);
        Assert.True(BookSearch.App.Ask.TextDirection.IsRightToLeft(hit.Excerpt));
    }

    // ---- real models (skipped unless BOOKSEARCH_M0_CONFIG is set) --------------------------------------------------

    [SkippableFact]
    public async Task Eval_CrossLanguage_Recall5AtLeast70()
    {
        var config = Environment.GetEnvironmentVariable("BOOKSEARCH_M0_CONFIG");
        Skip.If(string.IsNullOrEmpty(config), "BOOKSEARCH_M0_CONFIG not set");

        // English questions that must find Spanish, Japanese and Arabic passages. Only the three small books are indexed.
        var books = new Dictionary<string, long>();
        await using var embedding = new LazyEmbeddingServer(M0Config.Load(config!));
        var ingest = new IngestService(_db.Conn, embedding, embedding);
        foreach (var file in new[] { "quijote.epub", "kokoro.epub", "arabic.txt" })
        {
            var copy = Path.Combine(Path.GetTempPath(), $"world-{Guid.NewGuid():N}-{file}");
            File.Copy(Fixture(file), copy);
            _files.Add(copy);
            var added = await ingest.AddAsync(copy);
            await ingest.IngestAsync(added.BookId);
            books[file] = added.BookId;
        }
        var questions = EvalQuestions.Load(Fixture("questions.json"))
            .Where(q => q.Type == "cross-language" && books.ContainsKey(q.ExpectedFile ?? ""))
            .ToList();
        Assert.True(questions.Count >= 6);

        var results = await EvalRunner.RunAsync(new SearchService(_db.Conn, embedding), questions, f => books.GetValueOrDefault(f));

        Assert.True(EvalMetrics.RecallAtK(results, 5) >= 0.70, $"cross-language recall@5 was {EvalMetrics.RecallAtK(results, 5):P0}");
    }
}
