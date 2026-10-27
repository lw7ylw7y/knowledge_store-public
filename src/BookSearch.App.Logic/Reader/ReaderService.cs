using BookSearch.Core.Data;
using BookSearch.Core.Reading;
using Microsoft.Data.Sqlite;

namespace BookSearch.App.Reader;

public sealed record ReaderBook(long Id, string Title, string? Author, string? Language, string FilePath);

/// <param name="Index">Position of this chapter among the book's chapters that have text, from 0.</param>
/// <param name="HighlightChunkId">The passage the user came from, if it is in this chapter.</param>
/// <param name="ResumeParagraph">The paragraph to scroll to when the book was opened at its saved place (from 0).</param>
public sealed record ReaderChapter(
    ReaderBook Book, int Index, int Count, string? Title, ChapterText Text, int? PageStart, int? PageEnd, long? HighlightChunkId,
    int ResumeParagraph = 0);

public interface IReaderService
{
    /// <summary>The chapter holding a search result, with that result marked; null if the chunk no longer exists.</summary>
    Task<ReaderChapter?> LoadForChunkAsync(long chunkId);

    /// <summary>Chapter number <paramref name="index"/> (from 0) of a book, or null if there is no such chapter.</summary>
    Task<ReaderChapter?> LoadChapterAsync(long bookId, int index);

    /// <summary>
    /// A book opened as a whole (DCR-10): at the place the reader stopped, or at its first chapter if it was never opened or the
    /// place no longer exists. Null if the book is gone; a chapter with <c>Count</c> 0 if the book has no text.
    /// </summary>
    Task<ReaderChapter?> LoadBookAsync(long bookId);

    /// <summary>Remembers where the reader is in a book: the chapter number and the first visible paragraph (both from 0).</summary>
    Task SavePositionAsync(long bookId, int chapterIndex, int paragraph);
}

/// <summary>Rebuilds chapter text from the stored chunks, so the Reader works for every format and without the original file.</summary>
public sealed class ReaderService : IReaderService, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly BookRepository _books;
    private readonly SectionRepository _sections;
    private readonly ChunkRepository _chunks;
    private readonly ReadingPositionRepository _positions;

    public ReaderService(AppPaths paths)
    {
        _conn = Database.Open(paths.DatabasePath);
        _books = new BookRepository(_conn);
        _sections = new SectionRepository(_conn);
        _chunks = new ChunkRepository(_conn);
        _positions = new ReadingPositionRepository(_conn);
    }

    public Task<ReaderChapter?> LoadForChunkAsync(long chunkId) => Locked(() =>
    {
        if (_chunks.GetById(chunkId) is not { } chunk || _books.GetById(chunk.BookId) is not { } book) return null;
        var order = _chunks.SectionsInReadingOrder(book.Id);
        var index = Math.Max(0, order.IndexOf(chunk.SectionId));
        return Build(book, order, index, chunkId);
    });

    public Task<ReaderChapter?> LoadChapterAsync(long bookId, int index) => Locked(() =>
    {
        if (_books.GetById(bookId) is not { } book) return null;
        var order = _chunks.SectionsInReadingOrder(bookId);
        return index < 0 || index >= order.Count ? null : Build(book, order, index, null);
    });

    public Task<ReaderChapter?> LoadBookAsync(long bookId) => Locked(() =>
    {
        if (_books.GetById(bookId) is not { } book) return null;
        var order = _chunks.SectionsInReadingOrder(bookId);
        if (order.Count == 0)
            return new ReaderChapter(Describe(book), 0, 0, null, ChapterText.Merge([]), null, null, null);
        var saved = _positions.Get(bookId);
        var index = saved is null ? 0 : Math.Clamp(saved.SectionPos, 0, order.Count - 1);   // a book indexed again falls back to the nearest chapter
        var chapter = Build(book, order, index, null);
        return saved is null ? chapter : chapter with { ResumeParagraph = saved.Paragraph };
    });

    public Task SavePositionAsync(long bookId, int chapterIndex, int paragraph) => Locked(() =>
    {
        _positions.Save(bookId, chapterIndex, paragraph);
        return 0;
    });

    private static ReaderBook Describe(Book book) =>
        new(book.Id, book.Title ?? Path.GetFileNameWithoutExtension(book.FilePath), book.Author, book.Language, book.FilePath);

    private ReaderChapter Build(Book book, List<long?> order, int index, long? highlight)
    {
        var sectionId = order[index];
        var chunks = _chunks.ListBySection(book.Id, sectionId);
        var title = sectionId is { } id ? _sections.ListByBook(book.Id).FirstOrDefault(s => s.Id == id)?.Title : null;
        var pages = chunks.Where(c => c.PageStart is not null).ToList();
        var chapter = ChapterText.Merge(chunks);
        return new ReaderChapter(
            Describe(book),
            index, order.Count, title, chapter,
            pages.Count > 0 ? pages.Min(c => c.PageStart) : null,
            pages.Count > 0 ? pages.Max(c => c.PageEnd ?? c.PageStart) : null,
            highlight is { } h && chapter.Ranges.Any(r => r.ChunkId == h) ? h : null);
    }

    private async Task<T> Locked<T>(Func<T> work)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(work).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public void Dispose() => _conn.Dispose();
}
