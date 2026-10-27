namespace BookSearch.Core.Worker;

public static class CpuCap
{
    /// <summary>Embedding threads for a CPU cap: the share of the cores, at least one. 100% uses every core.</summary>
    public static int ThreadsFor(int capPercent, int cores) =>
        Math.Clamp((int)Math.Round(cores * Math.Clamp(capPercent, 1, 100) / 100.0), 1, Math.Max(1, cores));
}
