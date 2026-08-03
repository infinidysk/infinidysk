using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Utils;

/// <summary>
/// Pure, unit-testable selection logic for picking the "best" video candidate among a
/// release's video files for Profiles/manifest play. Extracted from
/// <c>ProfilePlayController.FindBestVideoAsync</c> so the episode-tag / token-overlap /
/// decoy-skip behavior can be exercised without a controller or database.
/// </summary>
internal static class VideoSelection
{
    private static readonly char[] TokenSeparators = ['.', '_', '-', ' ', '(', ')', '[', ']', '{', '}', '+'];

    /// <summary>
    /// Flags obviously-fake "decoy" releases (e.g. rar-bomb/rickroll uploads named
    /// <c>*.unpack.mkv</c>) that indexers occasionally ship alongside — or instead of — the
    /// real episode. The leading dot on <c>".unpack."</c> is intentional: it guards against
    /// matching legitimate filenames like <c>Unpacked.2017.mkv</c>.
    /// </summary>
    internal static bool IsProbablyDecoy(string name) =>
        name.Contains(".unpack.", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("rickroll", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when this candidate should be skipped as a sample relative to the other videos.
    /// Uses size ratio when sizes are known; falls back to whole-word name matching when not.
    /// </summary>
    internal static bool IsSampleCandidate(DavItem candidate, IReadOnlyList<DavItem> videos)
    {
        var largest = videos.Select(v => v.FileSize ?? 0).DefaultIfEmpty(0).Max();
        if (largest > 0 && candidate.FileSize is > 0)
            return FileFilterUtil.IsSampleFile(candidate.Name, candidate.FileSize, largest);

        // Unknown size: still skip whole-word sample names so playback does not prefer them.
        return FileFilterUtil.LooksLikeSampleName(candidate.Name);
    }

    /// <summary>
    /// Selects the best video among <paramref name="videos"/> (already filtered to
    /// video-content-type files).
    /// </summary>
    /// <param name="videos">Candidate video files. Must be non-empty for a non-null result.</param>
    /// <param name="season">Requested season, when the manifest id encodes a season/episode.</param>
    /// <param name="episode">Requested episode, when the manifest id encodes a season/episode.</param>
    /// <param name="primaryTitle">The clicked release title, used for token-overlap scoring.</param>
    internal static DavItem? SelectBest(
        IReadOnlyList<DavItem> videos,
        int? season,
        int? episode,
        string? primaryTitle)
    {
        if (videos.Count == 0) return null;

        if (season is { } s && episode is { } e)
        {
            var episodeFile = SelectEpisodeFile(videos, s, e);
            if (episodeFile is not null) return episodeFile;
        }

        if (primaryTitle is null) return PickFallback(videos);

        var clickTokens = TokenizeName(primaryTitle);
        if (clickTokens.Count == 0) return PickFallback(videos);

        DavItem? best = null;
        var bestScore = 0;
        long bestSize = -1;
        foreach (var v in videos)
        {
            if (IsSampleCandidate(v, videos)) continue;
            if (IsProbablyDecoy(v.Name)) continue;
            var fileTokens = TokenizeName(Path.GetFileNameWithoutExtension(v.Name));
            if (fileTokens.Count == 0) continue;
            var score = fileTokens.Count(t => clickTokens.Contains(t));
            var size = v.FileSize ?? 0;
            if (score > bestScore || (score == bestScore && score > 0 && size > bestSize))
            {
                best = v;
                bestScore = score;
                bestSize = size;
            }
        }
        return bestScore > 0 ? best : null;
    }

    private static DavItem? SelectEpisodeFile(IReadOnlyList<DavItem> videos, int season, int episode)
    {
        DavItem? best = null;
        long bestSize = -1;
        foreach (var v in videos)
        {
            if (IsSampleCandidate(v, videos)) continue;
            if (IsProbablyDecoy(v.Name)) continue;
            if (FilenameMatcher.ParseEpisode(v.Name) is not { } tag) continue;
            if (tag.Season != season) continue;
            if (tag.Episode is not { } start) continue;
            var end = tag.EpisodeEnd ?? start;
            if (episode < start || episode > end) continue;
            var size = v.FileSize ?? 0;
            if (size > bestSize)
            {
                best = v;
                bestSize = size;
            }
        }
        return best;
    }

    /// <summary>
    /// Used when there's no episode-tag match and no (or no useful) title to score against —
    /// falls back to the largest file. Prefers non-decoy candidates, but a false-positive
    /// decoy match must never make playback return "nothing playable": if every candidate
    /// looks like a decoy (e.g. it's the release's only video), fall back to the unfiltered list.
    /// </summary>
    private static DavItem PickFallback(IReadOnlyList<DavItem> videos)
    {
        var nonDecoy = videos.Where(v => !IsProbablyDecoy(v.Name) && !IsSampleCandidate(v, videos)).ToList();
        var pool = nonDecoy.Count > 0 ? nonDecoy : videos;
        return pool.Count == 1 ? pool[0] : pool.OrderByDescending(x => x.FileSize ?? 0).First();
    }

    private static HashSet<string> TokenizeName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return s.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 1)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
