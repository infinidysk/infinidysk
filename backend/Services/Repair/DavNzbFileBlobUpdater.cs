using System.Collections.Concurrent;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

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
/// </remarks>
internal static class DavNzbFileBlobUpdater
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();
    private static readonly ConcurrentDictionary<Guid, Guid> LatestBlobIds = new();

    public static async Task MutateAsync(
        DavItem davItem,
        Func<DavNzbFile, DavNzbFile> mutate,
        DavNzbFile? fallback = null)
    {
        var gate = Locks.GetOrAdd(davItem.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var blobId = LatestBlobIds.TryGetValue(davItem.Id, out var latest)
                ? latest
                : davItem.FileBlobId;
            DavNzbFile? current = null;
            if (blobId is { } id)
                current = await BlobStore.ReadBlob<DavNzbFile>(id).ConfigureAwait(false);
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
    };
}
