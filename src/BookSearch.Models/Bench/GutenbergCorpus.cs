using System.Text.Json;
using System.Text.RegularExpressions;

namespace BookSearch.Models.Bench;

/// <summary>A public-domain book offered by Project Gutenberg, as listed by gutendex.com.</summary>
public sealed record CorpusBook(int Id, string Title, string Author, string Language, string EpubUrl, long Downloads)
{
    /// <summary>"1342-pride-and-prejudice.epub": the id keeps names unique, the title keeps them readable.</summary>
    public string FileName
    {
        get
        {
            var slug = Regex.Replace(Title.ToLowerInvariant(), @"[^\p{L}\p{N}]+", "-").Trim('-');
            if (slug.Length > 60) slug = slug[..60].Trim('-');
            return $"{Id}-{(slug.Length == 0 ? "book" : slug)}.epub";
        }
    }
}

/// <summary>
/// Builds a test library of public-domain books for the release measurements (ST-22): lists the most downloaded
/// non-copyrighted EPUBs of a language through the gutendex.com catalogue and downloads them one at a time, slowly, skipping
/// files already there so an interrupted run continues. A developer tool: the app itself never contacts Project Gutenberg.
/// </summary>
public sealed class GutenbergCorpus(HttpClient http, TimeSpan? delayBetweenDownloads = null)
{
    public const string CatalogueUrl = "https://gutendex.com/books/";
    private const string EpubType = "application/epub+zip";

    private readonly TimeSpan _delay = delayBetweenDownloads ?? TimeSpan.FromSeconds(2);

    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BookSearch-bench/1.0 (test library for local measurements)");
        return client;
    }

    /// <summary>The <paramref name="count"/> most downloaded public-domain EPUBs in <paramref name="language"/> (a code like "en").</summary>
    public async Task<IReadOnlyList<CorpusBook>> ListAsync(string language, int count, CancellationToken ct = default)
    {
        var books = new List<CorpusBook>();
        string? url = $"{CatalogueUrl}?languages={Uri.EscapeDataString(language)}&sort=popular";
        while (url is not null && books.Count < count)
        {
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            foreach (var item in doc.RootElement.GetProperty("results").EnumerateArray())
            {
                if (Parse(item) is { } book && !books.Any(b => b.Id == book.Id)) books.Add(book);
                if (books.Count >= count) break;
            }
            url = doc.RootElement.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            if (url is not null) await Task.Delay(_delay, ct).ConfigureAwait(false);
        }
        return books;
    }

    /// <summary>One catalogue entry, or null when it is copyrighted (or unknown), has no EPUB, or isn't in a single language.</summary>
    internal static CorpusBook? Parse(JsonElement item)
    {
        if (!item.TryGetProperty("copyright", out var copyright) || copyright.ValueKind != JsonValueKind.False) return null;   // only "known public domain"
        if (!item.TryGetProperty("formats", out var formats) || !formats.TryGetProperty(EpubType, out var epub) || epub.ValueKind != JsonValueKind.String) return null;
        var languages = item.GetProperty("languages").EnumerateArray().Select(l => l.GetString() ?? "").ToList();
        if (languages.Count != 1) return null;
        var author = item.TryGetProperty("authors", out var authors) && authors.GetArrayLength() > 0
            ? authors[0].GetProperty("name").GetString() ?? "" : "";
        var id = item.GetProperty("id").GetInt32();
        // The catalogue links the EPUB with every illustration (about 25 MB for Pride and Prejudice); the same book without
        // images is about 0.5 MB and has the same text, which is all the measurements need.
        return new CorpusBook(id, item.GetProperty("title").GetString() ?? "", author, languages[0],
            $"https://www.gutenberg.org/ebooks/{id}.epub.noimages", item.TryGetProperty("download_count", out var d) ? d.GetInt64() : 0);
    }

    /// <summary>Downloads the books into <paramref name="folder"/> (existing non-empty files are kept) and returns how many were fetched now.</summary>
    public async Task<int> DownloadAsync(IReadOnlyList<CorpusBook> books, string folder, Action<int, int, CorpusBook, bool>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var fetched = 0;
        for (var i = 0; i < books.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var book = books[i];
            var path = Path.Combine(folder, book.FileName);
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                progress?.Invoke(i + 1, books.Count, book, false);
                continue;
            }
            var part = path + ".part";
            using (var response = await http.GetAsync(book.EpubUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var target = File.Create(part);
                await response.Content.CopyToAsync(target, ct).ConfigureAwait(false);
            }
            File.Move(part, path, overwrite: true);
            fetched++;
            progress?.Invoke(i + 1, books.Count, book, true);
            if (i < books.Count - 1) await Task.Delay(_delay, ct).ConfigureAwait(false);   // be gentle with the servers
        }
        File.WriteAllText(Path.Combine(folder, "corpus.json"), JsonSerializer.Serialize(books, new JsonSerializerOptions { WriteIndented = true }));
        return fetched;
    }
}
