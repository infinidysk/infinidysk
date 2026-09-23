using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services.NativeCache;

namespace NzbWebDAV.Api.Controllers.NativeCache;

[ApiController]
[Route("api/native-cache/summary")]
public sealed class NativeCacheSummaryController(NativeCacheService native, ConfigManager config) : GetOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        await native.WaitForInitializationAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        var store = native.Store;
        if (store is null) return Ok(new { activeMode = native.ActiveMode.ToString().ToLowerInvariant(),
            configuredMode = config.GetCacheMode().ToString().ToLowerInvariant(), restartRequired = native.RequiresRestart(config),
            native.InitializationPending, native.InitializationError, available = false, asOfUtc = DateTimeOffset.UtcNow });
        var ct = HttpContext.RequestAborted;
        var totals = await store.GetBrowserTotalsAsync(ct).ConfigureAwait(false);
        var statuses = await store.GetStatusAsync(ct).ConfigureAwait(false);
        var saved = await store.GetTraffic24hAsync(ct).ConfigureAwait(false);
        var traffic = saved.Add(native.Statistics.Pending24h());
        var firstTraffic = await store.GetFirstTrafficBucketAsync(ct).ConfigureAwait(false);
        var firstPending = native.Statistics.FirstPendingBucket();
        var observedBucket = firstTraffic is null ? firstPending : firstPending is null ? firstTraffic : Math.Min(firstTraffic.Value, firstPending.Value);
        var folderTotals = totals.Folders.ToDictionary(folder => folder.FolderId, StringComparer.Ordinal);
        var folders = native.ActiveSettings!.Folders.Select(folder =>
        {
            var status = statuses.FirstOrDefault(row => row.Id == folder.Id);
            var counts = folderTotals.GetValueOrDefault(folder.Id);
            return new
            {
                folder.Id, folder.Name, folder.Path, folder.StorageType, folder.Priority, folder.Enabled,
                folder.ReadOnly, folder.MaxBytes, folder.MinFreeBytes,
                online = status?.Online, writable = status?.Writable, error = status?.Error,
                allocatedBytes = status?.CommittedBytes ?? 0,
                liveFiles = counts?.LiveFiles ?? 0,
                emptyEntries = counts?.EmptyEntries ?? 0,
                verifiedBlocks = counts?.VerifiedBlocks ?? 0,
                retiredEntries = counts?.RetiredEntries ?? 0,
                retiredBytes = counts?.RetiredBytes ?? 0
            };
        }).ToArray();
        return Ok(new
        {
            available = true,
            activeMode = native.ActiveMode.ToString().ToLowerInvariant(),
            configuredMode = config.GetCacheMode().ToString().ToLowerInvariant(),
            restartRequired = native.RequiresRestart(config),
            native.InitializationPending, native.InitializationError,
            asOfUtc = DateTimeOffset.UtcNow,
            observedSinceUtc = observedBucket is null ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeSeconds(observedBucket.Value * 300),
            totalAllocatedBytes = folders.Sum(folder => folder.allocatedBytes),
            totalQuotaBytes = folders.Where(folder => folder.Enabled).Sum(folder => folder.MaxBytes),
            totals.LiveFiles, totals.EmptyEntries, totals.VerifiedBlocks, totals.RetiredEntries, totals.RetiredBytes,
            traffic = new { traffic.HitBlocks, traffic.HitBytes, traffic.MissBlocks, traffic.MissBytes,
                traffic.CommittedBytes, windowSeconds = 86400 },
            folders
        });
    }
}

[ApiController]
[Route("api/native-cache/files")]
public sealed class NativeCacheFilesController(NativeCacheService native, DavDatabaseClient database) : GetOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        await native.WaitForInitializationAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        if (native.Store is not { } store) return Ok(new { items = Array.Empty<object>(), totalCount = 0L, nextCursor = (string?)null });
        var q = HttpContext.Request.Query;
        var limit = q.ContainsKey("limit") ? int.TryParse(q["limit"], out var parsed) ? parsed : 0 : 50;
        var page = await store.BrowseFilesAsync(NullIfEmpty(q["folderId"].ToString()), NullIfEmpty(q["search"].ToString()),
            q["coverage"].ToString() is { Length: > 0 } coverage ? coverage : "all",
            q["sort"].ToString() is { Length: > 0 } sort ? sort : "recent",
            NullIfEmpty(q["cursor"].ToString()), limit, HttpContext.RequestAborted).ConfigureAwait(false);
        var names = await ResolveNamesAsync(page.Items, database, HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(new { items = page.Items.Select(row => ToFile(row, names)).ToArray(), page.TotalCount, page.NextCursor });
    }
    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    internal static object ToFile(NativeCacheBrowserFile row, IReadOnlyDictionary<string, string> names) => new
    {
        row.Key, row.FolderId, row.ItemId,
        name = !string.IsNullOrEmpty(row.DisplayName) ? row.DisplayName : names.GetValueOrDefault(row.ItemId) ?? row.ItemId,
        row.Length, row.AllocatedBytes, row.VerifiedBytes, row.Pinned,
        lastAccessedAt = DateTimeOffset.FromUnixTimeSeconds(row.AccessBucket * 300), row.AccessCount
    };

    internal static async Task<IReadOnlyDictionary<string, string>> ResolveNamesAsync(IEnumerable<NativeCacheBrowserFile> rows,
        DavDatabaseClient database, CancellationToken ct)
    {
        var ids = rows.Where(row => string.IsNullOrEmpty(row.DisplayName))
            .Select(row => Guid.TryParse(row.ItemId, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<string, string>();
        var items = await database.GetItemsByIdsBatchedAsync(ids, ct: ct).ConfigureAwait(false);
        return items.ToDictionary(item => item.Id.ToString("N"), item => item.Name);
    }
}

[ApiController]
[Route("api/native-cache/activity")]
public sealed class NativeCacheActivityController(NativeCacheService native, DavDatabaseClient database) : GetOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        await native.WaitForInitializationAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        if (native.Store is not { } store) return Ok(new { items = Array.Empty<object>() });
        var limit = HttpContext.Request.Query.ContainsKey("limit")
            ? int.TryParse(HttpContext.Request.Query["limit"], out var parsed) ? parsed : 0 : 10;
        var rows = await store.GetRecentFilesAsync(limit, HttpContext.RequestAborted).ConfigureAwait(false);
        var names = await NativeCacheFilesController.ResolveNamesAsync(rows, database, HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(new { items = rows.Select(row => NativeCacheFilesController.ToFile(row, names)).ToArray() });
    }
}

[ApiController]
[Route("api/native-cache/transfers")]
public sealed class NativeCacheTransfersController(NativeCacheService native) : GetOnlyApiController
{
    protected override Task<IActionResult> HandleRequest() => Task.FromResult<IActionResult>(Ok(new
    {
        asOfUtc = DateTimeOffset.UtcNow,
        writes = native.Statistics.ActiveTransfers()
    }));
}

[ApiController]
[Route("api/native-cache/evictions")]
public sealed class NativeCacheEvictionsController(NativeCacheService native) : GetOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        await native.WaitForInitializationAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        if (native.Store is not { } store) return Ok(new { items = Array.Empty<object>(), totalCount = 0L, nextCursor = (long?)null });
        var q = HttpContext.Request.Query;
        var limit = q.ContainsKey("limit") ? int.TryParse(q["limit"], out var parsed) ? parsed : 0 : 50;
        var cursor = q.ContainsKey("cursor") ? long.TryParse(q["cursor"], out var id) ? id : -1 : (long?)null;
        var page = await store.GetEvictionsAsync(string.IsNullOrWhiteSpace(q["search"]) ? null : q["search"].ToString(),
            string.IsNullOrWhiteSpace(q["reason"]) ? null : q["reason"].ToString(), cursor, limit,
            HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(page);
    }
}
