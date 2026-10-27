namespace BookSearch.Models.Management;

/// <summary>Chooses the model tier from the machine's memory (CPU only in v1).</summary>
public static class HardwareTiers
{
    private const long Gb = 1024L * 1024 * 1024;

    /// <summary>
    /// From this much RAM the 4B model is recommended. Windows reports about 15.9 GB for a "16 GB" PC and about 7.8 GB for
    /// an "8 GB" one, so the line sits between them with room to spare.
    /// </summary>
    public const long RecommendedMinRamBytes = 12 * Gb;

    /// <summary>Below this the app still works with the smallest model, but the first-run screen should warn.</summary>
    public const long BelowMinimumRamBytes = 6 * Gb;

    public static ModelTier Select(long totalRamBytes) =>
        totalRamBytes >= RecommendedMinRamBytes ? ModelTier.Recommended : ModelTier.Minimum;

    /// <summary>
    /// Tokens the chat server keeps in memory for all its requests at once (<c>-c</c>; llama-server's own default of
    /// automatic slots shares this pool). The larger pool is recommended from the same RAM line as the 4B model.
    /// </summary>
    public const int StandardChatContext = 4096, LargeChatContext = 8192;

    public static int SelectChatContext(long totalRamBytes) =>
        totalRamBytes >= RecommendedMinRamBytes ? LargeChatContext : StandardChatContext;

    public static bool IsBelowMinimum(long totalRamBytes) => totalRamBytes < BelowMinimumRamBytes;

    /// <summary>Total physical memory as the runtime sees it (it respects container and job limits).</summary>
    public static long DetectTotalRamBytes() => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    public static double ToGb(long bytes) => bytes / (double)Gb;
}
