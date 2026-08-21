using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Services;

public class DavCleanupService(IDbContextFactory<DavDatabaseContext> dbContextFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var dbContext = dbContextFactory.CreateDbContext();

                // If no items in queue, wait 10 seconds before checking again
                if (!await ProcessNextItemAsync(dbContext, stoppingToken).ConfigureAwait(false))
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // Continue immediately to next iteration to process more items
            }
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
            {
                // OperationCanceledException is expected on sigterm
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                var retryDelay = BackgroundServiceErrorHandler.LogAndGetRetryDelay(
                    e,
                    "Error processing dav cleanup queue.",
                    TimeSpan.FromSeconds(10));
                await Task.Delay(retryDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    internal static async Task<bool> ProcessNextItemAsync(
        DavDatabaseContext dbContext,
        CancellationToken cancellationToken = default)
    {
        if (dbContext.Database.IsNpgsql())
        {
            var cleanupItemIdPg = await dbContext.DavCleanupItems
                .AsNoTracking()
                .OrderBy(x => x.Id)
                .Select(x => (Guid?)x.Id)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (cleanupItemIdPg is null)
                return false;

            var deletedItemsPg = await dbContext.Items
                .Where(x => x.ParentId == cleanupItemIdPg)
                .AsNoTracking()
                .Select(x => new DavItem { Id = x.Id, Type = x.Type, Path = x.Path })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (deletedItemsPg.Count > 0)
            {
                DeletionAuditLog.RecordBatch(
                    "dav-cleanup",
                    deletedItemsPg,
                    "cascading child sweep after parent directory delete",
                    cleanupItemIdPg);
            }

            await dbContext.Items
                .Where(x => x.ParentId == cleanupItemIdPg)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
            _ = DavDatabaseContext.RcloneVfsForget(deletedItemsPg, cancellationToken);

            await dbContext.DavCleanupItems
                .Where(x => x.Id == cleanupItemIdPg)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        // Preserve the stored text casing: materializing as Guid would normalize it to
        // uppercase when bound again and miss lowercase rows in SQLite.
        var cleanupItemId = await dbContext.Database
            .SqlQueryRaw<string>("SELECT Id AS Value FROM DavCleanupItems ORDER BY Id LIMIT 1")
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (cleanupItemId == null)
            return false;

        // Children can use either casing: migrations can preserve lowercase ParentIds,
        // while EF always writes Guid parameters as uppercase text.
        var deletedItems = await dbContext.Items
            .FromSqlRaw(
                """
                SELECT * FROM DavItems
                WHERE ParentId IN (@exactParentId, @upperParentId, @lowerParentId)
                """,
                CreateParentIdParameters(cleanupItemId))
            .AsNoTracking()
            .Select(x => new DavItem { Id = x.Id, Type = x.Type, Path = x.Path })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (deletedItems.Count > 0)
        {
            Guid? parentId = Guid.TryParse(cleanupItemId, out var parsedParentId)
                ? parsedParentId
                : null;
            DeletionAuditLog.RecordBatch(
                "dav-cleanup",
                deletedItems,
                "cascading child sweep after parent directory delete",
                parentId);
        }

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM DavItems
            WHERE ParentId IN (@exactParentId, @upperParentId, @lowerParentId)
            """,
            CreateParentIdParameters(cleanupItemId),
            cancellationToken).ConfigureAwait(false);

        _ = DavDatabaseContext.RcloneVfsForget(deletedItems, cancellationToken);

        // Delete by the exact text selected above. A concurrent or repeated delete
        // affects zero rows without raising an optimistic-concurrency exception.
        await dbContext.Database.ExecuteSqlRawAsync(
            "DELETE FROM DavCleanupItems WHERE Id = @cleanupItemId",
            [new SqliteParameter("@cleanupItemId", cleanupItemId)],
            cancellationToken).ConfigureAwait(false);

        return true;
    }

    private static SqliteParameter[] CreateParentIdParameters(string cleanupItemId) =>
    [
        new("@exactParentId", cleanupItemId),
        new("@upperParentId", cleanupItemId.ToUpperInvariant()),
        new("@lowerParentId", cleanupItemId.ToLowerInvariant()),
    ];
}
