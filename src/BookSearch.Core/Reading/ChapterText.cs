using BookSearch.Core.Data;

namespace BookSearch.Core.Reading;

/// <summary>Where one chunk sits in the rebuilt chapter text.</summary>
public sealed record ChunkRange(long ChunkId, int ChunkIndex, int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>A chapter's text rebuilt from its chunks, with the position of each chunk in it.</summary>
public sealed record ChapterText(string Text, IReadOnlyList<ChunkRange> Ranges)
{
    /// <summary>Text pulled out of the book is cut into chunks that repeat their last sentences at the start of the next one;
    /// this joins them back, dropping each repeat, so the Reader can show the chapter as continuous text.</summary>
    /// <remarks>
    /// The repeat is a copy of whole sentences, so the end of one chunk and the start of the next are the same characters. The
    /// longest such match is taken; if there is none (a chunk that carried nothing over) the chunks are joined with a blank line.
    /// </remarks>
    public static ChapterText Merge(IReadOnlyList<Chunk> chunks)
    {
        var text = new System.Text.StringBuilder();
        var ranges = new List<ChunkRange>(chunks.Count);
        foreach (var chunk in chunks.OrderBy(c => c.ChunkIndex))
        {
            var overlap = text.Length == 0 ? 0 : OverlapLength(text, chunk.Text);
            if (text.Length > 0 && overlap == 0) text.Append("\n\n");
            var start = text.Length - overlap;
            text.Append(chunk.Text, overlap, chunk.Text.Length - overlap);
            ranges.Add(new ChunkRange(chunk.Id, chunk.ChunkIndex, start, chunk.Text.Length));
        }
        return new ChapterText(text.ToString(), ranges);
    }

    /// <summary>Length of the longest suffix of <paramref name="so_far"/> that is also a prefix of <paramref name="next"/> (at least a few characters).</summary>
    private static int OverlapLength(System.Text.StringBuilder so_far, string next)
    {
        const int minimum = 8;
        var max = Math.Min(next.Length - 1, Math.Min(so_far.Length, 4000));
        for (var length = max; length >= minimum; length--)
        {
            var matches = true;
            for (var i = 0; i < length; i++)
                if (so_far[so_far.Length - length + i] != next[i]) { matches = false; break; }
            if (matches) return length;
        }
        return 0;
    }
}
