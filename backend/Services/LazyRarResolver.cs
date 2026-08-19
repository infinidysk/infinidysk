using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Utils;
using Serilog;
using SharpCompress.Common.Rar.Headers;

namespace NzbWebDAV.Services;

// Resolves PendingParts of a lazy multipart RAR archive on demand.
// First reader to need part N pays the cost (~1 segment fetch + parse);
// subsequent readers reuse the resolved FilePart. The whole resolved
// archive is written back to the blob-store so restarts also reuse it.
public class LazyRarResolver(INntpClient usenetClient, ConfigManager configManager)
{
    // Coalesces concurrent resolution requests for the same volume.
    // Keyed by the volume's first segment ID so two readers asking for the
    // same trailing part share one parse, even if they hit different
    // FileParts.Length snapshots (which the old (Guid,int) key broke).
    private readonly ConcurrentDictionary<(Guid, string), Task<DavMultipartFile.FilePart>> _inFlight = new();

    private readonly ConcurrentDictionary<Guid, Persistor> _persistors = new();

    // Test seam: when set, volume opens skip NzbFileStream/yEnc so unit tests
    // can feed a crafted RAR with an understated Length without rapidyenc.
    internal Func<string[], long, Stream>? VolumeStreamFactory { get; set; }

    // Test seam for FileSize reconciliation after a lazy archive becomes complete.
    internal Func<Guid, DavMultipartFile.Meta, CancellationToken, Task>? ReconcileFileSizeAsync
    {
        get;
        set;
    }

#pragma warning disable CA1001 // Persistor entries live for the process lifetime in _persistors (never evicted); SemaphoreSlim holds no native resources unless WaitHandle is materialized, and only WaitAsync is used
    private sealed class Persistor
    {
        public readonly SemaphoreSlim Sem = new(1, 1);
        public long LatestStamp;
    }
#pragma warning restore CA1001

    // Resolve enough trailing volumes to cover targetByteOffset and return
    // the updated Meta. All needed volumes run in parallel (capped by
    // MaxDownloadConnections) — critical for the end-of-file metadata read
    // a player issues on open, which otherwise serializes one volume at a
    // time and stalls playback for seconds.
    public async Task<DavMultipartFile.Meta> EnsureResolvedThroughAsync(
        DavMultipartFile mpf,
        long targetByteOffset,
        CancellationToken ct)
    {
        var meta = mpf.Metadata;
        if (!meta.IsLazy) return meta;

        // Old MemoryPack blobs may deserialize PendingParts as null despite
        // the property initializer; treat that as "nothing to resolve".
        var pending = meta.PendingParts ?? [];
        if (pending.Length == 0) return meta;

        var resolvedBytes = SumResolvedBytes(meta);
        if (resolvedBytes > targetByteOffset) return meta;

        // Decide how many trailing parts to resolve based on estimates. The
        // estimates are adjusted at import time so cumulative sums match the
        // true file length, which makes this count an exact upper bound.
        var count = 0;
        var runningTotal = resolvedBytes;
        foreach (var p in pending)
        {
            count++;
            runningTotal += p.EstimatedDataSize;
            if (runningTotal > targetByteOffset) break;
        }

        var partsToResolve = new DavMultipartFile.PendingPart[count];
        Array.Copy(pending, partsToResolve, count);

        // Run resolutions in parallel, bounded by the provider plan limit.
        // Use the same cap that governs the rest of the queue processor so
        // we never burst past what the user's provider plan allows.
        var maxConcurrency = Math.Max(1, configManager.GetMaxDownloadConnections());
        using var semaphore = new SemaphoreSlim(maxConcurrency);

        var resolveTasks = partsToResolve.Select(async part =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await GetOrStartResolutionAsync(mpf, part, ct).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        var resolveds = await Task.WhenAll(resolveTasks).ConfigureAwait(false);
        return CommitResolvedBatch(mpf, resolveds);
    }

    // Convenience for the sequential read path (DavMultipartFileStream
    // crossing a single volume boundary during playback). Resolves just one
    // part — enough to keep the iterator advancing.
    public virtual async Task<DavMultipartFile.Meta> ResolveNextAsync(
        DavMultipartFile mpf,
        CancellationToken ct)
    {
        var meta = mpf.Metadata;
        var pending = meta.PendingParts ?? [];
        if (!meta.IsLazy || pending.Length == 0) return meta;

        var resolved = await GetOrStartResolutionAsync(mpf, pending[0], ct).ConfigureAwait(false);
        return CommitResolvedBatch(mpf, [resolved]);
    }

    // Coalesce by the part's first segment ID. Two concurrent readers
    // asking for the same volume share one resolution regardless of where
    // it currently sits in PendingParts.
    private Task<DavMultipartFile.FilePart> GetOrStartResolutionAsync(
        DavMultipartFile mpf,
        DavMultipartFile.PendingPart pending,
        CancellationToken callerCt)
    {
        var firstSeg = pending.SegmentIds.Length > 0 ? pending.SegmentIds[0] : "";
        var key = (mpf.Id, firstSeg);

        // CancellationToken.None for the shared work so one caller bailing
        // out doesn't kill resolution for others waiting on it.
        var shared = _inFlight.GetOrAdd(key, k =>
        {
            var task = DoResolveAsync(mpf, pending, CancellationToken.None);
            // Drop the entry once done so the dictionary doesn't grow
            // unbounded; the result lives in FileParts after commit.
            _ = task.ContinueWith(t => _inFlight.TryRemove(k, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return task;
        });

        return shared.WaitAsync(callerCt);
    }

    private async Task<DavMultipartFile.FilePart> DoResolveAsync(
        DavMultipartFile mpf,
        DavMultipartFile.PendingPart pending,
        CancellationToken ct)
    {
        var meta = mpf.Metadata;
        var pathInArchive = meta.PathInArchive
            ?? throw new InvalidOperationException("Lazy RAR meta missing PathInArchive.");

        var estimatedSize = pending.SegmentIdByteRange.Count;
        try
        {
            return await ParseVolumeHeaderAsync(
                    pending, pathInArchive, meta.ArchivePassword, estimatedSize, ct)
                .ConfigureAwait(false);
        }
        catch (RarSeekPastEndException)
        {
            // Pending SegmentIdByteRange was estimated (e.g. yEnc*0.95) and
            // understated the real volume length. Measure from the last
            // segment's yEnc header and retry once with the exact size.
            var measuredSize = await MeasureVolumeSizeAsync(pending.SegmentIds, ct)
                .ConfigureAwait(false);
            if (measuredSize <= estimatedSize)
                throw;

            Log.Debug(
                "Lazy RAR volume size estimate {Estimated} was too small for multipart {Id}; " +
                "retrying header parse with measured size {Measured}.",
                estimatedSize, mpf.Id, measuredSize);
            return await ParseVolumeHeaderAsync(
                    pending, pathInArchive, meta.ArchivePassword, measuredSize, ct)
                .ConfigureAwait(false);
        }
    }

    private async Task<DavMultipartFile.FilePart> ParseVolumeHeaderAsync(
        DavMultipartFile.PendingPart pending,
        string pathInArchive,
        string? password,
        long fileSize,
        CancellationToken ct)
    {
        await using var stream = OpenVolumeStream(
            pending.SegmentIds, fileSize, pending.SegmentFallbackIds);

        // Find-and-stop after the matching header. With in-tree SharpCompress
        // deferred data-skip, this no longer seeks past packed payload on
        // NzbFileStream (which previously triggered InterpolationSearch).
        // Measure-and-retry for understated Length remains as defense in depth.
        var match = await RarUtil.FindFirstFileHeaderAsync(
            stream,
            password,
            h => h.FileName == pathInArchive,
            ct).ConfigureAwait(false)
            ?? throw new CorruptRarException(
                $"Lazy RAR resolution: continuation header for '{pathInArchive}' not found in trailing volume.");

        var dataStart = match.DataStartPosition;
        var dataSize = match.AdditionalDataSize;
        return new DavMultipartFile.FilePart
        {
            SegmentIds = pending.SegmentIds,
            SegmentIdByteRange = LongRange.FromStartAndSize(0, dataStart + dataSize),
            FilePartByteRange = LongRange.FromStartAndSize(dataStart, dataSize),
            SegmentFallbackIds = pending.SegmentFallbackIds,
        };
    }

    private Stream OpenVolumeStream(string[] segmentIds, long fileSize, string[][]? segmentFallbacks = null) =>
        VolumeStreamFactory?.Invoke(segmentIds, fileSize)
        ?? usenetClient.GetFileStream(segmentIds, fileSize, articleBufferSize: 0,
            segmentFallbacks: segmentFallbacks);

    private async Task<long> MeasureVolumeSizeAsync(string[] segmentIds, CancellationToken ct)
    {
        if (segmentIds.Length == 0) return 0;
        var headers = await usenetClient.GetYencHeadersAsync(segmentIds[^1], ct)
            .ConfigureAwait(false);
        return headers.PartOffset + headers.PartSize;
    }

    // Atomically appends consecutive resolveds that match the head of
    // PendingParts. Race-safe: another reader's concurrent commit may have
    // already moved some/all of our resolveds across, in which case we
    // skip them silently. Persists fire-and-forget — a failed write only
    // costs us a re-resolve after restart.
    private DavMultipartFile.Meta CommitResolvedBatch(DavMultipartFile mpf, DavMultipartFile.FilePart[] resolveds)
    {
        if (resolveds.Length == 0) return mpf.Metadata;

        lock (mpf)
        {
            var meta = mpf.Metadata;
            var fileParts = meta.FileParts ?? [];
            var pendingParts = meta.PendingParts ?? [];

            // Find where our batch lines up with the current pending head.
            // A concurrent commit may have already advanced past the leading
            // resolveds; skip them and start matching from wherever the
            // current pending[0] is in our batch.
            var startIdx = 0;
            while (startIdx < resolveds.Length)
            {
                if (pendingParts.Length > 0
                    && pendingParts[0].SegmentIds.SequenceEqual(resolveds[startIdx].SegmentIds))
                {
                    break;
                }
                startIdx++;
            }

            // Match consecutive resolveds against consecutive pending head.
            var matchedCount = 0;
            while (startIdx + matchedCount < resolveds.Length
                   && matchedCount < pendingParts.Length
                   && pendingParts[matchedCount].SegmentIds
                       .SequenceEqual(resolveds[startIdx + matchedCount].SegmentIds))
            {
                matchedCount++;
            }

            if (matchedCount == 0) return meta;

            var newParts = new DavMultipartFile.FilePart[fileParts.Length + matchedCount];
            Array.Copy(fileParts, newParts, fileParts.Length);
            for (var i = 0; i < matchedCount; i++)
                newParts[fileParts.Length + i] = resolveds[startIdx + i];

            var newPending = new DavMultipartFile.PendingPart[pendingParts.Length - matchedCount];
            Array.Copy(pendingParts, matchedCount, newPending, 0, newPending.Length);

            var newMeta = new DavMultipartFile.Meta
            {
                AesParams = meta.AesParams,
                FileParts = newParts,
                IsLazy = newPending.Length > 0,
                PathInArchive = meta.PathInArchive,
                ArchivePassword = meta.ArchivePassword,
                PendingParts = newPending,
            };

            var becameComplete = meta.IsLazy && newPending.Length == 0;

            mpf.Metadata = newMeta;
            _ = SchedulePersistAsync(mpf, becameComplete);
            return newMeta;
        }
    }

    private static long SumResolvedBytes(DavMultipartFile.Meta meta) =>
        MultipartFileSizeReconciler.SumResolvedBytes(meta);

    private async Task SchedulePersistAsync(DavMultipartFile mpf, bool reconcileFileSize)
    {
        var p = _persistors.GetOrAdd(mpf.Id, _ => new Persistor());
        var myStamp = Interlocked.Increment(ref p.LatestStamp);

        await p.Sem.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref p.LatestStamp) != myStamp) return;
            await BlobStore.WriteBlob(mpf.Id, mpf).ConfigureAwait(false);

            if (reconcileFileSize)
            {
                await ReconcileDavItemFileSizeAsync(mpf.Id, mpf.Metadata, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            Log.Warning(e,
                "Failed to persist lazy-resolved RAR multipart {Id}; will re-resolve on next restart",
                mpf.Id);
        }
        finally
        {
            p.Sem.Release();
        }
    }

    private async Task ReconcileDavItemFileSizeAsync(
        Guid fileBlobId,
        DavMultipartFile.Meta meta,
        CancellationToken ct)
    {
        var size = MultipartFileSizeReconciler.TryGetPublishedSize(meta);
        if (size is null) return;

        var publishedSize = size.Value;
        if (ReconcileFileSizeAsync is not null)
        {
            await ReconcileFileSizeAsync(fileBlobId, meta, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await using var ctx = new DavDatabaseContext();
            var updated = await ctx.Items
                .Where(i => i.FileBlobId == fileBlobId && i.FileSize != publishedSize)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.FileSize, publishedSize), ct)
                .ConfigureAwait(false);
            if (updated > 0)
            {
                Log.Information(
                    "Reconciled DavItem FileSize for multipart blob {BlobId} to {Size}",
                    fileBlobId, publishedSize);
            }
        }
        catch (Exception e) when (e is DbUpdateException or InvalidOperationException)
        {
            Log.Warning(
                "Failed to reconcile DavItem FileSize for multipart blob {BlobId} after resolve. Reason: {Reason}",
                fileBlobId, e.Message);
            Log.Debug(e, "Lazy RAR FileSize reconcile known failure stack");
        }
    }
}
