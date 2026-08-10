using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Background service that processes the NZB blob cleanup queue.
/// An NZB blob is only deleted once it is no longer referenced by any
/// QueueItem, HistoryItem, or DavItem.
/// </summary>
public class NzbBlobCleanupService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var dbContext = new DavDatabaseContext();

                var processed = await ProcessNextCleanupItemAsync(dbContext, stoppingToken).ConfigureAwait(false);

                // If no items in queue, wait 10 seconds before checking again
                if (!processed)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                }

                // Otherwise continue immediately to process more items
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
                    "Error processing NZB blob cleanup queue.",
                    TimeSpan.FromSeconds(10));
                await Task.Delay(retryDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Processes the next item from the NZB blob cleanup queue, if any.
    /// Returns <c>false</c> when the queue is empty (nothing to do).
    /// Extracted as an internal static method so lifecycle behavior is unit-testable
    /// against a SQLite-backed <see cref="DavDatabaseContext"/> without running the
    /// background service loop.
    /// </summary>
    internal static async Task<bool> ProcessNextCleanupItemAsync(DavDatabaseContext dbContext, CancellationToken ct)
    {
        // Get the first item from the queue
        var cleanupItem = await dbContext.NzbBlobCleanupItems
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (cleanupItem == null) return false;

        var blobId = cleanupItem.Id;

        // Use a serializable (BEGIN IMMEDIATE) transaction so that the three
        // reference checks and the removal of the cleanup item are atomic.
        // Without this, a concurrent HistoryItem/DavItem deletion could:
        //   1. occur between our reference checks (making one check stale), and
        //   2. have its trigger INSERT OR IGNORE suppressed because our cleanup
        //      item is still in the table, permanently orphaning the blob.
        // With BEGIN IMMEDIATE, concurrent writers are blocked until we commit.
        // After commit, the cleanup item is gone, so any trigger that fires
        // will successfully insert a new item for the next service pass.
        await using var tx = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, ct)
            .ConfigureAwait(false);

        // Only delete the blob if it is no longer referenced anywhere.
        // QueueItem.Id IS the NZB blob ID (the blob is stored at that GUID).
        var isReferencedByQueue = await dbContext.QueueItems
            .AnyAsync(x => x.Id == blobId, ct)
            .ConfigureAwait(false);

        var isReferencedByHistory = await dbContext.HistoryItems
            .AnyAsync(x => x.NzbBlobId == blobId, ct)
            .ConfigureAwait(false);

        // Fetch (rather than just check) so a skip can be logged with the
        // referencing item's path — this turns future "leaked blob" reports
        // into a one-grep diagnosis.
        var referencingDavItemPath = await dbContext.Items
            .Where(x => x.NzbBlobId == blobId)
            .Select(x => x.Path)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (!isReferencedByQueue && !isReferencedByHistory && referencingDavItemPath == null)
        {
            // Delete the blob before SaveChangesAsync so that if SaveChangesAsync
            // fails, the cleanup item remains in the DB and the service retries.
            // On retry, BlobStore.Delete succeeds even if the file is already gone.
            BlobStore.Delete(blobId);

            // The NzbName row (original filename, used to serve the NZB at
            // download time) must not outlive its blob — otherwise it becomes
            // an orphaned row that nothing ever prunes.
            var nzbName = await dbContext.NzbNames.FindAsync([blobId], ct).ConfigureAwait(false);
            if (nzbName != null)
                dbContext.NzbNames.Remove(nzbName);
        }
        else if (referencingDavItemPath != null)
        {
            Log.Debug(
                "Skipping nzb blob cleanup for {BlobId}: still referenced by dav item at {Path}",
                blobId, referencingDavItemPath);
        }

        // Remove the cleanup queue item and commit.
        dbContext.NzbBlobCleanupItems.Remove(cleanupItem);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        return true;
    }
}
