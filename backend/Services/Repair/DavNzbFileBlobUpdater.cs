using System.Collections.Concurrent;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using Serilog;

namespace NzbWebDAV.Services.Repair;

/// <summary>
/// Writes a new NZB-file blob and swaps <see cref="DavItem.FileBlobId"/> under a
/// per-item lock so health-check replace mutations and streaming-corruption union
/// mutations cannot clobber each other's fields.
/// </summary>
/// <remarks>
/// Residual race: <see cref="HealthCheckService"/> commits <c>SaveChangesAsync</c>
/// outside this lock (transactionally with the health row). A corruption persist
/// that lands between a health check's in-lock swap and its out-of-lock save can
/// be overwritten at the <c>FileBlobId</c> column. Both writers are single
/// background loops, the window is small, and a lost corrupt record is recreated
/// on the next corrupt read. Do not move the health SaveChanges into this lock —
/// that would break health-row atomicity.
/// <para>
/// Item locks are striped (fixed SemaphoreSlim count) so a long-running server
/// cannot retain one semaphore per mutated file. <see cref="LatestBlobIds"/> still
/// maps item id → uncommitted blob id: the two writer loops can mutate different
/// in-memory <see cref="DavItem"/> instances of the same row before SaveChanges,
/// and the latest blob id is the only way the second writer sees the first swap.
/// Entries are one Guid per item that has ever been mutated — far cheaper than a
/// leaked SemaphoreSlim — and must outlive the lock or sequential unions on stale
/// instances would drop fields.
/// </para>
/// </remarks>
internal static class DavNzbFileBlobUpdater
{
    private const int StripeCount = 32;
    private static readonly SemaphoreSlim[] Stripes =
    [
        new(1, 1), new(1, 1), new(1, 1), new(1, 1),
        new(1, 1), new(1, 1), new(1, 1), new(1, 1),
        new(1, 1), new(1, 1), new(1, 1), new(1, 1),
        new(1, 1), new(1, 1), new(1, 1), new(1, 1),
        new(1, 1), new(1, 1), new(1, 1), new(1, 1),
        new(1, 1), new(1, 1), new(1, 1), new(1, 1),
        new(1, 1), new(1, 1), new(1, 1), new(1, 1),
        new(1, 1), new(1, 1), new(1, 1), new(1, 1),
    ];
    private static readonly ConcurrentDictionary<Guid, Guid> LatestBlobIds = new();

    private static SemaphoreSlim StripeFor(Guid itemId)
    {
        var hash = (uint)itemId.GetHashCode();
        return Stripes[hash % StripeCount];
    }

    public static async Task MutateAsync(
        DavItem davItem,
        Func<DavNzbFile, DavNzbFile> mutate,
        DavNzbFile? fallback = null)
    {
        var gate = StripeFor(davItem.Id);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var blobId = LatestBlobIds.TryGetValue(davItem.Id, out var latest)
                ? latest
                : davItem.FileBlobId;
            DavNzbFile? current = null;
            if (blobId is { } id)
            {
                try
                {
                    current = await BlobStore.ReadBlob<DavNzbFile>(id).ConfigureAwait(false);
                }
                catch (CorruptedBlobPayloadException e)
                {
                    // The current blob is unreadable; the caller's fallback (the
                    // last known-good in-memory copy) lets this mutation still
                    // succeed and overwrite the damaged blob.
                    Log.Warning(
                        "Streaming metadata blob {BlobId} for {Path} is unreadable during a repair " +
                        "mutation; using the caller's fallback copy instead.",
                        id, davItem.Path);
                    Log.Debug(e, "Unreadable streaming metadata blob stack for {Path}", davItem.Path);
                }
            }
            current ??= fallback;
            if (current is null)
                return;

            var copy = Clone(current);
            var updated = mutate(copy);
            var newBlobId = Guid.NewGuid();
            await BlobStore.WriteBlob(newBlobId, updated).ConfigureAwait(false);
            LatestBlobIds[davItem.Id] = newBlobId;
            davItem.FileBlobId = newBlobId;
        }
        finally
        {
            gate.Release();
        }
    }

    internal static DavNzbFile Clone(DavNzbFile source) => new()
    {
        Id = source.Id,
        SegmentIds = source.SegmentIds,
        SegmentByteRanges = source.SegmentByteRanges,
        SegmentFallbackIds = source.SegmentFallbackIds,
        MissingSegmentIndices = source.MissingSegmentIndices,
        ContainerClass = source.ContainerClass,
        CriticalHeadEndExclusive = source.CriticalHeadEndExclusive,
        CorruptSegmentIndices = source.CorruptSegmentIndices,
        SegmentByteRangesTrusted = source.SegmentByteRangesTrusted,
    };
}
