using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Services;

public static class HealthCheckQueueMutations
{
    private const int UpdateBatchSize = 500;

    public static Task<int> MakeDueAsync(DavDatabaseContext context, CancellationToken cancellationToken) =>
        context.Items
            .Where(x => x.Type == DavItem.ItemType.UsenetFile)
            .Where(x => x.NextHealthCheck != null && x.NextHealthCheck != DateTimeOffset.UnixEpoch)
            .ExecuteUpdateAsync(
                x => x.SetProperty(item => item.NextHealthCheck, (DateTimeOffset?)null),
                cancellationToken);

    public static async Task<int> RequeueLatestActionNeededAsync(
        DavDatabaseContext context,
        CancellationToken cancellationToken)
    {
        var actionNeededIds = new List<Guid>();
        Guid? currentDavItemId = null;
        var latestCreatedAt = DateTimeOffset.MinValue;
        var latestResultsAllNeedAction = false;

        await foreach (var result in context.HealthCheckResults
            .AsNoTracking()
            .OrderBy(x => x.DavItemId)
            .ThenByDescending(x => x.CreatedAt)
            .Select(x => new { x.DavItemId, x.CreatedAt, x.RepairStatus })
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            if (result.DavItemId != currentDavItemId)
            {
                if (currentDavItemId is Guid completedId && latestResultsAllNeedAction)
                    actionNeededIds.Add(completedId);

                currentDavItemId = result.DavItemId;
                latestCreatedAt = result.CreatedAt;
                latestResultsAllNeedAction =
                    result.RepairStatus == HealthCheckResult.RepairAction.ActionNeeded;
                continue;
            }

            if (result.CreatedAt == latestCreatedAt)
            {
                latestResultsAllNeedAction &=
                    result.RepairStatus == HealthCheckResult.RepairAction.ActionNeeded;
            }
        }

        if (currentDavItemId is Guid finalId && latestResultsAllNeedAction)
            actionNeededIds.Add(finalId);

        return await RequeueActionNeededIdsAsync(
                context,
                actionNeededIds,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<int> RequeueActionNeededIdsAsync(
        DavDatabaseContext context,
        IReadOnlyCollection<Guid> actionNeededIds,
        CancellationToken cancellationToken)
    {
        var requeuedCount = 0;
        foreach (var batch in actionNeededIds.Chunk(UpdateBatchSize))
        {
            requeuedCount += await context.Items
                .Where(x => batch.Contains(x.Id))
                .Where(x => x.Type == DavItem.ItemType.UsenetFile)
                .Where(x => x.NextHealthCheck != DateTimeOffset.UnixEpoch)
                .Where(x => x.NextHealthCheck != HealthCheckService.ForcedRecheckSentinel)
                .Where(x => context.HealthCheckResults.Any(result =>
                    result.DavItemId == x.Id &&
                    result.RepairStatus == HealthCheckResult.RepairAction.ActionNeeded &&
                    !context.HealthCheckResults.Any(newer =>
                        newer.DavItemId == x.Id &&
                        newer.CreatedAt > result.CreatedAt)))
                .Where(x => !context.HealthCheckResults.Any(result =>
                    result.DavItemId == x.Id &&
                    result.RepairStatus != HealthCheckResult.RepairAction.ActionNeeded &&
                    !context.HealthCheckResults.Any(newer =>
                        newer.DavItemId == x.Id &&
                        newer.CreatedAt > result.CreatedAt)))
                .ExecuteUpdateAsync(
                    x => x.SetProperty(
                        item => item.NextHealthCheck,
                        HealthCheckService.ForcedRecheckSentinel),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return requeuedCount;
    }
}
