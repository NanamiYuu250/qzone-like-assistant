namespace QzoneLikeAssistant;

internal sealed record RefreshContextCheckpoint(
    bool IsMain,
    IReadOnlyList<string> Keys,
    string Identity = "");

internal static class RefreshContextMatcher
{
    private const int PreferredSharedKeys = 2;

    public static IReadOnlyDictionary<int, int> Match(
        IReadOnlyList<RefreshContextCheckpoint> previous,
        IReadOnlyList<RefreshContextCheckpoint> current)
    {
        var candidatesByCurrent = new Dictionary<int, List<int>>();
        var candidatesByPrevious = new Dictionary<int, List<int>>();

        for (var currentIndex = 0; currentIndex < current.Count; currentIndex += 1)
        {
            var currentKeys = current[currentIndex].Keys
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToHashSet(StringComparer.Ordinal);
            if (currentKeys.Count == 0) continue;

            for (var previousIndex = 0; previousIndex < previous.Count; previousIndex += 1)
            {
                if (current[currentIndex].IsMain != previous[previousIndex].IsMain) continue;
                var shared = previous[previousIndex].Keys
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Distinct(StringComparer.Ordinal)
                    .Count(currentKeys.Contains);
                if (shared == 0) continue;
                if (shared < PreferredSharedKeys &&
                    !CanUseSingleAnchor(previous, current, previousIndex, currentIndex)) continue;

                candidatesByCurrent.TryAdd(currentIndex, []);
                candidatesByCurrent[currentIndex].Add(previousIndex);
                candidatesByPrevious.TryAdd(previousIndex, []);
                candidatesByPrevious[previousIndex].Add(currentIndex);
            }
        }

        var matches = new Dictionary<int, int>();
        foreach (var (currentIndex, previousCandidates) in candidatesByCurrent)
        {
            if (previousCandidates.Count != 1) continue;
            var previousIndex = previousCandidates[0];
            if (candidatesByPrevious[previousIndex].Count != 1) continue;
            matches[currentIndex] = previousIndex;
        }
        return matches;
    }

    private static bool CanUseSingleAnchor(
        IReadOnlyList<RefreshContextCheckpoint> previous,
        IReadOnlyList<RefreshContextCheckpoint> current,
        int previousIndex,
        int currentIndex)
    {
        var before = previous[previousIndex];
        var after = current[currentIndex];
        if (before.IsMain && after.IsMain) return true;

        var identity = before.Identity.Trim();
        if (identity.Length == 0 ||
            !string.Equals(identity, after.Identity.Trim(), StringComparison.Ordinal)) return false;

        var previousIdentityCount = previous.Count(context =>
            context.IsMain == before.IsMain &&
            string.Equals(context.Identity.Trim(), identity, StringComparison.Ordinal));
        var currentIdentityCount = current.Count(context =>
            context.IsMain == after.IsMain &&
            string.Equals(context.Identity.Trim(), identity, StringComparison.Ordinal));
        return previousIdentityCount == 1 && currentIdentityCount == 1;
    }
}
