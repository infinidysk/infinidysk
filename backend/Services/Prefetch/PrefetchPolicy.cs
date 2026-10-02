using NzbWebDAV.Services.Plex;

namespace NzbWebDAV.Services.Prefetch;

public static class PrefetchPolicy
{
    public sealed record SourceCandidateStatus(string Status, string Reason);
    public static SourceCandidateStatus DescribeSourceCandidate(PlexMediaItem item, PrefetchSettings settings,
        IReadOnlyList<PlexPathMapping> mappings, PrefetchSource? source = null)
    {
        if (!IsEligible(item, settings, source)) return new("excluded", "Excluded show, disabled source, or disabled media policy.");
        if (item.Type == "show") return new("episodes-required", "Show candidates require episode expansion; use Policy Preview to inspect imported episodes.");
        if (item.File is null) return new("metadata-required", "Plex returned no file path; metadata must be resolved before warming.");
        var mapped = PrefetchPathResolver.Map(item.File, mappings);
        if (mapped is null) return new("unmapped", "No exact configured path mapping.");
        if (mapped.LocalPath is not null && !settings.WarmLocalFiles) return new("excluded", "Mapped local-library warming is disabled.");
        return new("mapped", "Exact path mapping found; Policy Preview verifies the imported item and warming limits.");
    }
    public static IReadOnlyList<PlexMediaItem> HistoryCandidates(IEnumerable<PlexMediaItem> items, PrefetchSettings settings, string serverId, DateTimeOffset now)
    {
        var selected = items.Take(1000).Where(item => IsEligible(item, settings)
            && item.ViewedAt >= now.AddDays(-settings.LookbackDays).ToUnixTimeSeconds()
            && (settings.Users.Length == 0 || settings.Users.Contains(serverId + ":" + item.UserId, StringComparer.Ordinal)))
            .OrderByDescending(item => item.ViewedAt).ToArray();
        var movies = selected.Where(item => item.Type == "movie" && item.ViewOffset > 0
            && item.Duration > item.ViewOffset && item.ViewedAt >= now.AddDays(-settings.MoviePartialWatchLookbackDays).ToUnixTimeSeconds())
            .DistinctBy(item => (item.UserId, item.RatingKey));
        var episodes = selected.Where(item => item.Type == "episode" && item.ShowRatingKey is not null)
            .GroupBy(item => (item.UserId, item.ShowRatingKey))
            .Where(group => group.DistinctBy(item => item.RatingKey).Count() >= settings.MinEpisodesForPrediction)
            .Where(group => Math.Min(1, group.DistinctBy(item => item.RatingKey).Count() / (double)Math.Max(4, settings.MinEpisodesForPrediction)) >= settings.ConfidenceThreshold)
            .Select(group => group.First());
        return movies.Concat(episodes).Take(128).ToArray();
    }

    public static IReadOnlyList<(long Start, long Length)> Ranges(long length, long viewOffset, long duration, PrefetchSettings settings, bool minimum)
    {
        if (length <= 0) return [];
        const long block = 4L * 1024 * 1024;
        if (minimum)
        {
            var head = Math.Min(length, settings.MinimumHeadMb * 1024L * 1024);
            var tail = Math.Min(length, settings.MinimumTailMb * 1024L * 1024);
            var tailStart = (length - tail) / block * block;
            if (tail > 0 && tailStart <= head) return [(0, length)];
            var result = new List<(long, long)>(2);
            if (head > 0) result.Add((0, head));
            if (tail > 0) result.Add((tailStart, length - tailStart));
            return result;
        }
        if (settings.FullFileWarming) return [(0, 0)];
        // Time-to-byte is only a hint; the normal stream remains authoritative for seeking.
        var offset = duration > 0 ? (long)((decimal)Math.Clamp(viewOffset, 0, duration) / duration * length) : 0;
        var start = Math.Max(0, offset - block) / block * block;
        return [(start, Math.Min(64L * 1024 * 1024, length - start))];
    }
    public static bool IsEligible(PlexMediaItem item, PrefetchSettings settings, PrefetchSource? source = null) =>
        (item.Type == "movie" && settings.MovieEnabled || item.Type is "episode" or "show" && settings.TvEnabled)
        && source?.Enabled != false && (source is null || !source.ExcludedShows.Contains(item.ShowRatingKey ?? item.RatingKey, StringComparer.Ordinal));

    public static IReadOnlyList<PlexMediaItem> NextEpisodes(PlexMediaItem current, IEnumerable<PlexMediaItem> items, int limit) =>
        items.Where(item => item.Type == "episode" && item.ShowRatingKey == (current.ShowRatingKey ?? current.RatingKey)
            && item.Season is > 0 && item.Episode is > 0 && item.RatingKey != current.RatingKey
            && (current.Type == "show" || item.Season > current.Season || item.Season == current.Season && item.Episode > current.Episode))
            .OrderBy(item => item.Season).ThenBy(item => item.Episode).DistinctBy(item => item.RatingKey)
            .Take(Math.Clamp(limit, 1, 20)).ToArray();
}

public static class PrefetchPathResolver
{
    public sealed record MappedPath(string? DavPath, string? LocalPath);
    public static MappedPath? Map(string file, IReadOnlyList<PlexPathMapping> mappings)
    {
        if (string.IsNullOrWhiteSpace(file) || file.Length > 4096 || file.Any(char.IsControl)) return null;
        var normalized = file.Replace('\\', '/');
        if (normalized.Split('/').Any(part => part is "." or "..")) return null;
        var comparison = normalized.Length > 2 && normalized[1] == ':' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var matches = mappings.Select(mapping => (Mapping: mapping, Prefix: mapping.PlexPath.Replace('\\', '/').TrimEnd('/')))
            .Where(entry => normalized.StartsWith(entry.Prefix + "/", comparison))
            .OrderByDescending(entry => entry.Prefix.Length).ToArray();
        if (matches.Length == 0) return null;
        var selected = matches[0];
        if (matches.Skip(1).Any(entry => entry.Prefix.Length == selected.Prefix.Length && entry.Mapping != selected.Mapping)) return null;
        var suffix = normalized[selected.Prefix.Length..];
        return new(selected.Mapping.DavPath is { } dav ? dav.TrimEnd('/') + suffix : null,
            selected.Mapping.LocalPath is { } local ? local.TrimEnd('/', '\\') + suffix : null);
    }
}
