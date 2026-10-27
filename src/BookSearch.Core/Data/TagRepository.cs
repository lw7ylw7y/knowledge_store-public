using Microsoft.Data.Sqlite;

namespace BookSearch.Core.Data;

/// <summary>A tag as the filter list shows it, with how many books carry it.</summary>
public sealed record TagInfo(string Name, int Books)
{
    public string Key => Tags.KeyOf(Name);
}

/// <summary>Rules for tag text: trimmed, one space between words, no leading '#', at most 40 characters, case-insensitive.</summary>
public static class Tags
{
    public const int MaxLength = 40;

    /// <summary>The tag as it will be shown, or null when nothing is left of it.</summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var words = text.Trim().TrimStart('#').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var clean = string.Join(' ', words);
        if (clean.Length > MaxLength) clean = clean[..MaxLength].TrimEnd();
        return clean.Length == 0 ? null : clean;
    }

    public static string KeyOf(string tag) => tag.Trim().ToLowerInvariant();

    /// <summary>Splits "novels, Austen; to read" into clean, distinct tags (commas, semicolons and line breaks separate them).</summary>
    public static List<string> Parse(string? text)
    {
        var tags = new List<string>();
        foreach (var part in (text ?? "").Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            if (Clean(part) is { } tag && !tags.Any(t => KeyOf(t) == KeyOf(tag))) tags.Add(tag);
        return tags;
    }
}

/// <summary>The tags on books (ST-25). Run on a connection used by one thread at a time.</summary>
public sealed class TagRepository(SqliteConnection conn)
{
    /// <summary>Every tag in use with its book count, by name.</summary>
    public List<TagInfo> List()
    {
        using var cmd = Sql.Cmd(conn, null, """
            SELECT MIN(tag), COUNT(*) FROM book_tag GROUP BY tag_key ORDER BY tag_key
            """);
        using var r = cmd.ExecuteReader();
        var list = new List<TagInfo>();
        while (r.Read()) list.Add(new TagInfo(r.GetString(0), r.GetInt32(1)));
        return list;
    }

    /// <summary>The tags of every book that has any, by book id.</summary>
    public Dictionary<long, List<string>> ForAllBooks()
    {
        using var cmd = Sql.Cmd(conn, null, "SELECT book_id, tag FROM book_tag ORDER BY book_id, tag_key");
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<long, List<string>>();
        while (r.Read())
        {
            var id = r.GetInt64(0);
            if (!map.TryGetValue(id, out var list)) map[id] = list = [];
            list.Add(r.GetString(1));
        }
        return map;
    }

    public List<string> ForBook(long bookId)
    {
        using var cmd = Sql.Cmd(conn, null, "SELECT tag FROM book_tag WHERE book_id = $b ORDER BY tag_key", ("$b", bookId));
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>Replaces the book's tags with <paramref name="tags"/> (cleaned; duplicates dropped).</summary>
    public void Set(long bookId, IEnumerable<string> tags)
    {
        var clean = tags.Select(Tags.Clean).OfType<string>().GroupBy(Tags.KeyOf).Select(g => g.First()).ToList();
        Database.InTransaction(conn, tx =>
        {
            Sql.Cmd(conn, tx, "DELETE FROM book_tag WHERE book_id = $b", ("$b", bookId)).ExecuteNonQuery();
            foreach (var tag in clean) Insert(tx, bookId, tag);
        });
    }

    /// <summary>Adds tags to several books at once; a book that already has one keeps it.</summary>
    public void Add(IEnumerable<long> bookIds, IEnumerable<string> tags)
    {
        var clean = tags.Select(Tags.Clean).OfType<string>().GroupBy(Tags.KeyOf).Select(g => g.First()).ToList();
        var ids = bookIds.Distinct().ToList();
        Database.InTransaction(conn, tx =>
        {
            foreach (var id in ids)
                foreach (var tag in clean) Insert(tx, id, tag);
        });
    }

    private void Insert(SqliteTransaction tx, long bookId, string tag)
    {
        // Another book may have spelled it differently: keep one spelling per tag, the one already in use.
        using var existing = Sql.Cmd(conn, tx, "SELECT MIN(tag) FROM book_tag WHERE tag_key = $k", ("$k", Tags.KeyOf(tag)));
        var spelling = existing.ExecuteScalar() as string ?? tag;
        Sql.Cmd(conn, tx, "INSERT OR IGNORE INTO book_tag(book_id, tag_key, tag) VALUES ($b, $k, $t)",
            ("$b", bookId), ("$k", Tags.KeyOf(tag)), ("$t", spelling)).ExecuteNonQuery();
    }

    /// <summary>Ids of the books that carry at least one of the tags.</summary>
    public HashSet<long> BookIdsWithAny(IEnumerable<string> tags)
    {
        var keys = tags.Select(Tags.KeyOf).Distinct().ToList();
        var ids = new HashSet<long>();
        foreach (var key in keys)
        {
            using var cmd = Sql.Cmd(conn, null, "SELECT book_id FROM book_tag WHERE tag_key = $k", ("$k", key));
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
        }
        return ids;
    }

    /// <summary>
    /// The indexed books a filter allows: books with any of its tags plus the books picked by name, limited to books that are
    /// ready. Null for an empty filter (search everything); an empty set when the filter matches no indexed book.
    /// </summary>
    public HashSet<long>? Resolve(Search.SearchFilter filter)
    {
        if (filter.IsEmpty) return null;
        var ids = BookIdsWithAny(filter.Tags);
        foreach (var book in filter.Books) ids.Add(book.Id);
        var ready = new HashSet<long>();
        foreach (var id in ids)
        {
            using var cmd = Sql.Cmd(conn, null, "SELECT status FROM book WHERE id = $id", ("$id", id));
            if (cmd.ExecuteScalar() as string == BookStatus.Ready) ready.Add(id);
        }
        return ready;
    }
}
