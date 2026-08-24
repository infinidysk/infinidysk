using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue.PostProcessors;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Services;

public class HistoryCleanupService(
    IDbContextFactory<DavDatabaseContext> dbContextFactory,
    ConfigManager configManager) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var dbContext = dbContextFactory.CreateDbContext();

                // If no items in queue, wait 10 seconds before checking again
                if (!await ProcessNextItemAsync(dbContext, configManager, stoppingToken).ConfigureAwait(false))
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                    continue;
                }
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

    internal static async Task<bool> ProcessNextItemAsync(
        DavDatabaseContext dbContext,
        ConfigManager configManager,
        CancellationToken cancellationToken = default)
    {
        var cleanupItem = await dbContext.HistoryCleanupItems
            .OrderBy(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (cleanupItem == null)
            return false;

        if (cleanupItem.DeleteMountedFiles)
        {
            // Collect items to delete for vfs/forget and STRM ownership checks.
            var deletedItems = await dbContext.Items
                .Where(x => x.HistoryItemId == cleanupItem.Id)
                .Select(x => new DavItem { Id = x.Id, Name = x.Name, Type = x.Type, Path = x.Path })
                .ToListAsync(cancellationToken).ConfigureAwait(false);

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
                CreateSymlinkFilesPostProcessor.DeleteSymlinkFile(configManager, deletedItem);
                CreateStrmFilesPostProcessor.DeleteStrmFile(configManager, deletedItem);
            }

            // Delete the corresponding dav-items.
            await dbContext.Items
                .Where(x => x.HistoryItemId == cleanupItem.Id)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

            // Trigger rclone vfs/forget for deleted items.
            _ = DavDatabaseContext.RcloneVfsForget(deletedItems, cancellationToken);
        }
        else
        {
            // Mark the corresponding dav-items as no longer in History.
            await dbContext.Items
                .Where(x => x.HistoryItemId == cleanupItem.Id)
                .ExecuteUpdateAsync(
                    x => x.SetProperty(p => p.HistoryItemId, (Guid?)null),
                    cancellationToken
                ).ConfigureAwait(false);
        }

        // Remove the cleanup item from the database.
        dbContext.HistoryCleanupItems.Remove(cleanupItem);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
