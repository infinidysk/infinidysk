using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Background service that processes the blob cleanup queue.
/// A payload blob is only deleted once no DavItem still references it.
/// </summary>
public class BlobCleanupService(IDbContextFactory<DavDatabaseContext> dbContextFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var dbContext = dbContextFactory.CreateDbContext();

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
                    "Error processing blob cleanup queue.",
                    TimeSpan.FromSeconds(10));
                await Task.Delay(retryDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Processes the next item from the blob cleanup queue, if any.
    /// Returns <c>false</c> when the queue is empty (nothing to do).
    /// Extracted as an internal static method so lifecycle behavior is unit-testable
    /// against a SQLite-backed <see cref="DavDatabaseContext"/> without running the
    /// background service loop.
    /// </summary>
    internal static async Task<bool> ProcessNextCleanupItemAsync(DavDatabaseContext dbContext, CancellationToken ct)
    {
        // Get the first item from the queue
        var cleanupItem = await dbContext.BlobCleanupItems
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (cleanupItem == null) return false;

        var blobId = cleanupItem.Id;

        // Use a serializable (BEGIN IMMEDIATE) transaction so the reference
        // check and the cleanup-item removal are atomic: a concurrent insert
        // of a new DavItem referencing this blob id must block until commit,
        // after which its own cleanup trigger can requeue if needed.
        await using var tx = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, ct)
            .ConfigureAwait(false);

        // Only delete the blob if no DavItem still references it. The delete
        // trigger queues the old FileBlobId on every removal/rekey, so a blob
        // that was later re-attached to a live item must never be dropped.
        var referencingItemPath = await dbContext.Items
            .Where(x => x.FileBlobId == blobId)
            .Select(x => x.Path)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (referencingItemPath == null)
        {
            // Delete the blob before SaveChangesAsync so a failure leaves the
            // cleanup item in the DB for a retry; BlobStore.Delete is
            // idempotent when the file is already gone.
            BlobStore.Delete(blobId);
        }
        else
        {
            Log.Debug(
                "Skipping blob cleanup for {BlobId}: still referenced by dav item at {Path}",
                blobId, referencingItemPath);
        }

        // Remove the cleanup queue item and commit.
        dbContext.BlobCleanupItems.Remove(cleanupItem);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        return true;
    }
}
