namespace NzbWebDAV.Utils;

public static class ProfileIdentityFilter
{
    public readonly record struct ExpectedIdentity(
        IReadOnlySet<string> NormalizedTitles,
        int? MovieYear);

    public static bool HasCanonicalIdentity(ExpectedIdentity identity)
        => identity.NormalizedTitles.Count > 0 || identity.MovieYear.HasValue;

    public static List<T> ApplyStrictMatching<T>(
        IReadOnlyList<T> items,
        Func<T, string> indexerName,
        Func<T, string?> title,
        IReadOnlySet<string> strictIndexers,
        ExpectedIdentity identity,
        bool applyMovieYear)
    {
        if (strictIndexers.Count == 0) return items.ToList();

        if (HasCanonicalIdentity(identity))
        {
            return items
                .Where(item => !strictIndexers.Contains(indexerName(item))
                               || MatchesCanonical(title(item), identity, applyMovieYear))
                .ToList();
        }

        // Consensus fallback is only used when canonical metadata could not be resolved,
        // and still requires at least two results before it will reject anything.
        if (items.Count < 2) return items.ToList();

        var withHead = items
            .Select(item => (Entry: item, Head: FilenameMatcher.HeadTokens(title(item))))
            .ToList();
        var consensus = withHead
            .Where(x => x.Head.Length > 0)
            .GroupBy(x => string.Join(' ', x.Head))
            .Select(g => new { g.First().Head, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .FirstOrDefault();
        if (consensus is not { Count: >= 2 }) return items.ToList();

        return withHead
            .Where(x => !strictIndexers.Contains(indexerName(x.Entry))
                        || FilenameMatcher.TokensEqual(x.Head, consensus.Head))
            .Select(x => x.Entry)
            .ToList();
    }

    private static bool MatchesCanonical(string? releaseTitle, ExpectedIdentity identity, bool applyMovieYear)
    {
        if (identity.NormalizedTitles.Count > 0
            && !FilenameMatcher.TitleMatches(identity.NormalizedTitles, releaseTitle))
        {
            return false;
        }

        return !applyMovieYear
               || FilenameMatcher.YearCompatible(identity.MovieYear, releaseTitle, identity.NormalizedTitles);
    }
}
