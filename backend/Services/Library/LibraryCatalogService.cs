using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.MediaLibrary;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Services.Library;

/// <summary>
/// Read-only catalog: one row per <c>/content</c> Usenet file (deduplicated by
/// <c>DavItem.Id</c>, size = the DavItem file size) plus one row per external
/// symlink link path. Search spans item name/path and mapping link/target.
/// </summary>
public sealed class LibraryCatalogService(DavDatabaseContext context, bool videoOnly = false)
{
    public async Task<LibraryCatalogResult> QueryAsync(
        LibraryCatalogQuery query,
        LibraryCatalogScanner? scanner = null,
        CancellationToken ct = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var search = query.Search?.Trim();
        var descending = string.Equals(query.Direction, "desc", StringComparison.OrdinalIgnoreCase);

        var dtos = await LoadAllAsync(ct, search).ConfigureAwait(false);
        dtos = dtos.Where(d => PassesTypeFilter(d, query.TypeFilter)).ToList();
        dtos = SortDtos(dtos, query.Sort, descending);
        var total = dtos.Count;
        var pageItems = dtos.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new LibraryCatalogResult(
            pageItems, total, page, pageSize,
            scanner?.LastSuccessfulScanAt, scanner?.LastScanWarning);
    }

    internal async Task<List<LibraryCatalogItemDto>> LoadAllAsync(
        CancellationToken ct, string? search = null)
    {
        var maps = await context.LinkMaps.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        if (videoOnly)
            maps = maps.Where(map => MediaLibraryVideoFilter.IsVideoLink(map.LinkPath, map.TargetText)).ToList();

        var items = context.Items.AsNoTracking()
            .Where(i => i.Type == DavItem.ItemType.UsenetFile
                && i.Path.StartsWith("/content/"));
        if (!string.IsNullOrEmpty(search))
        {
            var matchedIds = context.LinkMaps.AsNoTracking()
                .Where(m => m.DavItemId != null
                    && (m.LinkPath.Contains(search) || m.TargetText.Contains(search)))
                .Select(m => m.DavItemId!.Value);
            items = items.Where(i =>
                i.Name.Contains(search) || i.Path.Contains(search) || matchedIds.Contains(i.Id));
        }
        var internalRows = await items
            .Select(i => new { Item = i })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (videoOnly)
            internalRows = internalRows.Where(row => FilenameUtil.IsVideoFile(row.Item.Name)
                && !row.Item.Name.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)).ToList();

        var internalIds = internalRows.Select(r => r.Item.Id).ToHashSet();
        var mappingsByItem = maps
            .Where(m => m.DavItemId != null && internalIds.Contains(m.DavItemId.Value))
            .GroupBy(m => m.DavItemId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<LibraryLinkMap>)g.ToList());

        var dtos = new List<LibraryCatalogItemDto>();
        foreach (var row in internalRows)
        {
            mappingsByItem.TryGetValue(row.Item.Id, out var mappings);
            mappings ??= [];
            var dto = ToInternalDto(row.Item, mappings);
            dtos.Add(dto);
        }

        var externalMaps = maps.Where(m => m.DavItemId == null
            && (string.IsNullOrEmpty(search)
                || m.LinkPath.Contains(search, StringComparison.OrdinalIgnoreCase)
                || m.TargetText.Contains(search, StringComparison.OrdinalIgnoreCase)));
        foreach (var group in externalMaps.GroupBy(m => m.LinkPath, StringComparer.Ordinal))
        {
            var dto = ToExternalDto(group.ToList());
            dtos.Add(dto);
        }
        return videoOnly && !string.IsNullOrEmpty(search)
            ? dtos.Where(dto => MatchesSearch(dto, search)).ToList() : dtos;
    }

    internal Task<List<DavItem>> LoadItemsByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        ids.Count == 0 ? Task.FromResult(new List<DavItem>())
            : context.Items.AsNoTracking().Where(item => ids.Contains(item.Id)).ToListAsync(ct);

    internal static bool MatchesSearch(LibraryCatalogItemDto dto, string search) =>
        dto.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
        || (dto.ContentPath?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
        || dto.Mappings.Any(m =>
            m.LinkPath.Contains(search, StringComparison.OrdinalIgnoreCase)
            || m.TargetText.Contains(search, StringComparison.OrdinalIgnoreCase));

    private static LibraryCatalogItemDto ToInternalDto(DavItem item, IReadOnlyList<LibraryLinkMap> mappings) =>
        new("internal", item.Id, item.Name, item.Path, item.FileSize, mappings.Count,
            mappings.Count == 0 ? "unmapped"
                : mappings.All(m => m.Status == LibraryLinkStatus.Valid) ? "healthy" : "attention",
            mappings.Select(m => new LibraryCatalogMappingDto(
                m.LinkPath, m.TargetText, m.MappingType.ToString().ToLowerInvariant(),
                m.Status.ToString().ToLowerInvariant())).ToList());

    private static LibraryCatalogItemDto ToExternalDto(List<LibraryLinkMap> group)
    {
        var first = group[0];
        return new("external", null, first.LinkPath, null, null, group.Count,
            group.All(m => m.Status == LibraryLinkStatus.Valid) ? "external" : "attention",
            group.Select(m => new LibraryCatalogMappingDto(
                m.LinkPath, m.TargetText, "external",
                m.Status.ToString().ToLowerInvariant())).ToList());
    }

    private static bool PassesTypeFilter(LibraryCatalogItemDto dto, string filter) =>
        filter switch
        {
            "internal" => dto.Kind == "internal",
            "external" => dto.Kind == "external",
            "broken" => dto.Mappings.Any(m => m.Status is "broken" or "stale"),
            _ => true,
        };

    private static List<LibraryCatalogItemDto> SortDtos(
        List<LibraryCatalogItemDto> dtos, string sort, bool descending)
    {
        IOrderedEnumerable<LibraryCatalogItemDto> ordered = sort switch
        {
            "size" => descending
                ? dtos.OrderByDescending(d => d.Size ?? -1).ThenBy(d => d.DisplayName, StringComparer.Ordinal)
                : dtos.OrderBy(d => d.Size ?? -1).ThenBy(d => d.DisplayName, StringComparer.Ordinal),
            "mappings" => descending
                ? dtos.OrderByDescending(d => d.MappingCount).ThenBy(d => d.DisplayName, StringComparer.Ordinal)
                : dtos.OrderBy(d => d.MappingCount).ThenBy(d => d.DisplayName, StringComparer.Ordinal),
            _ => descending
                ? dtos.OrderByDescending(d => d.DisplayName, StringComparer.Ordinal)
                : dtos.OrderBy(d => d.DisplayName, StringComparer.Ordinal),
        };
        return ordered.ToList();
    }
}
