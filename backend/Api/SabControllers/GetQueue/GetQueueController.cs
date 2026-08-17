using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
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
        // Snapshot every in-progress item (primary first), then apply the same
        // literal filters used for persisted queue rows.
        var allInProgress = queueManager.GetInProgressQueueItems();
        var activeIds = allInProgress.Select(x => x.QueueItem.Id).ToHashSet();
        var inProgress = allInProgress;
        if (!string.IsNullOrEmpty(request.Category))
        {
            inProgress = inProgress
                .Where(x => x.QueueItem.Category == request.Category)
                .ToList();
        }
        if (request.Search is not null)
        {
            inProgress = inProgress
                .Where(x => MatchesSearch(x.QueueItem, request.Search))
                .ToList();
        }
        if (request.Status is not null)
        {
            inProgress = request.Status == "Downloading" ? inProgress : [];
        }

        var inProgressById = inProgress.ToDictionary(x => x.QueueItem.Id);
        var ct = request.CancellationToken;

        // Apply SQL-side filters before counting or paginating so noofslots is
        // always the count for the displayed view.
        IQueryable<QueueItem> queuedQuery = dbClient.Ctx.QueueItems.AsNoTracking();
        if (request.Category is not null)
            queuedQuery = queuedQuery.Where(x => x.Category == request.Category);
        queuedQuery = SabListQuery.ApplySearch(queuedQuery, request.Search);
        if (activeIds.Count > 0)
            queuedQuery = queuedQuery.Where(x => !activeIds.Contains(x.Id));
        queuedQuery = request.Status switch
        {
            "Paused" => queuedQuery.Where(x => x.Priority == QueueItem.PriorityOption.Paused),
            "Queued" => queuedQuery.Where(x => x.Priority != QueueItem.PriorityOption.Paused),
            "Downloading" or "Unsupported" => queuedQuery.Where(_ => false),
            _ => queuedQuery,
        };

        var queuedCountTask = queuedQuery.CountAsync(ct);
        var activeItems = inProgress.Select(x => x.QueueItem).ToList();
        var activePage = activeItems
            .Skip(request.Start)
            .Take(request.Limit)
            .ToList();
        var remainingLimit = request.Limit == int.MaxValue
            ? int.MaxValue
            : Math.Max(0, request.Limit - activePage.Count);
        var queuedStart = Math.Max(0, request.Start - activeItems.Count);
        var queuedItemsTask = remainingLimit == 0
            ? Task.FromResult(Array.Empty<QueueItem>())
            : SabListQuery.ApplyQueueSort(queuedQuery, request.Sort, request.Direction)
                .Skip(queuedStart)
                .Take(remainingLimit)
                .ToArrayAsync(ct);

        var queuedCount = await queuedCountTask.ConfigureAwait(false);
        var queuedItems = await queuedItemsTask.ConfigureAwait(false);
        var totalCount = checked(activeItems.Count + queuedCount);
        var merged = activePage.Concat(queuedItems).ToList();

        // Metrics keys of every configured Usenet provider — used to show idle providers
        // alongside active ones for in-progress downloads.
        var configuredProviders = Config.GetUsenetProviderConfig().Providers;
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
                // Arr treats slot status "Paused" (priority -2) separately from the
                // queue-level paused flag set by mode=pause.
                var status = isInProgress
                    ? "Downloading"
                    : queueItem.Priority == QueueItem.PriorityOption.Paused
                        ? "Paused"
                        : "Queued";
                var eta = isInProgress ? active.Eta : null;
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
                    queueItem, index, percentage, status, eta, providerUsage, displayByMetricsKey);
            })
            .ToList();

        // return response
        var speedLimitKbps = Config.GetSabSpeedLimitKbps();
        var aggregateBytesPerSecond = inProgress.Sum(x => x.BytesPerSecond);
        var remainingBytes = inProgress.Sum(x =>
        {
            var pct = Math.Clamp(x.ProgressPercentage, 0, 100);
            return (100 - pct) / 100.0 * x.QueueItem.TotalSegmentBytes;
        });
        return new GetQueueResponse()
        {
            Queue = new GetQueueResponse.QueueObject()
            {
                Paused = Config.IsSabQueuePaused(),
                Slots = slots,
                TotalCount = totalCount,
                SpeedLimit = speedLimitKbps.ToString(),
                SpeedLimitAbs = speedLimitKbps.ToString(),
                Speed = QueueThroughput.FormatSpeed(aggregateBytesPerSecond),
                KbPerSec = QueueThroughput.FormatKbPerSec(aggregateBytesPerSecond),
                TimeLeft = QueueThroughput.FromRemaining(aggregateBytesPerSecond, remainingBytes)
                           ?? TimeSpan.Zero,
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
        var request = new GetQueueRequest(Context, Config);
        return Ok(await GetQueueAsync(request).ConfigureAwait(false));
    }

    private static bool MatchesSearch(QueueItem item, string search) =>
        item.JobName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        item.FileName.Contains(search, StringComparison.OrdinalIgnoreCase);
}
