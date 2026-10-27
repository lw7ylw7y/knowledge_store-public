using System.Text.Json;

namespace BookSearch.Core.Search;

/// <summary>A book picked by name in the filter. The title is kept so the filter can be shown (and restored) without the library.</summary>
public sealed record FilterBook(long Id, string Title);

/// <summary>
/// The user's explicit filter for a search (ST-25): books that carry any of the <see cref="Tags"/> plus the individually picked
/// <see cref="Books"/>. An empty filter searches everything.
/// </summary>
public sealed record SearchFilter(IReadOnlyList<string> Tags, IReadOnlyList<FilterBook> Books)
{
    public static readonly SearchFilter None = new([], []);

    public bool IsEmpty => Tags.Count == 0 && Books.Count == 0;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The form kept with a history entry; null for no filter.</summary>
    public string? ToJson() => IsEmpty ? null : JsonSerializer.Serialize(this, Json);

    public static SearchFilter FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return None;
        try { return JsonSerializer.Deserialize<SearchFilter>(json, Json) is { } f && !f.IsEmpty ? f : None; }
        catch (JsonException) { return None; }
    }

    /// <summary>Tags first, then book titles: "novels, Austen, Pride and Prejudice".</summary>
    public string Describe() => string.Join(", ", Tags.Concat(Books.Select(b => b.Title)));
}
