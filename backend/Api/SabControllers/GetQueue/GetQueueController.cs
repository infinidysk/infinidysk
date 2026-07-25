using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;

namespace NzbWebDAV.Api.SabControllers.GetQueue;

public class GetQueueController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    QueueManager queueManager,
    ConfigManager configManager,
    ProviderUsageTracker providerUsageTracker
) : SabApiController.BaseController(httpContext, configManager)
{
    internal async Task<GetQueueResponse> GetQueueAsync(GetQueueRequest request)
    {
        // Snapshot every in-progress item (primary first).
        var inProgress = queueManager.GetInProgressQueueItems();
        if (!string.IsNullOrEmpty(request.Category))
        {
            inProgress = inProgress
                .Where(x => x.QueueItem.Category == request.Category)
                .ToList();
        }

        var inProgressIds = inProgress.Select(x => x.QueueItem.Id).ToHashSet();
        var inProgressById = inProgress.ToDictionary(x => x.QueueItem.Id);

        // get total count
        var ct = request.CancellationToken;
        var totalCount = await dbClient.GetQueueItemsCount(request.Category, ct).ConfigureAwait(false);

        // get queued items, then merge active items ahead for the requested page
        var getQueueItemsTask = dbClient.GetQueueItems(request.Category, 0, request.Start + request.Limit, ct);
        var queuedItems = (await getQueueItemsTask.ConfigureAwait(false))
            .Where(x => !inProgressIds.Contains(x.Id))
            .ToArray();

        var merged = inProgress
            .Select(x => x.QueueItem)
            .Concat(queuedItems)
            .Skip(request.Start)
            .Take(request.Limit)
            .ToList();

        // Metrics keys of every configured Usenet provider — used to show idle providers
        // alongside active ones for in-progress downloads.
        var configuredProviders = configManager.GetUsenetProviderConfig().Providers;
        var configuredKeys = configuredProviders
            .Where(p => p.ProviderId != Guid.Empty)
            .Select(UsenetProviderIdentity.MetricsKey)
            .Distinct()
            .ToList();
        var displayByMetricsKey = ProviderUsageHelper.BuildDisplayByMetricsKey(configuredProviders);

        // get slots
        var slots = merged
            .Select((queueItem, index) =>
            {
                var isInProgress = inProgressById.TryGetValue(queueItem.Id, out var active);
                var percentage = isInProgress ? active.ProgressPercentage : 0;
                var status = isInProgress ? "Downloading" : "Queued";
                IReadOnlyDictionary<string, long> providerUsage =
                    GetProviderUsageForSlot(isInProgress, queueItem.Id, providerUsageTracker);
                if (isInProgress && configuredKeys.Count > 0)
                {
                    var mergedUsage = new Dictionary<string, long>();
                    foreach (var key in configuredKeys) mergedUsage[key] = 0;
                    foreach (var kv in providerUsage) mergedUsage[kv.Key] = kv.Value;
                    providerUsage = mergedUsage;
                }
                return GetQueueResponse.QueueSlot.FromQueueItem(
                    queueItem, index, percentage, status, providerUsage, displayByMetricsKey);
            })
            .ToList();

        // return response
        var speedLimitKbps = configManager.GetSabSpeedLimitKbps();
        return new GetQueueResponse()
        {
            Queue = new GetQueueResponse.QueueObject()
            {
                Paused = configManager.IsSabQueuePaused(),
                Slots = slots,
                TotalCount = totalCount,
                SpeedLimit = speedLimitKbps.ToString(),
                SpeedLimitAbs = speedLimitKbps.ToString(),
            }
        };
    }

    /// <summary>
    /// Queued slots do not display live provider metrics; only snapshot the
    /// in-progress items to keep large queues responsive.
    /// </summary>
    internal static IReadOnlyDictionary<string, long> GetProviderUsageForSlot(
        bool isInProgress,
        Guid queueItemId,
        ProviderUsageTracker providerUsageTracker)
    {
        return isInProgress
            ? providerUsageTracker.Snapshot(queueItemId)
            : new Dictionary<string, long>();
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = new GetQueueRequest(httpContext, configManager);
        return Ok(await GetQueueAsync(request).ConfigureAwait(false));
    }
}
