using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Services;

public class HistoryCleanupService(IDbContextFactory<DavDatabaseContext> dbContextFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var dbContext = dbContextFactory.CreateDbContext();

                // Get the first item from the queue
                var cleanupItem = await dbContext.HistoryCleanupItems
                    .FirstOrDefaultAsync(stoppingToken)
                    .ConfigureAwait(false);

                // If no items in queue, wait 10 seconds before checking again
                if (cleanupItem == null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (cleanupItem.DeleteMountedFiles)
                {
                    // Collect items to delete for vfs/forget
                    var deletedItems = await dbContext.Items
                        .Where(x => x.HistoryItemId == cleanupItem.Id)
                        .Select(x => new DavItem { Id = x.Id, Type = x.Type, Path = x.Path })
                        .ToListAsync(stoppingToken).ConfigureAwait(false);

                    // Loud warning for large deletes; does not block (SAB semantics unchanged).
                    DeletionAuditLog.WarnBulkDelete(
                        "history-cleanup",
                        deletedItems.Count,
                        $"DeleteMountedFiles=true historyItemId={cleanupItem.Id}");

                    foreach (var deletedItem in deletedItems)
                    {
                        DeletionAuditLog.Record(
                            "history-cleanup",
                            deletedItem,
                            $"DeleteMountedFiles=true historyItemId={cleanupItem.Id}");
                    }

                    // Delete the corresponding dav-items
                    await dbContext.Items
                        .Where(x => x.HistoryItemId == cleanupItem.Id)
                        .ExecuteDeleteAsync(stoppingToken).ConfigureAwait(false);

                    // Trigger rclone vfs/forget for deleted items
                    _ = DavDatabaseContext.RcloneVfsForget(deletedItems);
                }
                else
                {
                    // Mark the corresponding dav-items as no longer in History
                    await dbContext.Items
                        .Where(x => x.HistoryItemId == cleanupItem.Id)
                        .ExecuteUpdateAsync(
                            x => x.SetProperty(p => p.HistoryItemId, (Guid?)null),
                            stoppingToken
                        ).ConfigureAwait(false);
                }

                // Remove the cleanup item from the database
                dbContext.HistoryCleanupItems.Remove(cleanupItem);
                await dbContext.SaveChangesAsync(stoppingToken).ConfigureAwait(false);

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
                    "Error processing history cleanup queue",
                    TimeSpan.FromSeconds(10));
                await Task.Delay(retryDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
