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
        Guid? previousDavItemId = null;

        await foreach (var result in context.HealthCheckResults
            .AsNoTracking()
            .OrderBy(x => x.DavItemId)
            .ThenByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Select(x => new { x.DavItemId, x.RepairStatus })
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            if (result.DavItemId == previousDavItemId) continue;
            previousDavItemId = result.DavItemId;
            if (result.RepairStatus == HealthCheckResult.RepairAction.ActionNeeded)
                actionNeededIds.Add(result.DavItemId);
        }

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
                .Where(x => context.HealthCheckResults
                    .Where(result => result.DavItemId == x.Id)
                    .OrderByDescending(result => result.CreatedAt)
                    .ThenByDescending(result => result.Id)
                    .Select(result => (HealthCheckResult.RepairAction?)result.RepairStatus)
                    .FirstOrDefault() == HealthCheckResult.RepairAction.ActionNeeded)
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
