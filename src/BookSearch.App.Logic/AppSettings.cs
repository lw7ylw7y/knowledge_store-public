using System.Text.Json;
using System.Text.Json.Serialization;
using BookSearch.Models.Management;

namespace BookSearch.App;

public sealed record AppSettings
{
    /// <summary>The user got through the first-run wizard once.</summary>
    public bool WizardCompleted { get; init; }

    /// <summary>The tier the user chose when it differs from the recommended one; null follows the hardware check.</summary>
    public ModelTier? TierOverride { get; init; }

    /// <summary>
    /// Write a summary in the background after every search (DCR-1). When off, the Summarize button writes one on request.
    /// Search itself never waits for the language model either way.
    /// </summary>
    public bool AutoSummary { get; init; } = true;

    /// <summary>
    /// Also search a question in the other languages of the library (DCR-11): the language model writes the question in each of them
    /// and the search runs in all. Like the follow-up rewrite it needs the chat model, so it only happens with automatic summaries on.
    /// </summary>
    public bool SearchOtherLanguages { get; init; } = true;

    /// <summary>
    /// The conversation shown on the Ask page, restored on the next start (ST-24). Null after "New thread", so the page starts
    /// blank until a question is asked.
    /// </summary>
    public long? CurrentThreadId { get; init; }

    /// <summary>How many passages a search shows and a summary is written from (ST-18). See <see cref="SettingsLimits"/>.</summary>
    public int ResultCount { get; init; } = SettingsLimits.DefaultResultCount;

    /// <summary>Chunk size and overlap in tokens for books added from now on (ST-18). Books already indexed keep their chunks.</summary>
    public int ChunkTargetTokens { get; init; } = SettingsLimits.DefaultChunkTokens;
    public int ChunkOverlapTokens { get; init; } = SettingsLimits.DefaultOverlapTokens;

    /// <summary>
    /// How many earlier questions of the conversation a follow-up uses as context, for the search and for the summary alike
    /// (0 to 3). 0 searches and summarizes every question on its own.
    /// </summary>
    public int ContextQuestions { get; init; } = SettingsLimits.DefaultContextQuestions;

    /// <summary>
    /// The tags and books the Ask page limits every search to (<see cref="BookSearch.Core.Search.SearchFilter.ToJson"/>), kept
    /// until the user clears it; null searches the whole library (ST-25).
    /// </summary>
    public string? Filter { get; init; }

    /// <summary>How much text surrounds each result in the summary: 0 small, 1 medium, 2 large (ST-26). More is clearer and slower.</summary>
    public int SummaryContext { get; init; } = 1;

    /// <summary>
    /// Tokens the language model server keeps in memory, shared by all its requests (the summary being written and a question
    /// being rewritten at the same time) (<see cref="HardwareTiers.StandardChatContext"/> or
    /// <see cref="HardwareTiers.LargeChatContext"/>) when the user chose a value other than the recommended one; null follows
    /// the hardware check. Applies the next time the language model starts.
    /// </summary>
    public int? ChatContextTokens { get; init; }

    /// <summary>
    /// The model download is the only network use (NFR-9). When off, the wizard refuses to download and the user imports
    /// the model files from disk instead.
    /// </summary>
    public bool DownloadsAllowed { get; init; } = true;
}

/// <summary>The ranges the Settings page accepts. A value from settings.json outside them is clamped when used.</summary>
public static class SettingsLimits
{
    public const int DefaultResultCount = 3, MinResultCount = 1, MaxResultCount = 10;
    public const int DefaultChunkTokens = 500, MinChunkTokens = 200, MaxChunkTokens = 1000;
    public const int DefaultOverlapTokens = 75, MinOverlapTokens = 0, MaxOverlapTokens = 200;
    public const int MinCpuPercent = 10, MaxCpuPercent = 100;
    public const int DefaultContextQuestions = 1, MinContextQuestions = 0, MaxContextQuestions = 3;
    public static int ContextQuestions(AppSettings s) => Math.Clamp(s.ContextQuestions, MinContextQuestions, MaxContextQuestions);

    /// <summary>The chat server's token pool: the user's choice if it is one of the two sizes, else the recommended one.</summary>
    public static int ChatContext(AppSettings s, int recommended) =>
        s.ChatContextTokens is HardwareTiers.StandardChatContext or HardwareTiers.LargeChatContext ? s.ChatContextTokens.Value : recommended;

    public static int ResultCount(AppSettings s) => Math.Clamp(s.ResultCount, MinResultCount, MaxResultCount);
    public static int ChunkTokens(AppSettings s) => Math.Clamp(s.ChunkTargetTokens, MinChunkTokens, MaxChunkTokens);

    /// <summary>The overlap, never more than a third of the chunk so consecutive chunks still advance.</summary>
    public static int OverlapTokens(AppSettings s) =>
        Math.Min(Math.Clamp(s.ChunkOverlapTokens, MinOverlapTokens, MaxOverlapTokens), ChunkTokens(s) / 3);
}

/// <summary>Raised when a model download is asked for while "Allow model downloads" is off.</summary>
public sealed class DownloadsDisabledException() : InvalidOperationException("Model downloads are turned off in Settings.");

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

/// <summary>settings.json in the data folder. A missing or unreadable file gives the defaults, so first run just works.</summary>
public sealed class SettingsStore(string path) : ISettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public AppSettings Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
        File.Move(temp, path, overwrite: true);   // a crash mid-write never leaves a half-written settings file
    }
}
