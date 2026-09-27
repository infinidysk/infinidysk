using NzbWebDAV.Services.Plex;
using NzbWebDAV.Services.NativeCache;
using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace NzbWebDAV.Services.Library;

/// <summary>
/// Classifies indexed files by their Plex filename match, then groups matched
/// files using the recognized link directories.
/// </summary>
public sealed class LibraryBrowseService(LibraryCatalogService catalog, IPlexLibraryMetadataIndex plexMetadata,
    NativeCacheService? nativeCache = null)
{
    private const int GroupFilePageSize = 50;
    private const int TablePageSize = 50;
    public async Task<LibraryBrowseResult> QueryAsync(
        LibraryBrowseQuery query,
        LibraryCatalogScanner? scanner = null,
        CancellationToken ct = default)
    {
        var all = await catalog.LoadAllAsync(ct).ConfigureAwait(false);
        // Cache filters need coverage for the whole catalogue. Ordinary browsing
        // only displays it for the current page, so defer the expensive source
        // revision checks until the visible files are known.
        var candidates = all
            .Where(i => query.TypeFilter switch
            {
                "internal" => i.Kind == "internal",
                "external" => i.Kind == "external",
                "broken" => i.Mappings.Any(m => m.Status is "broken" or "stale"),
                _ => true,
            })
            .Where(i => query.Quality == "all" ||
                string.Equals(QualityFromName(i.DisplayName), query.Quality, StringComparison.OrdinalIgnoreCase))
            .Select(i =>
            {
                var plex = Match(i, plexMetadata);
                return new ClassifiedItem(i, Classify(i, plex), plex, null);
            })
            .Where(i => string.IsNullOrWhiteSpace(query.Search) ||
                MatchesSearch(i, query.Search.Trim()))
            .ToList();
        var currentCoverage = query.Cache == "all"
            ? null : await LoadCurrentCoverageAsync(candidates.Select(i => i.Item), ct).ConfigureAwait(false);
        var matched = candidates
            .Select(i => i with { CachePercentage = CachePercentage(i.Item, currentCoverage) })
            .Where(i => query.Cache switch
            {
                "any" => i.CachePercentage > 0,
                "complete" => i.CachePercentage == 100,
                "empty" => i.CachePercentage == 0,
                "unavailable" => i.CachePercentage is null,
                _ => true,
            })
            .ToList();

        if (query.View == "files")
        {
            var selectedFiles = matched.Where(i => (query.Category == "all" ||
                i.Identity.Category == query.Category) &&
                (query.MatchFilter == "all" ||
                    (query.MatchFilter == "matched" ? i.PlexMatch is not null : i.PlexMatch is null)) &&
                (query.SeasonFilter is null || i.PlexMatch?.Season == query.SeasonFilter))
                .OrderBy(i => i.Identity.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.PlexMatch?.Season ?? int.MaxValue)
                .ThenBy(i => i.PlexMatch?.Episode ?? int.MaxValue)
                .ThenBy(i => i.Item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var filePage = Math.Clamp(query.Page, 1,
                Math.Max(1, (selectedFiles.Count + TablePageSize - 1) / TablePageSize));
            var visibleFiles = selectedFiles.Skip((filePage - 1) * TablePageSize).Take(TablePageSize).ToList();
            var fileCoverage = query.Cache == "all"
                ? await LoadCurrentCoverageAsync(visibleFiles.Select(i => i.Item), ct).ConfigureAwait(false)
                : currentCoverage;
            var files = visibleFiles
                .Select(i => new LibraryBrowseFileRowDto(i.Item,
                    i.PlexMatch is null ? null :
                        i.Identity.Category == "shows" ? i.PlexMatch.ShowName : i.PlexMatch.Title,
                    i.PlexMatch?.Season, i.PlexMatch?.Episode, i.Identity.Category,
                    QualityFromName(i.Item.DisplayName), CachePercentage(i.Item, fileCoverage)))
                .ToList();
            return new LibraryBrowseResult([], 0, filePage, TablePageSize,
                matched.Count, matched.Count(i => IsHealthy(i.Item)),
                matched.Count(i => NeedsAttention(i.Item)),
                plexMetadata.Status.Ready ? matched.Count(i => i.Identity.Category == "unmatched") : 0,
                null, scanner?.LastSuccessfulScanAt, scanner?.LastScanWarning, plexMetadata.Status,
                files, selectedFiles.Count);
        }

        var selected = plexMetadata.Status.Ready
            ? matched.Where(i => i.Identity.Category == query.Category)
            : Enumerable.Empty<ClassifiedItem>();
        var groups = selected
            .GroupBy(i => i.Identity.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Group(g.Key, g.First().Identity.Title,
                g.First().Identity.Category, g.ToList()))
            .OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        var pageSize = Math.Clamp(query.PageSize, 1, 24);
        var page = Math.Clamp(query.Page, 1, Math.Max(1, (groups.Count + pageSize - 1) / pageSize));
        var visibleGroups = groups.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var expandedGroup = groups.FirstOrDefault(g =>
            string.Equals(g.Key, query.GroupKey, StringComparison.OrdinalIgnoreCase));
        var groupPage = expandedGroup is null ? 1 : Math.Clamp(query.GroupPage, 1,
            Math.Max(1, (expandedGroup.Items.Count + GroupFilePageSize - 1) / GroupFilePageSize));
        var expandedFiles = expandedGroup?.Items
            .OrderBy(i => i.PlexMatch?.Season ?? 999)
            .ThenBy(i => i.PlexMatch?.Episode ?? 9999)
            .ThenBy(i => i.Item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Skip((groupPage - 1) * GroupFilePageSize)
            .Take(GroupFilePageSize).ToList() ?? [];
        var visibleCoverage = query.Cache == "all"
            ? await LoadCurrentCoverageAsync(visibleGroups.Where(g => g.Items.Count == 1)
                .Select(g => g.Items[0].Item).Concat(expandedFiles.Select(i => i.Item)), ct).ConfigureAwait(false)
            : currentCoverage;
        var pageGroups = visibleGroups
            .Select(g => new LibraryBrowseGroupDto(g.Key, g.Title, g.Category,
                g.Items.Count, g.Items.Count(i => IsHealthy(i.Item)), g.Items.Count(i => NeedsAttention(i.Item)),
                g.Items.Count == 1 ? QualityFromName(g.Items[0].Item.DisplayName) : null,
                g.Items.Count == 1 ? CachePercentage(g.Items[0].Item, visibleCoverage) : null))
            .ToList();

        LibraryBrowseExpandedGroupDto? expanded = null;
        if (expandedGroup is not null)
        {
            var files = expandedFiles
                .Select(i =>
                {
                    var season = i.PlexMatch?.Season;
                    var episode = i.PlexMatch?.Episode;
                    return new LibraryBrowseFileDto(i.Item,
                        season.HasValue ? $"Season {season}" : null,
                        season.HasValue && episode.HasValue ? $"S{season:00}E{episode:00}" : null,
                        QualityFromName(i.Item.DisplayName), CachePercentage(i.Item, visibleCoverage));
                })
                .ToList();
            expanded = new LibraryBrowseExpandedGroupDto(expandedGroup.Key,
                groupPage, GroupFilePageSize, expandedGroup.Items.Count, files);
        }

        return new LibraryBrowseResult(pageGroups, groups.Count, page, pageSize,
            matched.Count, matched.Count(i => IsHealthy(i.Item)),
            matched.Count(i => NeedsAttention(i.Item)),
            plexMetadata.Status.Ready ? matched.Count(i => i.Identity.Category == "unmatched") : 0, expanded,
            scanner?.LastSuccessfulScanAt, scanner?.LastScanWarning, plexMetadata.Status);
    }

    private async Task<Dictionary<string, int?>?> LoadCurrentCoverageAsync(
        IEnumerable<LibraryCatalogItemDto> items, CancellationToken ct)
    {
        if (nativeCache?.InitializationPending == true)
            await nativeCache.WaitForInitializationAsync(ct).ConfigureAwait(false);
        if (nativeCache?.Store is not { } store) return null;
        try
        {
            var coverage = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
            var cachedIds = await store.GetCachedItemIdsAsync(ct).ConfigureAwait(false);
            var ids = items.Where(item => item.DavItemId is { } id && cachedIds.Contains(id.ToString("N")))
                .Select(item => item.DavItemId!.Value).Distinct().ToArray();
            foreach (var item in await catalog.LoadItemsByIdsAsync(ids, ct).ConfigureAwait(false))
            {
                try { coverage[item.Id.ToString("N")] = await nativeCache.GetCurrentCoverageAsync(item, ct).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException
                    or NzbWebDAV.Exceptions.CorruptedBlobPayloadException or ObjectDisposedException)
                { coverage[item.Id.ToString("N")] = null; }
            }
            return coverage;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException or ObjectDisposedException)
        {
            // Cache information is optional; a catalogue problem must not hide the library.
            return null;
        }
    }

    private static int? CachePercentage(LibraryCatalogItemDto item, IReadOnlyDictionary<string, int?>? coverage) =>
        item.DavItemId is { } id && coverage is not null
            ? coverage.GetValueOrDefault(id.ToString("N"), 0) : null;

    private static bool IsHealthy(LibraryCatalogItemDto item) =>
        item.Health == "healthy";

    private static bool NeedsAttention(LibraryCatalogItemDto item) =>
        item.Health is "attention" or "unmapped";

    private static bool MatchesSearch(ClassifiedItem item, string search) =>
        LibraryCatalogService.MatchesSearch(item.Item, search)
        || (item.PlexMatch?.ShowName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
        || (item.PlexMatch?.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
        || (item.PlexMatch?.Season is { } season &&
            ($"Season {season}".Contains(search, StringComparison.OrdinalIgnoreCase) ||
             (item.PlexMatch.Episode is { } episode &&
              $"S{season:00}E{episode:00}".Contains(search, StringComparison.OrdinalIgnoreCase))));

    internal static string QualityFromName(string name)
    {
        if (Regex.IsMatch(name, @"(?i)(?:^|[^a-z0-9])(?:2160p|4k|uhd)(?:[^a-z0-9]|$)")) return "4k";
        if (Regex.IsMatch(name, @"(?i)(?:^|[^a-z0-9])1080[pi]?(?:[^a-z0-9]|$)")) return "1080p";
        if (Regex.IsMatch(name, @"(?i)(?:^|[^a-z0-9])720[pi]?(?:[^a-z0-9]|$)")) return "720p";
        if (Regex.IsMatch(name, @"(?i)(?:^|[^a-z0-9])(?:480[pi]?|576[pi]?|sd)(?:[^a-z0-9]|$)")) return "sd";
        return "unknown";
    }

    private static string? PrimaryLinkPath(LibraryCatalogItemDto item) =>
        item.Mappings.OrderBy(m => m.LinkPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()?.LinkPath;

    private static PlexLibraryMedia? Match(LibraryCatalogItemDto item, IPlexLibraryMetadataIndex metadata)
    {
        foreach (var mapping in item.Mappings.OrderBy(m => m.LinkPath, StringComparer.OrdinalIgnoreCase))
        {
            var match = metadata.Match(mapping.LinkPath);
            if (match is not null) return match;
        }
        return metadata.Match(item.DisplayName);
    }

    private static GroupIdentity Classify(LibraryCatalogItemDto item, PlexLibraryMedia? plex)
    {
        if (plex is null)
        {
            var unmatchedKey = item.DavItemId?.ToString("D") ?? PrimaryLinkPath(item) ?? item.DisplayName;
            return new GroupIdentity("unmatched", $"unmatched/{unmatchedKey}", item.DisplayName);
        }
        var category = plex.MediaType == "episode" ? "shows" : "movies";
        if (item.Kind == "internal")
        {
            foreach (var mapping in item.Mappings
                .Where(m => m.MappingType == "internal")
                .OrderBy(m => m.LinkPath, StringComparer.OrdinalIgnoreCase))
            {
                var segments = mapping.LinkPath.Replace('\\', '/').Split('/',
                    StringSplitOptions.RemoveEmptyEntries);
                for (var index = 0; index < Math.Min(3, segments.Length - 1); index++)
                {
                    var root = segments[index].ToLowerInvariant();
                    var folderCategory = root switch
                    {
                        "tv" or "shows" or "tv shows" or "series" or "television" => "shows",
                        "movies" or "films" => "movies",
                        _ when root.Length > 3 && root.StartsWith("tv-", StringComparison.Ordinal) => "shows",
                        _ when root.Length > 7 && root.StartsWith("movies-", StringComparison.Ordinal) => "movies",
                        _ => null,
                    };
                    if (folderCategory != category) continue;
                    var title = segments[index + 1];
                    if (category == "shows" && index + 2 >= segments.Length)
                        continue; // A bare file under TV is not a known show.
                    if (category == "movies" && index + 2 >= segments.Length)
                        title = Path.GetFileNameWithoutExtension(title);
                    if (!string.IsNullOrWhiteSpace(title))
                        return new GroupIdentity(category, $"{category}/{title}", title);
                }
            }
        }
        var plexTitle = category == "shows" ? plex.ShowName : plex.Title;
        var fallback = string.IsNullOrWhiteSpace(plexTitle) ? item.DisplayName : plexTitle;
        if (category == "movies" && plex.Year is { } year && !fallback.Contains(year.ToString(), StringComparison.Ordinal))
            fallback = $"{fallback} ({year})";
        return new GroupIdentity(category, $"{category}/{fallback}", fallback);
    }

    private sealed record GroupIdentity(string Category, string Key, string Title);
    private sealed record ClassifiedItem(LibraryCatalogItemDto Item, GroupIdentity Identity,
        PlexLibraryMedia? PlexMatch, int? CachePercentage);
    private sealed record Group(string Key, string Title, string Category,
        IReadOnlyList<ClassifiedItem> Items);
}
