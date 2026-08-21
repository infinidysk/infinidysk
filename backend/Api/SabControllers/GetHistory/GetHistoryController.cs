using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;

namespace NzbWebDAV.Api.SabControllers.GetHistory;

public class GetHistoryController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    ProviderUsageTracker providerUsageTracker
) : SabApiController.BaseController(httpContext, configManager)
{
    internal async Task<GetHistoryResponse> GetHistoryAsync(GetHistoryRequest request)
    {
        // get query
        IQueryable<HistoryItem> query = dbClient.Ctx.HistoryItems;
        if (request.NzoIds.Count > 0)
            query = query.Where(q => request.NzoIds.Contains(q.Id));
        if (request.Category != null)
            query = query.Where(q => q.Category == request.Category);
        query = SabListQuery.ApplySearch(
            query, request.Search, dbClient.Ctx.Database.IsNpgsql());
        if (request.HasUnsupportedStatus)
            query = query.Where(_ => false);
        else if (request.Status is { } status)
            query = query.Where(q => q.DownloadStatus == status);

        // Get total count before querying the page: DbContext does not support
        // concurrent operations.
        var totalCount = await query
            .CountAsync(request.CancellationToken)
            .ConfigureAwait(false);

        // get history items
        var historyItems = await SabListQuery.ApplyHistorySort(
                query,
                request.Sort,
                request.Direction,
                dbClient.Ctx.Database.IsNpgsql())
            .Skip(request.Start)
            .Take(request.Limit)
            .ToArrayAsync(request.CancellationToken)
            .ConfigureAwait(false);

        // get download folders
        var downloadFolderIds = historyItems.Select(x => x.DownloadDirId).Where(x => x.HasValue).Select(x => x!.Value);
        var davItems = await dbClient.GetItemsByIdsBatchedAsync(downloadFolderIds, ct: request.CancellationToken).ConfigureAwait(false);
        var davItemsDict = davItems.ToDictionary(x => x.Id, x => x);

        // get slots (in-memory provider counts only survive until app restart)
        var providerUsages = providerUsageTracker.SnapshotMany(historyItems.Select(x => x.Id));
        var displayByMetricsKey = ProviderUsageHelper
            .BuildDisplayByMetricsKey(Config.GetUsenetProviderConfig().Providers);
        var slots = historyItems
            .Select(x =>
                GetHistoryResponse.HistorySlot.FromHistoryItem(
                    x,
                    x.DownloadDirId != null ? davItemsDict.GetValueOrDefault(x.DownloadDirId.Value) : null,
                    Config,
                    providerUsages.GetValueOrDefault(x.Id),
                    displayByMetricsKey
                )
            )
            .ToList();

        // return response
        return new GetHistoryResponse()
        {
            History = new GetHistoryResponse.HistoryObject()
            {
                Slots = slots,
                TotalCount = totalCount,
            }
        };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = new GetHistoryRequest(Context, Config);
        return Ok(await GetHistoryAsync(request).ConfigureAwait(false));
    }
}
