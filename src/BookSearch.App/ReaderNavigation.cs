using BookSearch.App.Reader;

namespace BookSearch.App;

/// <summary>
/// What the Reader page is given when it is opened: the shared view model and either the passage to show (from a search
/// result) or the book to read from its saved place (from the Library).
/// </summary>
public sealed record ReaderNavigation(ReaderViewModel Reader, ReaderRequest? Request, long? BookId = null)
{
    public static ReaderNavigation ForPassage(ReaderViewModel reader, ReaderRequest request) => new(reader, request);
    public static ReaderNavigation ForBook(ReaderViewModel reader, long bookId) => new(reader, null, bookId);
}
