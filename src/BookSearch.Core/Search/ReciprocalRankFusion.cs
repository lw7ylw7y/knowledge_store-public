namespace BookSearch.Core.Search;

/// <summary>One fused result: the id, its summed score, and its 1-based rank in each input list (null if absent).</summary>
public sealed record FusedItem(long Id, double Score, IReadOnlyList<int?> Ranks);

public static class ReciprocalRankFusion
{
    /// <summary>
    /// Merges ranked lists: each appearance adds <c>1 / (k + rank)</c>, so an item found by several lists beats one
    /// found by a single list. Ties keep the order in which items first appeared, so results are deterministic.
    /// </summary>
    public static List<FusedItem> Fuse(IReadOnlyList<IReadOnlyList<long>> rankedLists, int top, int k = 60)
    {
        var scores = new Dictionary<long, double>();
        var ranks = new Dictionary<long, int?[]>();
        var firstSeen = new List<long>();

        for (var list = 0; list < rankedLists.Count; list++)
            for (var i = 0; i < rankedLists[list].Count; i++)
            {
                var id = rankedLists[list][i];
                if (!scores.ContainsKey(id))
                {
                    scores[id] = 0;
                    ranks[id] = new int?[rankedLists.Count];
                    firstSeen.Add(id);
                }
                if (ranks[id][list] is not null) continue; // a repeated id keeps its best rank
                scores[id] += 1.0 / (k + i + 1);
                ranks[id][list] = i + 1;
            }

        return firstSeen
            .Select((id, order) => (id, order))
            .OrderByDescending(x => scores[x.id])
            .ThenBy(x => x.order)
            .Take(top)
            .Select(x => new FusedItem(x.id, scores[x.id], ranks[x.id]))
            .ToList();
    }
}
