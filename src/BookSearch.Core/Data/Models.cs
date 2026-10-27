namespace BookSearch.Core.Data;

public static class BookStatus
{
    public const string Queued = "queued";
    public const string Indexing = "indexing";
    public const string Ready = "ready";
    public const string Failed = "failed";
    public const string Rejected = "rejected";
}

public static class IngestStage
{
    public const string Extract = "extract";
    public const string Chunk = "chunk";
    public const string Embed = "embed";
    public const string Done = "done";
}

public sealed record Book
{
    public long Id { get; init; }
    public required string FilePath { get; init; }
    public required string FileHash { get; init; }
    public string? Title { get; init; }
    public string? Author { get; init; }
    public required string Format { get; init; }
    public string? Language { get; init; }
    public string Status { get; init; } = BookStatus.Queued;
    public string? StatusReason { get; init; }
    public string? EmbeddingModelId { get; init; }
    public DateTime AddedAt { get; init; } = DateTime.UtcNow;
}

public sealed record Section
{
    public long Id { get; init; }
    public long BookId { get; init; }
    public long? ParentId { get; init; }
    public string? Title { get; init; }
    public int Order { get; init; }
}

public sealed record Chunk
{
    public long Id { get; init; }
    public long BookId { get; init; }
    public long? SectionId { get; init; }
    public int ChunkIndex { get; init; }
    public required string Text { get; init; }
    public int TokenCount { get; init; }
    public int? PageStart { get; init; }
    public int? PageEnd { get; init; }
    public string? Location { get; init; }
}

/// <summary>A chunk ready to be written with its embedding; <see cref="Cjk"/> also feeds the trigram index.</summary>
public sealed record ChunkWrite(Chunk Chunk, float[] Embedding, bool Cjk = false);

public sealed record IngestJob(long BookId, string Stage, int NextChunkIndex, double Progress, string? Error, DateTime UpdatedAt, int Attempts = 0);

public sealed record VectorHit(long ChunkId, double Distance);

/// <summary>Where a search's summary stands; stored in <c>search_history.summary_status</c>.</summary>
public static class SummaryStatus
{
    /// <summary>In the queue behind an earlier summary.</summary>
    public const string Waiting = "waiting";
    public const string Writing = "writing";
    public const string Ready = "ready";
    /// <summary>The user stopped it (it can be written again).</summary>
    public const string Stopped = "stopped";
    public const string Failed = "failed";
    /// <summary>The library doesn't cover the question, so no summary was written.</summary>
    public const string NotCovered = "not_covered";
    /// <summary>No summary was requested (automatic summaries were off).</summary>
    public const string None = "none";

    /// <summary>Waiting or writing: not finished, so it is completed after the next start.</summary>
    public static bool IsUnfinished(string status) => status is Waiting or Writing;
}

/// <summary>One past search: the question, when it was made, and its summary (plain text, no citation markers).</summary>
public sealed record SearchHistoryEntry(
    long Id, string Question, DateTime CreatedAt, string Status, string? SummaryText, string? SummaryModel, int? SummaryMs,
    long? ThreadId = null, string? Filter = null, string? SearchedAs = null, IReadOnlyList<string>? AlsoSearched = null);

/// <summary>A conversation: its questions in order. The title is the first question; <see cref="UpdatedAt"/> is the time of the last.</summary>
public sealed record SearchThread(long Id, DateTime CreatedAt, DateTime UpdatedAt, string Title, int Questions);

public sealed record WorkerControl(bool Paused, int CpuCapPercent);

/// <summary>A chunk joined with its book and section, as shown in search results.</summary>
public sealed record Passage(
    long ChunkId, long BookId, string? BookTitle, string? Author, string? SectionTitle,
    string Text, int? PageStart, int? PageEnd, string? Location, string? Language = null, int ChunkIndex = 0, long? SectionId = null);
