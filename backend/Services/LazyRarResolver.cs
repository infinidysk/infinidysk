using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
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
    private readonly ConcurrentDictionary<(Guid, string), Task<Resolution>> _inFlight = new();

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

    private sealed class Resolution
    {
        public required DavMultipartFile.PendingPart Pending { get; init; }
        public DavMultipartFile.FilePart? Part { get; init; }
        public string? FoundPath { get; init; }
        public bool IsSplitBefore { get; init; }
        public bool IsSplitAfter { get; init; }
        public bool IsSolid { get; init; }
        public bool IsEncrypted { get; init; }
        public Exception? Error { get; init; }
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
        var current = mpf.Metadata;
        if (!current.IsLazy) return current;
        if (SumResolvedBytes(current) > targetByteOffset) return current;

        var meta = await EnsureLastResolvedSplitStateAsync(mpf, ct).ConfigureAwait(false);
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

        // Start a bounded parallel window, but consume it in physical order.
        // This preserves tail-seek throughput while letting a terminal member
        // part return without waiting for unrelated trailing probes.
        var maxConcurrency = Math.Max(1, configManager.GetMaxDownloadConnections());
        var resolveds = await ResolveOrderedPrefixAsync(
                mpf, partsToResolve, maxConcurrency, ct)
            .ConfigureAwait(false);
        return CommitResolvedBatch(mpf, resolveds);
    }

    private async Task<DavMultipartFile.Meta> EnsureLastResolvedSplitStateAsync(
        DavMultipartFile mpf,
        CancellationToken ct)
    {
        var meta = mpf.Metadata;
        var pending = meta.PendingParts ?? [];
        var fileParts = meta.FileParts ?? [];
        if (!meta.IsLazy || pending.Length == 0 || fileParts.Length == 0) return meta;

        var lastPart = fileParts[^1];
        if (lastPart.IsSplitAfter is null)
        {
            var splitAfter = await ReadResolvedPartSplitStateAsync(
                    meta, lastPart, fileParts.Length - 1, ct)
                .ConfigureAwait(false);

            lock (mpf)
            {
                meta = mpf.Metadata;
                fileParts = meta.FileParts ?? [];
                if (fileParts.Length > 0
                    && fileParts[^1].SegmentIds.SequenceEqual(lastPart.SegmentIds)
                    && fileParts[^1].IsSplitAfter is null)
                {
                    fileParts[^1].IsSplitAfter = splitAfter;
                    _ = SchedulePersistAsync(mpf, reconcileFileSize: false);
                }
            }
        }

        meta = mpf.Metadata;
        fileParts = meta.FileParts ?? [];
        if (fileParts.Length == 0 || fileParts[^1].IsSplitAfter is not false) return meta;
        return CompleteAtResolvedTerminal(mpf);
    }

    private async Task<bool> ReadResolvedPartSplitStateAsync(
        DavMultipartFile.Meta meta,
        DavMultipartFile.FilePart part,
        int partIndex,
        CancellationToken ct)
    {
        var pathInArchive = meta.PathInArchive
            ?? throw new InvalidOperationException("Lazy RAR meta missing PathInArchive.");
        var fileSize = Math.Max(
            part.SegmentIdByteRange.Count,
            part.FilePartByteRange.EndExclusive);
        await using var stream = OpenVolumeStream(
            part.SegmentIds,
            fileSize,
            part.SegmentFallbackIds,
            part.SegmentByteRanges);
        var headers = await RarUtil.ReadHeadersUntilFirstFileAsync(
                stream, meta.ArchivePassword, ct)
            .ConfigureAwait(false);
        var header = headers.OfType<IRarFileHeader>().LastOrDefault(h => !h.IsDirectory);
        var expectedSplitBefore = partIndex > 0;
        if (header is null
            || !string.Equals(header.FileName, pathInArchive, StringComparison.Ordinal)
            || header.IsSplitBefore != expectedSplitBefore
            || header.IsSolid
            || header.IsEncrypted != (meta.AesParams is not null))
        {
            var found = header is null
                ? "no file header"
                : $"'{header.FileName}' (split-before: {header.IsSplitBefore}, " +
                  $"split-after: {header.IsSplitAfter}, solid: {header.IsSolid}, " +
                  $"encrypted: {header.IsEncrypted})";
            throw new CorruptRarException(
                $"Lazy RAR resolution: could not recover split state for resolved volume " +
                $"{partIndex + 1}; expected '{pathInArchive}', found {found}.");
        }

        return header.IsSplitAfter;
    }

    private DavMultipartFile.Meta CompleteAtResolvedTerminal(DavMultipartFile mpf)
    {
        lock (mpf)
        {
            var meta = mpf.Metadata;
            var fileParts = meta.FileParts ?? [];
            var pendingParts = meta.PendingParts ?? [];
            if (!meta.IsLazy
                || pendingParts.Length == 0
                || fileParts.Length == 0
                || fileParts[^1].IsSplitAfter is not false)
            {
                return meta;
            }

            ValidateTerminalSize(meta, fileParts, [], pendingParts.Length);
            var newMeta = new DavMultipartFile.Meta
            {
                AesParams = meta.AesParams,
                FileParts = fileParts,
                IsLazy = false,
                PathInArchive = meta.PathInArchive,
                ArchivePassword = meta.ArchivePassword,
                PendingParts = [],
                ExpectedFileSize = meta.ExpectedFileSize,
            };

            Log.Information(
                "Lazy RAR member {Path} was already complete; discarding {IgnoredCount} unrelated " +
                "trailing volume(s) from multipart {Id}",
                meta.PathInArchive,
                pendingParts.Length,
                mpf.Id);
            mpf.Metadata = newMeta;
            _ = SchedulePersistAsync(mpf, reconcileFileSize: true);
            return newMeta;
        }
    }

    private async Task<Resolution[]> ResolveOrderedPrefixAsync(
        DavMultipartFile mpf,
        DavMultipartFile.PendingPart[] parts,
        int maxConcurrency,
        CancellationToken ct)
    {
        var tasks = new Task<Resolution>?[parts.Length];
        var started = Math.Min(maxConcurrency, parts.Length);
        for (var i = 0; i < started; i++)
            tasks[i] = GetOrStartResolutionAsync(mpf, parts[i], ct);

        var results = new List<Resolution>(parts.Length);
        try
        {
            for (var i = 0; i < parts.Length; i++)
            {
                var outcome = await tasks[i]!.ConfigureAwait(false);
                results.Add(outcome);

                if (outcome.Error is not null
                    || outcome.Part is null
                    || outcome.IsEncrypted != (mpf.Metadata.AesParams is not null)
                    || !outcome.IsSplitAfter)
                {
                    _ = ObserveResolutionTasksAsync(
                        tasks.Skip(i + 1).Take(started - i - 1),
                        mpf.Metadata.PathInArchive);
                    break;
                }

                if (started < parts.Length)
                {
                    tasks[started] = GetOrStartResolutionAsync(mpf, parts[started], ct);
                    started++;
                }
            }
        }
        catch
        {
            _ = ObserveResolutionTasksAsync(
                tasks.Take(started).Where(task => task is not null)!,
                mpf.Metadata.PathInArchive);
            throw;
        }

        return results.ToArray();
    }

    private static async Task ObserveResolutionTasksAsync(
        IEnumerable<Task<Resolution>?> tasks,
        string? pathInArchive)
    {
        foreach (var task in tasks.Where(task => task is not null))
        {
            try
            {
                var outcome = await task!.ConfigureAwait(false);
                if (outcome.Error is not null)
                {
                    Log.Debug(
                        outcome.Error,
                        "Ignored failure from a RAR probe after ordered resolution stopped for {Path}",
                        pathInArchive);
                }
            }
            catch (OutOfMemoryException e)
            {
                Log.Fatal(
                    e,
                    "Fatal out-of-memory failure in detached RAR probe for {Path}",
                    pathInArchive);
                Environment.FailFast(
                    $"Fatal out-of-memory failure in detached RAR probe for {pathInArchive}.",
                    e);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.Debug(
                    e,
                    "Ignored RAR probe task failure after ordered resolution stopped for {Path}",
                    pathInArchive);
            }
        }
    }

    // Convenience for the sequential read path (DavMultipartFileStream
    // crossing a single volume boundary during playback). Resolves just one
    // part — enough to keep the iterator advancing.
    public virtual async Task<DavMultipartFile.Meta> ResolveNextAsync(
        DavMultipartFile mpf,
        CancellationToken ct)
    {
        var meta = await EnsureLastResolvedSplitStateAsync(mpf, ct).ConfigureAwait(false);
        var pending = meta.PendingParts ?? [];
        if (!meta.IsLazy || pending.Length == 0) return meta;

        var resolved = await GetOrStartResolutionAsync(mpf, pending[0], ct).ConfigureAwait(false);
        return CommitResolvedBatch(mpf, [resolved]);
    }

    // Coalesce by the part's first segment ID. Two concurrent readers
    // asking for the same volume share one resolution regardless of where
    // it currently sits in PendingParts.
    private Task<Resolution> GetOrStartResolutionAsync(
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
            var task = CaptureResolutionAsync(mpf, pending, CancellationToken.None);
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

    private async Task<Resolution> CaptureResolutionAsync(
        DavMultipartFile mpf,
        DavMultipartFile.PendingPart pending,
        CancellationToken ct)
    {
        try
        {
            return await DoResolveAsync(mpf, pending, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Full pre-warm resolves volumes concurrently. Preserve failures as
            // ordered outcomes so a terminal member part can make failures in
            // unrelated trailing volumes irrelevant; failures before the
            // terminal part are rethrown by CommitResolvedBatch.
            return new Resolution
            {
                Pending = pending,
                Error = e,
            };
        }
    }

    private async Task<Resolution> DoResolveAsync(
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

    private async Task<Resolution> ParseVolumeHeaderAsync(
        DavMultipartFile.PendingPart pending,
        string pathInArchive,
        string? password,
        long fileSize,
        CancellationToken ct)
    {
        await using var stream = OpenVolumeStream(
            pending.SegmentIds,
            fileSize,
            pending.SegmentFallbackIds);

        // A continuation must be the first real file header in the next
        // physical volume. Reading only that header avoids scanning unrelated
        // payload and lets split flags, rather than filename alone, define the
        // member's volume span.
        var headers = await RarUtil.ReadHeadersUntilFirstFileAsync(stream, password, ct)
            .ConfigureAwait(false);
        var match = headers.OfType<IRarFileHeader>().LastOrDefault(h => !h.IsDirectory);
        if (match is null
            || !string.Equals(match.FileName, pathInArchive, StringComparison.Ordinal)
            || !match.IsSplitBefore
            || match.IsSolid)
        {
            return new Resolution
            {
                Pending = pending,
                FoundPath = match?.FileName,
                IsSplitBefore = match?.IsSplitBefore ?? false,
                IsSplitAfter = match?.IsSplitAfter ?? false,
                IsSolid = match?.IsSolid ?? false,
                IsEncrypted = match?.IsEncrypted ?? false,
            };
        }

        var dataStart = match.DataStartPosition;
        var dataSize = match.AdditionalDataSize;
        return new Resolution
        {
            Pending = pending,
            Part = new DavMultipartFile.FilePart
            {
                SegmentIds = pending.SegmentIds,
                SegmentIdByteRange = LongRange.FromStartAndSize(0, Math.Max(fileSize, dataStart + dataSize)),
                FilePartByteRange = LongRange.FromStartAndSize(dataStart, dataSize),
                SegmentFallbackIds = pending.SegmentFallbackIds,
                IsSplitAfter = match.IsSplitAfter,
            },
            FoundPath = match.FileName,
            IsSplitBefore = match.IsSplitBefore,
            IsSplitAfter = match.IsSplitAfter,
            IsSolid = match.IsSolid,
            IsEncrypted = match.IsEncrypted,
        };
    }

    private Stream OpenVolumeStream(
        string[] segmentIds,
        long fileSize,
        string[][]? segmentFallbacks = null,
        LongRange[]? segmentByteRanges = null) =>
        VolumeStreamFactory?.Invoke(segmentIds, fileSize)
        ?? usenetClient.GetFileStream(
            segmentIds,
            fileSize,
            articleBufferSize: 0,
            segmentByteRanges: segmentByteRanges,
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
    private DavMultipartFile.Meta CommitResolvedBatch(DavMultipartFile mpf, Resolution[] resolveds)
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
                    && pendingParts[0].SegmentIds.SequenceEqual(resolveds[startIdx].Pending.SegmentIds))
                {
                    break;
                }
                startIdx++;
            }

            // Match consecutive outcomes against the pending head and stop at
            // the member's terminal split part. Outcomes after that point
            // belong to later archive members and are intentionally ignored.
            var accepted = new List<DavMultipartFile.FilePart>();
            var matchedCount = 0;
            while (startIdx + matchedCount < resolveds.Length
                   && matchedCount < pendingParts.Length
                   && pendingParts[matchedCount].SegmentIds
                       .SequenceEqual(resolveds[startIdx + matchedCount].Pending.SegmentIds))
            {
                var volume = resolveds[startIdx + matchedCount];
                if (volume.Error is not null)
                    ExceptionDispatchInfo.Capture(volume.Error).Throw();

                if (volume.Part is null)
                {
                    var volumeNumber = fileParts.Length + matchedCount + 1;
                    var totalVolumes = fileParts.Length + pendingParts.Length;
                    var found = volume.FoundPath is null
                        ? "no file header"
                        : $"'{volume.FoundPath}' (split-before: {volume.IsSplitBefore}, " +
                          $"split-after: {volume.IsSplitAfter}, solid: {volume.IsSolid}, " +
                          $"encrypted: {volume.IsEncrypted})";
                    throw new CorruptRarException(
                        $"Lazy RAR resolution: expected continuation header for '{meta.PathInArchive}' " +
                        $"in volume {volumeNumber} of {totalVolumes}; found {found}.");
                }

                if (volume.IsEncrypted != (meta.AesParams is not null))
                {
                    throw new CorruptRarException(
                        $"Lazy RAR resolution: continuation volume " +
                        $"{fileParts.Length + matchedCount + 1} for '{meta.PathInArchive}' changed " +
                        $"encryption state (expected encrypted: {meta.AesParams is not null}, " +
                        $"found: {volume.IsEncrypted}).");
                }

                accepted.Add(volume.Part);
                matchedCount++;
                if (!volume.IsSplitAfter) break;
            }

            if (matchedCount == 0) return meta;

            var terminalReached = !resolveds[startIdx + matchedCount - 1].IsSplitAfter;
            if (!terminalReached && matchedCount == pendingParts.Length)
            {
                throw new CorruptRarException(
                    $"Lazy RAR resolution: '{meta.PathInArchive}' continues beyond the final " +
                    $"available volume {fileParts.Length + matchedCount}.");
            }

            var ignoredTailCount = terminalReached
                ? pendingParts.Length - matchedCount
                : 0;
            if (terminalReached)
                ValidateTerminalSize(meta, fileParts, accepted, ignoredTailCount);

            var newParts = new DavMultipartFile.FilePart[fileParts.Length + accepted.Count];
            Array.Copy(fileParts, newParts, fileParts.Length);
            for (var i = 0; i < accepted.Count; i++)
                newParts[fileParts.Length + i] = accepted[i];

            DavMultipartFile.PendingPart[] newPending;
            if (terminalReached)
            {
                newPending = [];
                if (ignoredTailCount > 0)
                {
                    Log.Information(
                        "Lazy RAR member {Path} ended before {IgnoredCount} unrelated trailing volume(s); " +
                        "discarding them from multipart {Id}",
                        meta.PathInArchive,
                        ignoredTailCount,
                        mpf.Id);
                }

                foreach (var ignoredOutcome in resolveds
                             .Skip(startIdx + matchedCount)
                             .Where(outcome => outcome.Error is not null))
                {
                    Log.Debug(
                        ignoredOutcome.Error!,
                        "Ignored failure while probing a RAR volume after terminal member {Path}",
                        meta.PathInArchive);
                }
            }
            else
            {
                newPending = new DavMultipartFile.PendingPart[pendingParts.Length - matchedCount];
                Array.Copy(pendingParts, matchedCount, newPending, 0, newPending.Length);
            }

            var newMeta = new DavMultipartFile.Meta
            {
                AesParams = meta.AesParams,
                FileParts = newParts,
                IsLazy = newPending.Length > 0,
                PathInArchive = meta.PathInArchive,
                ArchivePassword = meta.ArchivePassword,
                PendingParts = newPending,
                ExpectedFileSize = meta.ExpectedFileSize,
            };

            var becameComplete = meta.IsLazy && newPending.Length == 0;

            mpf.Metadata = newMeta;
            _ = SchedulePersistAsync(mpf, becameComplete);
            return newMeta;
        }
    }

    private static void ValidateTerminalSize(
        DavMultipartFile.Meta meta,
        IReadOnlyList<DavMultipartFile.FilePart> existingParts,
        IReadOnlyList<DavMultipartFile.FilePart> newParts,
        int ignoredTailCount)
    {
        if (meta.ExpectedFileSize is not { } expectedFileSize)
        {
            if (ignoredTailCount == 0) return;
            throw new CorruptRarException(
                $"Lazy RAR resolution: '{meta.PathInArchive}' ended before {ignoredTailCount} " +
                "trailing volume(s), but its original file size is unavailable for safe recovery.");
        }

        long resolvedSize;
        long expectedStoredSize;
        try
        {
            resolvedSize = 0;
            foreach (var part in existingParts)
                resolvedSize = checked(resolvedSize + part.FilePartByteRange.Count);
            foreach (var part in newParts)
                resolvedSize = checked(resolvedSize + part.FilePartByteRange.Count);

            expectedStoredSize = meta.AesParams is null
                ? expectedFileSize
                : checked((expectedFileSize + 15) / 16 * 16);
        }
        catch (OverflowException)
        {
            throw new CorruptRarException(
                $"Lazy RAR resolution: size validation overflowed for '{meta.PathInArchive}'.");
        }

        const long tolerance = 16; // matches RarAggregator.ValidateVolumes
        if (Math.Abs((decimal)resolvedSize - expectedStoredSize) <= tolerance) return;

        throw new CorruptRarException(
            $"Lazy RAR resolution: terminal continuation for '{meta.PathInArchive}' resolves " +
            $"{resolvedSize} stored bytes, expected {expectedStoredSize}; refusing to complete " +
            $"the chain with {ignoredTailCount} trailing volume(s) ignored.");
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
                    "Reconciled DavItem FileSize for multipart blob {BlobId} to {Size} (changed after import)",
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
