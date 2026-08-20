using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Par2Recovery.Packets;
using NzbWebDAV.Par2Recovery.ReedSolomon;
using NzbWebDAV.Services.Observability;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Services.Repair;

public class Par2RepairService : BackgroundService
{
    private const int MaxQueueLength = 50;
    private const int MaxAttempts = 3;

    private readonly ConfigManager _configManager;
    private readonly UsenetStreamingClient _usenetClient;
    private readonly RepairPatchStore _patchStore;
    private readonly Channel<RepairWorkItem> _queue;
    private readonly Channel<ZeroFillEvent> _zeroFillQueue;
    private readonly ConcurrentDictionary<Guid, byte> _queuedOrRunning = new();
    private readonly ConcurrentDictionary<string, byte> _pendingZeroFillPaths = new(StringComparer.Ordinal);
    private long _totalSucceeded;
    private long _totalFailed;
    private long _totalInfeasible;
    private long _totalBytesRead;
    private long _totalSlicesReconstructed;
    private long _totalSegmentsCommitted;

    public Par2RepairService(
        ConfigManager configManager,
        UsenetStreamingClient usenetClient,
        RepairPatchStore patchStore)
    {
        _configManager = configManager;
        _usenetClient = usenetClient;
        _patchStore = patchStore;
        // Wait mode makes non-blocking TryWrite report full queues as false so
        // callers can undo bookkeeping; DropWrite would return true and silently
        // discard the item, leaking _queuedOrRunning/_pendingZeroFillPaths entries.
        _queue = Channel.CreateBounded<RepairWorkItem>(new BoundedChannelOptions(MaxQueueLength)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _zeroFillQueue = Channel.CreateBounded<ZeroFillEvent>(new BoundedChannelOptions(MaxQueueLength)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    internal int PendingZeroFillCount => _pendingZeroFillPaths.Count;

    /// <summary>
    /// Synchronous, allocation-light entry point for streaming zero-fill events.
    /// Runs on the playback hot path's failure branch: gate on config, dedup by
    /// path, and hand off to the single background consumer. All DB work happens
    /// in the consumer.
    /// </summary>
    public void ReportZeroFill(string path, string segmentId)
    {
        if (!_configManager.IsPar2RepairEnabled()) return;
        if (!_pendingZeroFillPaths.TryAdd(path, 0)) return;
        if (!_zeroFillQueue.Writer.TryWrite(new ZeroFillEvent(path, segmentId)))
            _pendingZeroFillPaths.TryRemove(path, out _);
    }

    public void ReportCorruption(string path, string segmentId)
    {
        if (!_configManager.IsCorruptionTrackingEnabled()) return;
        if (!_pendingZeroFillPaths.TryAdd(path, 0)) return;
        if (!_zeroFillQueue.Writer.TryWrite(new ZeroFillEvent(path, segmentId, IsCorruption: true)))
            _pendingZeroFillPaths.TryRemove(path, out _);
    }

    public virtual async Task EnqueueAsync(
        DavItem davItem,
        IReadOnlyList<string> missingSegmentIds,
        CancellationToken ct = default)
    {
        if (!_configManager.IsPar2RepairEnabled()) return;
        if (missingSegmentIds.Count == 0) return;
        if (!await ShouldEnqueueAsync(davItem.Id, ct).ConfigureAwait(false)) return;

        if (!_queuedOrRunning.TryAdd(davItem.Id, 0))
            return;

        var item = new RepairWorkItem(davItem.Id, davItem.Path, missingSegmentIds.Distinct().ToArray());
        if (!_queue.Writer.TryWrite(item))
        {
            _queuedOrRunning.TryRemove(davItem.Id, out _);
            Log.Warning(
                "PAR2 repair queue full ({Capacity}); dropping repair request for {Path}",
                MaxQueueLength, davItem.Path);
            PrometheusMetrics.Current?.RecordPar2RepairJob("dropped");
            return;
        }

        PrometheusMetrics.Current?.RecordPar2RepairJob("queued");
    }

    /// <summary>
    /// Attempts PAR2 repair synchronously for health-check and urgent paths.
    /// Returns true when reconstruction succeeded and patches were committed.
    /// Virtual so health-check classification tests can script the outcome.
    /// </summary>
    public virtual Task<bool> TryPar2RepairAsync(
        DavItem davItem,
        IReadOnlyList<string>? missingSegmentIds,
        CancellationToken ct)
        => RunRepairAsync(davItem, missingSegmentIds, queueGuard: false, ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _patchStore.CatalogLoadTask.ConfigureAwait(false);

        await Task.WhenAll(
            ProcessRepairQueueAsync(stoppingToken),
            ProcessZeroFillQueueAsync(stoppingToken)).ConfigureAwait(false);
    }

    private async Task ProcessRepairQueueAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ProcessQueueItemAsync(item, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _queuedOrRunning.TryRemove(item.DavItemId, out _);
                e.LogWarningKnownOrStack("PAR2 background repair worker failed for {Path}", item.Path);
            }
        }
    }

    private async Task ProcessZeroFillQueueAsync(CancellationToken stoppingToken)
    {
        await foreach (var evt in _zeroFillQueue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ProcessZeroFillEventAsync(evt, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.Debug(e, "PAR2 zero-fill trigger failed for {Path}", evt.Path);
            }
            finally
            {
                _pendingZeroFillPaths.TryRemove(evt.Path, out _);
            }
        }
    }

    private async Task ProcessZeroFillEventAsync(ZeroFillEvent evt, CancellationToken ct)
    {
        await using var dbContext = new DavDatabaseContext();
        var dbClient = new DavDatabaseClient(dbContext);
        if (evt.IsCorruption)
        {
            await ProcessCorruptionEventAsync(dbContext, dbClient, evt, ct).ConfigureAwait(false);
            return;
        }

        var davItem = await dbClient.GetItemByPathAsync(evt.Path, ct).ConfigureAwait(false);
        if (davItem != null)
            await EnqueueAsync(davItem, [evt.SegmentId], ct).ConfigureAwait(false);
    }

    internal Task ProcessCorruptionEventForTestsAsync(string path, string segmentId, CancellationToken ct) =>
        ProcessZeroFillEventAsync(new ZeroFillEvent(path, segmentId, IsCorruption: true), ct);

    private async Task ProcessCorruptionEventAsync(
        DavDatabaseContext dbContext,
        DavDatabaseClient dbClient,
        ZeroFillEvent evt,
        CancellationToken ct)
    {
        if (!_configManager.IsCorruptionTrackingEnabled()) return;

        var davItem = await dbContext.Items
            .FirstOrDefaultAsync(x => x.Path == evt.Path, ct)
            .ConfigureAwait(false);
        if (davItem is null) return;

        var nzbFile = await dbClient.GetDavNzbFileAsync(davItem, ct).ConfigureAwait(false);
        if (nzbFile is null) return;

        var index = Array.IndexOf(nzbFile.SegmentIds, evt.SegmentId);
        if (index < 0) return;

        await DavNzbFileBlobUpdater.MutateAsync(
            davItem,
            current =>
            {
                var existing = current.CorruptSegmentIndices ?? [];
                if (existing.Contains(index))
                    return current;
                current.CorruptSegmentIndices = existing
                    .Append(index)
                    .Distinct()
                    .OrderBy(i => i)
                    .ToArray();
                return current;
            },
            fallback: nzbFile).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        if (_configManager.IsPar2RepairEnabled())
            await EnqueueAsync(davItem, [evt.SegmentId], ct).ConfigureAwait(false);
    }

    private async Task ProcessQueueItemAsync(RepairWorkItem item, CancellationToken ct)
    {
        await using var dbContext = new DavDatabaseContext();
        var dbClient = new DavDatabaseClient(dbContext);
        var davItem = await dbClient.Ctx.Items
            .FirstOrDefaultAsync(x => x.Id == item.DavItemId, ct)
            .ConfigureAwait(false);
        if (davItem == null)
        {
            _queuedOrRunning.TryRemove(item.DavItemId, out _);
            return;
        }

        await RunRepairAsync(davItem, item.MissingSegmentIds, queueGuard: true, ct)
            .ConfigureAwait(false);
    }

    private async Task<bool> RunRepairAsync(
        DavItem davItem,
        IReadOnlyList<string>? missingSegmentIds,
        bool queueGuard,
        CancellationToken ct)
    {
        if (!_configManager.IsPar2RepairEnabled())
        {
            if (queueGuard) _queuedOrRunning.TryRemove(davItem.Id, out _);
            return false;
        }

        Par2RepairJob? job = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            job = await CreateOrResumeJobAsync(davItem, missingSegmentIds, ct).ConfigureAwait(false);
            if (job == null)
            {
                if (queueGuard) _queuedOrRunning.TryRemove(davItem.Id, out _);
                return false;
            }

            job.State = Par2RepairJob.RepairJobState.Running;
            job.StartedAt = DateTimeOffset.UtcNow;
            await PersistJobAsync(job, ct).ConfigureAwait(false);
            PrometheusMetrics.Current?.RecordPar2RepairJob("running");

            // MaintenanceDownloadContext is attribution-only; it does NOT set AttributionContext,
            // so recovery-volume BODY fetches MAY populate the playback segment cache (harmless).
            using var maintenanceScope = ct.SetContext(MaintenanceDownloadContext.Instance);

            var result = await ExecuteRepairJobAsync(davItem, job, ct).ConfigureAwait(false);
            stopwatch.Stop();

            if (result.Success)
            {
                job.State = Par2RepairJob.RepairJobState.Succeeded;
                job.CompletedAt = DateTimeOffset.UtcNow;
                job.BytesRead = result.BytesRead;
                job.SlicesReconstructed = result.SlicesReconstructed;
                job.FailureReason = null;
                await PersistJobAsync(job, ct).ConfigureAwait(false);
                PrometheusMetrics.Current?.RecordPar2RepairJob("succeeded");
                PrometheusMetrics.Current?.ObservePar2RepairDuration(stopwatch.Elapsed);
                PrometheusMetrics.Current?.SetPar2PatchStoreBytes(_patchStore.CurrentBytes);
                Interlocked.Increment(ref _totalSucceeded);
                Interlocked.Add(ref _totalBytesRead, result.BytesRead);
                Interlocked.Add(ref _totalSlicesReconstructed, result.SlicesReconstructed);
                Interlocked.Add(ref _totalSegmentsCommitted, result.SegmentsCommitted);
                Log.Information(
                    "PAR2 repair succeeded for {Path}: {Slices} slice(s) reconstructed, "
                    + "{Segments} segment(s) committed, {Bytes} bytes read in {Elapsed}",
                    davItem.Path, result.SlicesReconstructed, result.SegmentsCommitted,
                    result.BytesRead, stopwatch.Elapsed);
                return true;
            }

            job.State = result.IsInfeasible
                ? Par2RepairJob.RepairJobState.Infeasible
                : Par2RepairJob.RepairJobState.Failed;
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.FailureReason = result.FailureReason;
            job.NextAttemptAt = DateTimeOffset.UtcNow +
                                TimeSpan.FromHours(_configManager.GetPar2FailureCooldownHours());
            await PersistJobAsync(job, ct).ConfigureAwait(false);
            PrometheusMetrics.Current?.RecordPar2RepairJob(result.IsInfeasible ? "infeasible" : "failed");
            PrometheusMetrics.Current?.ObservePar2RepairDuration(stopwatch.Elapsed);
            if (result.IsInfeasible) Interlocked.Increment(ref _totalInfeasible);
            else Interlocked.Increment(ref _totalFailed);
            Log.Warning(
                "PAR2 repair {Outcome} for {Path}. Reason: {Reason}",
                result.IsInfeasible ? "infeasible" : "failed", davItem.Path, result.FailureReason);
            return false;
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            stopwatch.Stop();
            if (job != null)
            {
                job.State = Par2RepairJob.RepairJobState.Failed;
                job.CompletedAt = DateTimeOffset.UtcNow;
                job.Attempts++;
                e.TryGetKnownErrorMessage(out var reason);
                job.FailureReason = reason ?? e.Message;
                job.NextAttemptAt = DateTimeOffset.UtcNow +
                                    TimeSpan.FromHours(_configManager.GetPar2FailureCooldownHours());
                await PersistJobAsync(job, ct).ConfigureAwait(false);
            }

            e.LogWarningKnownOrStack("PAR2 repair error for {Path}", davItem.Path);
            PrometheusMetrics.Current?.RecordPar2RepairJob("failed");
            PrometheusMetrics.Current?.ObservePar2RepairDuration(stopwatch.Elapsed);
            return false;
        }
        finally
        {
            if (queueGuard) _queuedOrRunning.TryRemove(davItem.Id, out _);
        }
    }

    private async Task<RepairExecutionResult> ExecuteRepairJobAsync(
        DavItem davItem,
        Par2RepairJob job,
        CancellationToken ct)
    {
        if (davItem.SubType != DavItem.ItemSubType.NzbFile)
            return RepairExecutionResult.NotFeasible("PAR2 repair supports plain NZB files only.");

        if (davItem.NzbBlobId is not Guid nzbBlobId)
            return RepairExecutionResult.NotFeasible("NZB blob id is missing for this file.");

        await using var nzbStream = BlobStore.ReadBlob(nzbBlobId);
        if (nzbStream == null)
            return RepairExecutionResult.NotFeasible("NZB blob is no longer available.");

        NzbDocument nzbDocument;
        try
        {
            nzbDocument = await NzbDocument.LoadAsync(nzbStream, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return RepairExecutionResult.NotFeasible($"Could not parse NZB blob: {e.Message}");
        }

        await using var dbContext = new DavDatabaseContext();
        var dbClient = new DavDatabaseClient(dbContext);
        var nzbFile = await dbClient.GetDavNzbFileAsync(davItem, ct).ConfigureAwait(false);
        if (nzbFile?.SegmentIds is not { Length: > 0 } segmentIds)
            return RepairExecutionResult.NotFeasible("Streaming payload metadata is missing.");

        var contentNzb = FindContentNzbFile(nzbDocument, davItem.Name);
        if (contentNzb == null)
            return RepairExecutionResult.NotFeasible("Could not locate the content file in the NZB.");

        var par2Context = await DiscoverPar2SetAsync(nzbDocument, contentNzb, davItem.Name, ct)
            .ConfigureAwait(false);
        if (par2Context == null)
            return RepairExecutionResult.NotFeasible("No matching PAR2 recovery set found in the NZB.");

        var sliceSize = (int)par2Context.Main.SliceSize;
        var targetKey = Convert.ToHexString(par2Context.Main.FileIds[par2Context.TargetFileIndex]);
        var targetDesc = par2Context.FileDescsById[targetKey];
        var targetIfsc = par2Context.IfscsByFileId[targetKey];
        var fileLength = davItem.FileSize ?? (long)targetDesc.FileLength;
        var globalSliceBase = GlobalSliceOffset(
            par2Context.TargetFileIndex, par2Context.Main, par2Context.IfscsByFileId);

        LongRange[] segmentRanges;
        try
        {
            segmentRanges = BuildSegmentRanges(nzbFile, segmentIds.Length, fileLength);
        }
        catch (InvalidOperationException e)
        {
            return RepairExecutionResult.NotFeasible(e.Message);
        }

        if (!Par2FileSliceMap.TryCreate(
                fileLength,
                globalSliceBase,
                sliceSize,
                targetIfsc.Slices.Count,
                segmentRanges,
                out var sliceMap,
                out var mapError)
            || sliceMap is null)
        {
            return RepairExecutionResult.NotFeasible(mapError ?? "Could not map segments onto PAR2 slices.");
        }

        var requested = ResolveMissingSegments(job.MissingSegmentIds, segmentIds);
        var persistedMissing = ValidIndices(nzbFile.MissingSegmentIndices, segmentIds.Length);
        var persistedCorrupt = ValidIndices(nzbFile.CorruptSegmentIndices, segmentIds.Length);
        var unavailableSegments = new HashSet<int>();
        foreach (var item in requested)
            unavailableSegments.Add(item.Index);
        unavailableSegments.UnionWith(persistedMissing);
        unavailableSegments.UnionWith(persistedCorrupt);

        if (unavailableSegments.Count == 0 && job.MissingSegmentIds.Length == 0
            && persistedMissing.Count == 0 && persistedCorrupt.Count == 0)
        {
            unavailableSegments.UnionWith(Enumerable.Range(0, segmentIds.Length));
        }

        if (unavailableSegments.Count == 0)
            return RepairExecutionResult.NotFeasible("No missing or corrupt segments to repair.");

        var unavailableSlices = new HashSet<int>();
        try
        {
            foreach (var index in unavailableSegments)
            {
                foreach (var slice in sliceMap.GlobalSlicesForSegment(index))
                    unavailableSlices.Add(slice);
            }
        }
        catch (OverflowException)
        {
            return RepairExecutionResult.NotFeasible("Segment-to-slice mapping overflowed.");
        }

        if (unavailableSlices.Count == 0)
            return RepairExecutionResult.NotFeasible("Missing or corrupt segments do not map to PAR2 slices.");

        var fetchConcurrency = _configManager.GetPar2FetchConcurrency();
        using var fetchGate = new SemaphoreSlim(fetchConcurrency, fetchConcurrency);
        var bytesRead = 0L;
        var accessor = new SliceSegmentAccessor(
            segmentIds,
            sliceMap,
            nzbDocument,
            par2Context,
            _usenetClient,
            fetchGate,
            unavailableSlices,
            onBytesRead: n => bytesRead += n);
        foreach (var index in persistedMissing)
            accessor.NoteMissing(index);
        foreach (var index in persistedCorrupt)
            accessor.NoteCorrupt(index);

        try
        {
            await DiscoverUnavailableSourcesAsync(
                    accessor, sliceMap, targetIfsc, unavailableSegments, unavailableSlices, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        var discoveredMissing = accessor.MissingSegmentIndices.Except(persistedMissing).Except(requested.Select(x => x.Index)).Count();
        var discoveredCorrupt = accessor.CorruptSegmentIndices.Except(persistedCorrupt).Except(requested.Select(x => x.Index)).Count();
        Log.Information(
            "PAR2 repair targeting {Path}: requested={Requested} persistedMissing={PersistedMissing} "
            + "persistedCorrupt={PersistedCorrupt} discoveredMissing={DiscoveredMissing} "
            + "discoveredCorrupt={DiscoveredCorrupt} slices={Slices}",
            davItem.Path,
            requested.Count,
            persistedMissing.Count,
            persistedCorrupt.Count,
            discoveredMissing,
            discoveredCorrupt,
            unavailableSlices.Count);

        var maxMissingSlices = _configManager.GetPar2MaxMissingSlices();
        if (unavailableSlices.Count > maxMissingSlices)
        {
            await PersistDiscoveredDamageAsync(davItem, nzbFile, accessor, ct).ConfigureAwait(false);
            return RepairExecutionResult.NotFeasible(
                $"Missing slice count {unavailableSlices.Count} exceeds cap {maxMissingSlices}.");
        }

        var patchTargets = SegmentsOverlappingSlices(sliceMap, unavailableSlices, segmentIds);
        var stagedPatchBytes = patchTargets.Sum(target => segmentRanges[target.Index].Count);
        var presentSliceCount = Math.Max(0, sliceMap.SliceCount - unavailableSlices.Count);
        long workingSetBytes;
        try
        {
            workingSetBytes = EstimateWorkingSetBytes(
                accessor.CachedBodyBytes,
                presentSliceCount,
                unavailableSlices.Count,
                unavailableSlices.Count,
                stagedPatchBytes,
                sliceSize);
        }
        catch (OverflowException)
        {
            await PersistDiscoveredDamageAsync(davItem, nzbFile, accessor, ct).ConfigureAwait(false);
            return RepairExecutionResult.NotFeasible("PAR2 working-set estimate overflowed.");
        }

        var maxMemoryBytes = _configManager.GetPar2MaxMemoryMb() * 1024L * 1024L;
        if (workingSetBytes > maxMemoryBytes)
        {
            await PersistDiscoveredDamageAsync(davItem, nzbFile, accessor, ct).ConfigureAwait(false);
            return RepairExecutionResult.NotFeasible(
                $"PAR2 working set {workingSetBytes} bytes exceeds memory cap.");
        }

        var releaseBytesCap = _configManager.GetPar2MaxReleaseGb() * 1024L * 1024L * 1024L;
        var releaseBytes = EstimateReleaseBytes(par2Context.Main, par2Context.FileDescsById);
        if (releaseBytes > releaseBytesCap)
        {
            await PersistDiscoveredDamageAsync(davItem, nzbFile, accessor, ct).ConfigureAwait(false);
            return RepairExecutionResult.NotFeasible(
                $"Recovery set size {releaseBytes} bytes exceeds release cap.");
        }

        var missingSliceIndices = unavailableSlices.OrderBy(x => x).ToList();
        var recoverySlices = await CollectRecoverySlicesAsync(
            par2Context.VolumeFiles,
            missingSliceIndices.Count,
            par2Context.Main.SliceSize,
            fetchGate,
            ct,
            onBytesRead: n => bytesRead += n).ConfigureAwait(false);
        if (recoverySlices.Count < missingSliceIndices.Count)
        {
            await PersistDiscoveredDamageAsync(davItem, nzbFile, accessor, ct).ConfigureAwait(false);
            return RepairExecutionResult.NotFeasible(
                $"Need {missingSliceIndices.Count} recovery slices but only collected {recoverySlices.Count}.");
        }

        var reconstructor = new Par2Reconstructor();
        var reconstruction = await reconstructor.ReconstructAsync(
            par2Context.Main,
            par2Context.FileDescsById,
            par2Context.IfscsByFileId,
            missingSliceIndices,
            recoverySlices,
            (sliceIndex, size, token) => accessor.FetchSliceBytesAsync(sliceIndex, size, token),
            ct).ConfigureAwait(false);

        if (!reconstruction.Success)
        {
            PrometheusMetrics.Current?.RecordPar2ValidationFailure("slice");
            await PersistDiscoveredDamageAsync(davItem, nzbFile, accessor, ct).ConfigureAwait(false);
            return RepairExecutionResult.Failed(reconstruction.FailureReason ?? "Reconstruction failed.");
        }

        if (patchTargets.Count == 0)
        {
            await PersistDiscoveredDamageAsync(davItem, nzbFile, accessor, ct).ConfigureAwait(false);
            return RepairExecutionResult.NotFeasible("No missing or corrupt segments were confirmed during PAR2 repair.");
        }

        var commits = await ExtractSegmentPatchesAsync(
            patchTargets,
            sliceMap,
            reconstruction.ReconstructedSlices,
            accessor,
            davItem.Name,
            fileLength,
            segmentIds.Length,
            ct).ConfigureAwait(false);

        if (!TryVerifyWholeFileMd5(targetDesc, sliceMap, reconstruction.ReconstructedSlices, accessor, out var md5Reason))
        {
            PrometheusMetrics.Current?.RecordPar2ValidationFailure("file");
            await PersistDiscoveredDamageAsync(davItem, nzbFile, accessor, ct).ConfigureAwait(false);
            return RepairExecutionResult.Failed(md5Reason ?? "Whole-file MD5 mismatch.");
        }

        _patchStore.CommitPatches(
            commits.Select(patch => (patch.SegmentId, patch.Bytes, patch.Header)).ToList());

        await PersistDiscoveredDamageAsync(davItem, nzbFile, accessor, ct).ConfigureAwait(false);
        PrometheusMetrics.Current?.AddPar2RepairBytesRead(bytesRead);
        PrometheusMetrics.Current?.AddPar2SlicesReconstructed(reconstruction.ReconstructedSlices.Count);
        PrometheusMetrics.Current?.AddPar2SegmentsCommitted(commits.Count);
        job.MissingSegmentIds = patchTargets.Select(x => x.SegmentId).ToArray();
        return RepairExecutionResult.Succeeded(bytesRead, reconstruction.ReconstructedSlices.Count, commits.Count);
    }

    private static async Task DiscoverUnavailableSourcesAsync(
        SliceSegmentAccessor accessor,
        Par2FileSliceMap sliceMap,
        IfscPacket targetIfsc,
        HashSet<int> unavailableSegments,
        HashSet<int> unavailableSlices,
        CancellationToken ct)
    {
        var expanded = true;
        while (expanded)
        {
            ct.ThrowIfCancellationRequested();
            expanded = false;
            AbsorbAccessorDiscoveries(accessor, sliceMap, unavailableSegments, unavailableSlices, ref expanded);

            for (var local = 0; local < sliceMap.SliceCount; local++)
            {
                ct.ThrowIfCancellationRequested();
                var globalSlice = checked(sliceMap.GlobalSliceBase + local);
                if (unavailableSlices.Contains(globalSlice))
                    continue;

                var assembled = await accessor.FetchSliceBytesAsync(globalSlice, sliceMap.SliceSize, ct)
                    .ConfigureAwait(false);
                AbsorbAccessorDiscoveries(accessor, sliceMap, unavailableSegments, unavailableSlices, ref expanded);
                if (assembled is null)
                {
                    if (unavailableSlices.Add(globalSlice))
                        expanded = true;
                    continue;
                }

                if (!Par2Reconstructor.VerifySliceChecksum(assembled, targetIfsc.Slices[local]))
                {
                    if (unavailableSlices.Add(globalSlice))
                        expanded = true;
                }
            }
        }
    }

    private static void AbsorbAccessorDiscoveries(
        SliceSegmentAccessor accessor,
        Par2FileSliceMap sliceMap,
        HashSet<int> unavailableSegments,
        HashSet<int> unavailableSlices,
        ref bool expanded)
    {
        foreach (var index in accessor.MissingSegmentIndices.Concat(accessor.CorruptSegmentIndices))
        {
            if (!unavailableSegments.Add(index))
                continue;
            foreach (var slice in sliceMap.GlobalSlicesForSegment(index))
            {
                if (unavailableSlices.Add(slice))
                    expanded = true;
            }
        }
    }

    private static List<MissingSegment> SegmentsOverlappingSlices(
        Par2FileSliceMap sliceMap,
        HashSet<int> unavailableSlices,
        string[] segmentIds)
    {
        var targets = new List<MissingSegment>();
        for (var i = 0; i < segmentIds.Length; i++)
        {
            if (sliceMap.GlobalSlicesForSegment(i).Any(unavailableSlices.Contains))
                targets.Add(new MissingSegment(segmentIds[i], i));
        }

        return targets;
    }

    internal static long EstimateWorkingSetBytes(
        long cachedSourceBodyBytes,
        int assembledPresentSliceCount,
        int recoverySliceCount,
        int reconstructedSliceCount,
        long stagedPatchBytes,
        int sliceSize)
    {
        checked
        {
            var assembled = (long)assembledPresentSliceCount * sliceSize;
            var recovery = (long)recoverySliceCount * sliceSize;
            var accumulators = (long)recoverySliceCount * sliceSize;
            var reconstructed = (long)reconstructedSliceCount * sliceSize;
            var memoryStreamDup = cachedSourceBodyBytes;
            return cachedSourceBodyBytes
                   + assembled
                   + recovery
                   + accumulators
                   + reconstructed
                   + stagedPatchBytes
                   + memoryStreamDup;
        }
    }

    private static HashSet<int> ValidIndices(int[]? indices, int segmentCount)
    {
        if (indices is not { Length: > 0 })
            return [];
        return indices.Where(index => (uint)index < (uint)segmentCount).ToHashSet();
    }

    private static async Task PersistDiscoveredDamageAsync(
        DavItem davItem,
        DavNzbFile nzbFile,
        SliceSegmentAccessor accessor,
        CancellationToken ct)
    {
        var missing = accessor.MissingSegmentIndices.OrderBy(i => i).ToArray();
        var corrupt = accessor.CorruptSegmentIndices.OrderBy(i => i).ToArray();
        if (missing.Length == 0 && corrupt.Length == 0)
            return;

        await DavNzbFileBlobUpdater.MutateAsync(
            davItem,
            current =>
            {
                if (missing.Length > 0)
                {
                    current.MissingSegmentIndices = UnionIndices(current.MissingSegmentIndices, missing);
                }

                if (corrupt.Length > 0)
                {
                    current.CorruptSegmentIndices = UnionIndices(current.CorruptSegmentIndices, corrupt);
                }

                return current;
            },
            fallback: nzbFile).ConfigureAwait(false);

        await using var dbContext = new DavDatabaseContext();
        var tracked = await dbContext.Items.FirstOrDefaultAsync(x => x.Id == davItem.Id, ct).ConfigureAwait(false);
        if (tracked is not null && davItem.FileBlobId is { } blobId)
        {
            tracked.FileBlobId = blobId;
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private static int[] UnionIndices(int[]? existing, int[] discovered)
    {
        return (existing ?? [])
            .Concat(discovered)
            .Distinct()
            .OrderBy(i => i)
            .ToArray();
    }

#pragma warning disable CA5351 // PAR 2.0 whole-file hashes use MD5 per spec
    private static bool TryVerifyWholeFileMd5(
        FileDesc desc,
        Par2FileSliceMap sliceMap,
        Dictionary<int, byte[]> reconstructedSlices,
        SliceSegmentAccessor accessor,
        out string? reason)
    {
        reason = null;
        if (desc.FileHash is not { Length: 16 })
            return true;

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        long offset = 0;
        while (offset < sliceMap.FileLength)
        {
            var globalSlice = sliceMap.GlobalSliceBase + (int)(offset / sliceMap.SliceSize);
            var sliceRange = sliceMap.SliceFileRange(globalSlice);
            byte[] sliceBytes;
            if (reconstructedSlices.TryGetValue(globalSlice, out var reconstructed))
            {
                sliceBytes = reconstructed;
            }
            else
            {
                var fetched = accessor.TryGetCachedSlice(globalSlice, sliceMap.SliceSize);
                if (fetched is null)
                {
                    reason = $"Whole-file MD5 coverage gap at slice {globalSlice}.";
                    return false;
                }

                sliceBytes = fetched;
            }

            var validLen = (int)Math.Min(sliceRange.Count, sliceMap.FileLength - offset);
            if (validLen > 0)
                hasher.AppendData(sliceBytes.AsSpan(0, validLen));
            offset += validLen;
        }

        var computed = hasher.GetHashAndReset();
        if (computed.AsSpan().SequenceEqual(desc.FileHash))
            return true;

        reason = $"Whole-file MD5 mismatch for {desc.FileName}.";
        return false;
    }
#pragma warning restore CA5351

    private static async Task<List<SegmentPatch>> ExtractSegmentPatchesAsync(
        IReadOnlyList<MissingSegment> missingSegments,
        Par2FileSliceMap sliceMap,
        Dictionary<int, byte[]> reconstructedSlices,
        SliceSegmentAccessor accessor,
        string fileName,
        long fileSize,
        int segmentCount,
        CancellationToken ct)
    {
        var patches = new List<SegmentPatch>();
        foreach (var missing in missingSegments)
        {
            var range = sliceMap.SegmentRanges[missing.Index];
            var bytes = new byte[range.Count];
            var cached = await accessor.TryGetCachedBodyAsync(missing.Index, ct).ConfigureAwait(false);
            if (cached is not null)
            {
                var copy = (int)Math.Min(cached.Length, bytes.Length);
                Buffer.BlockCopy(cached, 0, bytes, 0, copy);
            }

            var copied = 0L;
            var fileOffset = range.StartInclusive;
            while (copied < range.Count)
            {
                var globalSlice = sliceMap.GlobalSliceBase + (int)((fileOffset + copied) / sliceMap.SliceSize);
                if (reconstructedSlices.TryGetValue(globalSlice, out var slice))
                {
                    var offsetInSlice = (int)((fileOffset + copied) % sliceMap.SliceSize);
                    var toCopy = (int)Math.Min(range.Count - copied, sliceMap.SliceSize - offsetInSlice);
                    Buffer.BlockCopy(slice, offsetInSlice, bytes, (int)copied, toCopy);
                    copied += toCopy;
                    continue;
                }

                copied += Math.Min(range.Count - copied, sliceMap.SliceSize - ((fileOffset + copied) % sliceMap.SliceSize));
            }

            patches.Add(new SegmentPatch(
                missing.SegmentId,
                bytes,
                new UsenetYencHeader
                {
                    FileName = fileName,
                    FileSize = fileSize,
                    LineLength = 128,
                    PartNumber = missing.Index + 1,
                    TotalParts = segmentCount,
                    PartSize = range.Count,
                    PartOffset = range.StartInclusive,
                }));
        }

        return patches;
    }

    private static int GlobalSliceOffset(
        int targetFileIndex,
        MainPacket main,
        Dictionary<string, IfscPacket> ifscsByFileId)
    {
        var offset = 0;
        for (var i = 0; i < targetFileIndex; i++)
        {
            var key = Convert.ToHexString(main.FileIds[i]);
            offset += ifscsByFileId[key].Slices.Count;
        }

        return offset;
    }

    private static LongRange[] BuildSegmentRanges(DavNzbFile nzbFile, int segmentCount, long? fileSize)
    {
        if (nzbFile.SegmentByteRanges is { Length: var len } ranges && len == segmentCount)
            return ranges;

        if (fileSize is > 0 && segmentCount > 0)
        {
            var uniform = fileSize.Value / segmentCount;
            if (uniform > 0)
            {
                var built = new LongRange[segmentCount];
                long start = 0;
                for (var i = 0; i < segmentCount; i++)
                {
                    var size = i == segmentCount - 1 ? fileSize.Value - start : uniform;
                    built[i] = LongRange.FromStartAndSize(start, size);
                    start += size;
                }

                return built;
            }
        }

        throw new InvalidOperationException("Segment byte ranges are unavailable for PAR2 repair.");
    }

    private static List<MissingSegment> ResolveMissingSegments(string[] requestedIds, string[] segmentIds)
    {
        var indexById = segmentIds
            .Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index, StringComparer.Ordinal);

        return requestedIds
            .Where(indexById.ContainsKey)
            .Select(id => new MissingSegment(id, indexById[id]))
            .ToList();
    }

    private async Task<List<Par2Reconstructor.RecoverySlice>> CollectRecoverySlicesAsync(
        IReadOnlyList<NzbFile> volumeFiles,
        int needed,
        ulong sliceSize,
        SemaphoreSlim fetchGate,
        CancellationToken ct,
        Action<long>? onBytesRead = null)
    {
        var byExponent = new Dictionary<uint, byte[]>();
        foreach (var volume in volumeFiles)
        {
            if (byExponent.Count >= needed) break;

            await fetchGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var segments = volume.GetSegmentIds();
                var fileSize = await _usenetClient.GetFileSizeAsync(volume, ct).ConfigureAwait(false);
                onBytesRead?.Invoke(fileSize);
                await using var stream = _usenetClient.GetFileStream(segments, fileSize, articleBufferSize: 0);
                while (stream.Position < stream.Length && byExponent.Count < needed)
                {
                    ct.ThrowIfCancellationRequested();
                    Par2Packet packet;
                    try
                    {
                        packet = await Par2RepairReader.ReadVerifiedPacketAsync(stream, readRecvSlicPayload: true, ct)
                            .ConfigureAwait(false);
                    }
                    catch (InvalidDataException e)
                    {
                        PrometheusMetrics.Current?.RecordPar2ValidationFailure("packet");
                        Log.Debug(e, "Skipping invalid PAR2 packet while reading recovery volume {Subject}", volume.Subject);
                        break;
                    }

                    if (packet is RecvSlic recvSlic && recvSlic.Payload.Length == (int)sliceSize
                        && byExponent.TryAdd(recvSlic.Exponent, recvSlic.Payload)
                        && byExponent.Count >= needed)
                    {
                        break;
                    }
                }
            }
            finally
            {
                fetchGate.Release();
            }
        }

        return byExponent
            .OrderBy(x => x.Key)
            .Take(needed)
            .Select(x => new Par2Reconstructor.RecoverySlice(x.Key, x.Value))
            .ToList();
    }

    private async Task<Par2SetContext?> DiscoverPar2SetAsync(
        NzbDocument nzbDocument,
        NzbFile contentNzb,
        string davItemName,
        CancellationToken ct)
    {
        var candidates = nzbDocument.Files
            .Where(IsPar2CandidateSubject)
            .OrderBy(x => x.Segments.Count)
            .ThenBy(x => x.Segments.FirstOrDefault()?.MessageId, StringComparer.Ordinal)
            .ToList();

        foreach (var candidate in candidates.Where(x => !Par2.ParVolume.IsMatch(x.GetSubjectFileName())))
        {
            var context = await TryParsePar2IndexAsync(candidate, contentNzb, davItemName, nzbDocument, ct)
                .ConfigureAwait(false);
            if (context != null) return context;
        }

        if (candidates.Count > 0)
        {
            return await TryParsePar2IndexAsync(candidates[0], contentNzb, davItemName, nzbDocument, ct)
                .ConfigureAwait(false);
        }

        foreach (var candidate in candidates)
        {
            if (await HasPar2MagicAsync(candidate, ct).ConfigureAwait(false))
            {
                var context = await TryParsePar2IndexAsync(candidate, contentNzb, davItemName, nzbDocument, ct)
                    .ConfigureAwait(false);
                if (context != null) return context;
            }
        }

        return null;
    }

    private static bool IsPar2CandidateSubject(NzbFile file)
    {
        var name = file.GetSubjectFileName();
        return name.EndsWith(".par2", StringComparison.OrdinalIgnoreCase)
               || Par2.ParVolume.IsMatch(name);
    }

    private async Task<bool> HasPar2MagicAsync(NzbFile file, CancellationToken ct)
    {
        if (file.Segments.Count == 0) return false;
        try
        {
            var response = await _usenetClient.DecodedBodyAsync(file.Segments[0].MessageId, ct)
                .ConfigureAwait(false);
            await using var stream = response.Stream!;
            var buffer = new byte[64];
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            return read >= 8 && Par2.HasPar2MagicBytes(buffer);
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            Log.Debug(e, "PAR2 magic sniff failed for {Subject}", file.Subject);
            return false;
        }
    }

    private async Task<Par2SetContext?> TryParsePar2IndexAsync(
        NzbFile indexFile,
        NzbFile contentNzb,
        string davItemName,
        NzbDocument nzbDocument,
        CancellationToken ct)
    {
        var segments = indexFile.GetSegmentIds();
        if (segments.Length == 0) return null;

        var fileSize = await _usenetClient.GetFileSizeAsync(indexFile, ct).ConfigureAwait(false);
        await using var stream = _usenetClient.GetFileStream(segments, fileSize, articleBufferSize: 0);

        var fileDescs = new Dictionary<string, FileDesc>(StringComparer.Ordinal);
        MainPacket? main = null;
        var ifscs = new Dictionary<string, IfscPacket>(StringComparer.Ordinal);

        while (stream.Position < stream.Length)
        {
            Par2Packet packet;
            try
            {
                packet = await Par2RepairReader.ReadVerifiedPacketAsync(stream, readRecvSlicPayload: false, ct)
                    .ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                PrometheusMetrics.Current?.RecordPar2ValidationFailure("packet");
                break;
            }

            switch (packet)
            {
                case FileDesc fileDesc:
                    fileDescs[Convert.ToHexString(fileDesc.FileID)] = fileDesc;
                    break;
                case MainPacket mainPacket:
                    main = mainPacket;
                    break;
                case IfscPacket ifsc:
                    ifscs[Convert.ToHexString(ifsc.FileId)] = ifsc;
                    break;
                case RecvSlic:
                    goto done;
            }
        }

    done:
        if (main == null || fileDescs.Count == 0 || ifscs.Count == 0)
            return null;

        var targetDesc = fileDescs.Values.FirstOrDefault(desc =>
            string.Equals(desc.FileName, davItemName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(desc.FileName, contentNzb.GetSubjectFileName(), StringComparison.OrdinalIgnoreCase));
        if (targetDesc == null) return null;

        var targetKey = Convert.ToHexString(targetDesc.FileID);
        if (!ifscs.ContainsKey(targetKey)) return null;

        var targetFileIndex = -1;
        for (var i = 0; i < main.FileIds.Count; i++)
        {
            if (Convert.ToHexString(main.FileIds[i]).Equals(targetKey, StringComparison.Ordinal))
            {
                targetFileIndex = i;
                break;
            }
        }

        if (targetFileIndex < 0) return null;

        var volumeFiles = nzbDocument.Files
            .Where(x => x != indexFile)
            .Where(x => IsPar2CandidateSubject(x) || Par2.ParVolume.IsMatch(x.GetSubjectFileName()))
            .ToList();

        return new Par2SetContext(main, fileDescs, ifscs, targetFileIndex, volumeFiles);
    }

    private static NzbFile? FindContentNzbFile(NzbDocument document, string davItemName)
    {
        return document.Files.FirstOrDefault(f =>
                   string.Equals(f.GetSubjectFileName(), davItemName, StringComparison.OrdinalIgnoreCase))
               ?? document.Files.FirstOrDefault(f =>
                   f.GetSubjectFileName().EndsWith(davItemName, StringComparison.OrdinalIgnoreCase));
    }

    private static long EstimateReleaseBytes(
        MainPacket main,
        Dictionary<string, FileDesc> fileDescs)
    {
        return main.FileIds
            .Select(fileId => Convert.ToHexString(fileId))
            .Sum(key => fileDescs.TryGetValue(key, out var desc) ? (long)desc.FileLength : 0L);
    }

    private async Task<bool> ShouldEnqueueAsync(Guid davItemId, CancellationToken ct)
    {
        if (_queuedOrRunning.ContainsKey(davItemId)) return false;

        await using var dbContext = new DavDatabaseContext();
        var now = DateTimeOffset.UtcNow;
        var active = await dbContext.Par2RepairJobs.AsNoTracking()
            .Where(x => x.DavItemId == davItemId)
            .Where(x => x.State == Par2RepairJob.RepairJobState.Queued
                        || x.State == Par2RepairJob.RepairJobState.Running)
            .AnyAsync(ct)
            .ConfigureAwait(false);
        if (active) return false;

        var cooling = await dbContext.Par2RepairJobs.AsNoTracking()
            .Where(x => x.DavItemId == davItemId)
            .Where(x => x.NextAttemptAt != null && x.NextAttemptAt > now)
            .AnyAsync(ct)
            .ConfigureAwait(false);
        return !cooling;
    }

    private async Task<Par2RepairJob?> CreateOrResumeJobAsync(
        DavItem davItem,
        IReadOnlyList<string>? missingSegmentIds,
        CancellationToken ct)
    {
        await using var dbContext = new DavDatabaseContext();
        var existing = await dbContext.Par2RepairJobs
            .Where(x => x.DavItemId == davItem.Id)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (existing is { State: Par2RepairJob.RepairJobState.Running })
            return null;

        if (existing is { Attempts: >= MaxAttempts }
            and ({ State: Par2RepairJob.RepairJobState.Failed } or { State: Par2RepairJob.RepairJobState.Infeasible }))
            return null;

        var segments = missingSegmentIds?.ToArray()
                       ?? existing?.MissingSegmentIds
                       ?? Array.Empty<string>();

        if (existing is { State: Par2RepairJob.RepairJobState.Queued or Par2RepairJob.RepairJobState.Failed or Par2RepairJob.RepairJobState.Infeasible })
        {
            existing.Attempts++;
            existing.MissingSegmentIds = segments;
            existing.State = Par2RepairJob.RepairJobState.Queued;
            existing.CreatedAt = DateTimeOffset.UtcNow;
            return existing;
        }

        return new Par2RepairJob
        {
            Id = Guid.NewGuid(),
            DavItemId = davItem.Id,
            Path = davItem.Path,
            State = Par2RepairJob.RepairJobState.Queued,
            MissingSegmentIds = segments,
            CreatedAt = DateTimeOffset.UtcNow,
            Attempts = 1,
        };
    }

    private static async Task PersistJobAsync(Par2RepairJob job, CancellationToken ct)
    {
        await using var dbContext = new DavDatabaseContext();
        var tracked = await dbContext.Par2RepairJobs
            .FirstOrDefaultAsync(x => x.Id == job.Id, ct)
            .ConfigureAwait(false);
        if (tracked == null)
        {
            dbContext.Par2RepairJobs.Add(job);
        }
        else
        {
            dbContext.Entry(tracked).CurrentValues.SetValues(job);
        }

        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public static int CountRepairedSegments(DavNzbFile nzbFile, RepairPatchStore patchStore)
    {
        if (!patchStore.IsCatalogReady) return 0;
        var count = 0;
        var ranges = nzbFile.SegmentByteRanges;
        for (var i = 0; i < nzbFile.SegmentIds.Length; i++)
        {
            if (ranges == null || i >= ranges.Length) continue;
            var size = ranges[i].Count;

            if (patchStore.IsRepaired(nzbFile.SegmentIds[i], size))
                count++;
        }

        return count;
    }

    public Par2RepairDiagnosticSnapshot GetDiagnosticSnapshot()
    {
        var recentJobs = GetRecentJobsForDiagnostics();
        return new Par2RepairDiagnosticSnapshot
        {
            PatchStoreEntries = _patchStore.EntryCount,
            PatchHitCount = _patchStore.HitCount,
            PatchEvictionCount = _patchStore.EvictionCount,
            QueuedOrRunningCount = _queuedOrRunning.Count,
            TotalSucceeded = Interlocked.Read(ref _totalSucceeded),
            TotalFailed = Interlocked.Read(ref _totalFailed),
            TotalInfeasible = Interlocked.Read(ref _totalInfeasible),
            TotalBytesRead = Interlocked.Read(ref _totalBytesRead),
            TotalSlicesReconstructed = Interlocked.Read(ref _totalSlicesReconstructed),
            TotalSegmentsCommitted = Interlocked.Read(ref _totalSegmentsCommitted),
            RecentJobs = recentJobs,
        };
    }

    private List<object> GetRecentJobsForDiagnostics()
    {
        try
        {
            using var dbContext = new DavDatabaseContext();
            return dbContext.Par2RepairJobs
                .OrderByDescending(j => j.CreatedAt)
                .Take(10)
                .AsEnumerable()
                .Select(j => (object)new
                {
                    id = j.Id,
                    path = j.Path,
                    state = j.State.ToString(),
                    createdAt = j.CreatedAt,
                    startedAt = j.StartedAt,
                    completedAt = j.CompletedAt,
                    attempts = j.Attempts,
                    bytesRead = j.BytesRead,
                    slicesReconstructed = j.SlicesReconstructed,
                    failureReason = j.FailureReason,
                })
                .ToList();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Debug(e, "Could not load recent PAR2 repair jobs for diagnostics");
            return [];
        }
    }

    public sealed class Par2RepairDiagnosticSnapshot
    {
        public int PatchStoreEntries { get; init; }
        public long PatchHitCount { get; init; }
        public long PatchEvictionCount { get; init; }
        public int QueuedOrRunningCount { get; init; }
        public long TotalSucceeded { get; init; }
        public long TotalFailed { get; init; }
        public long TotalInfeasible { get; init; }
        public long TotalBytesRead { get; init; }
        public long TotalSlicesReconstructed { get; init; }
        public long TotalSegmentsCommitted { get; init; }
        public List<object> RecentJobs { get; init; } = [];
    }

    private sealed record RepairWorkItem(Guid DavItemId, string Path, string[] MissingSegmentIds);

    private sealed record ZeroFillEvent(string Path, string SegmentId, bool IsCorruption = false);

    private sealed record MissingSegment(string SegmentId, int Index);

    private sealed record SegmentPatch(string SegmentId, byte[] Bytes, UsenetYencHeader Header);

    private sealed record Par2SetContext(
        MainPacket Main,
        Dictionary<string, FileDesc> FileDescsById,
        Dictionary<string, IfscPacket> IfscsByFileId,
        int TargetFileIndex,
        List<NzbFile> VolumeFiles);

    private sealed record RepairExecutionResult(
        bool Success,
        bool IsInfeasible,
        string? FailureReason,
        long BytesRead,
        int SlicesReconstructed,
        int SegmentsCommitted)
    {
        public static RepairExecutionResult Succeeded(long bytesRead, int slices, int segmentsCommitted)
            => new(true, false, null, bytesRead, slices, segmentsCommitted);

        public static RepairExecutionResult NotFeasible(string reason)
            => new(false, true, reason, 0, 0, 0);

        public static RepairExecutionResult Failed(string reason)
            => new(false, false, reason, 0, 0, 0);
    }

    private sealed class SliceSegmentAccessor
    {
        private readonly string[] _segmentIds;
        private readonly Par2FileSliceMap _map;
        private readonly NzbDocument _nzbDocument;
        private readonly Par2SetContext _par2;
        private readonly UsenetStreamingClient _client;
        private readonly SemaphoreSlim _fetchGate;
        private readonly HashSet<int> _targetSlices;
        private readonly Action<long>? _onBytesRead;
        private readonly Dictionary<int, byte[]> _segmentBodies = new();
        private readonly Dictionary<int, byte[]> _siblingFileBytes = new();
        private readonly HashSet<int> _missingSegmentIndices = new();
        private readonly HashSet<int> _corruptSegmentIndices = new();
        private long _cachedBodyBytes;

        public SliceSegmentAccessor(
            string[] segmentIds,
            Par2FileSliceMap map,
            NzbDocument nzbDocument,
            Par2SetContext par2,
            UsenetStreamingClient client,
            SemaphoreSlim fetchGate,
            HashSet<int> targetSlices,
            Action<long>? onBytesRead)
        {
            _segmentIds = segmentIds;
            _map = map;
            _nzbDocument = nzbDocument;
            _par2 = par2;
            _client = client;
            _fetchGate = fetchGate;
            _targetSlices = targetSlices;
            _onBytesRead = onBytesRead;
        }

        public IReadOnlyCollection<int> MissingSegmentIndices => _missingSegmentIndices;
        public IReadOnlyCollection<int> CorruptSegmentIndices => _corruptSegmentIndices;
        public long CachedBodyBytes => Interlocked.Read(ref _cachedBodyBytes);

        public void NoteMissing(int segmentIndex) => _missingSegmentIndices.Add(segmentIndex);

        public void NoteCorrupt(int segmentIndex) => _corruptSegmentIndices.Add(segmentIndex);

        public async Task<byte[]?> FetchSliceBytesAsync(int globalSliceIndex, int sliceSize, CancellationToken ct)
        {
            var local = globalSliceIndex - _map.GlobalSliceBase;
            if ((uint)local >= (uint)_map.SliceCount)
                return await FetchForeignSliceAsync(globalSliceIndex, sliceSize, ct).ConfigureAwait(false);

            if (_targetSlices.Contains(globalSliceIndex))
                return null;

            var sliceRange = _map.SliceFileRange(globalSliceIndex);
            var buffer = new byte[sliceSize];
            var copied = 0;
            foreach (var segmentIndex in _map.SegmentIndicesForGlobalSlice(globalSliceIndex))
            {
                var body = await GetSegmentBodyAsync(segmentIndex, ct).ConfigureAwait(false);
                if (body is null)
                {
                    if (sliceRange.EndExclusive < _map.FileLength)
                        return null;
                    break;
                }

                var segmentRange = _map.SegmentRanges[segmentIndex];
                var intersectStart = Math.Max(sliceRange.StartInclusive, segmentRange.StartInclusive);
                var intersectEnd = Math.Min(sliceRange.EndExclusive, segmentRange.EndExclusive);
                if (intersectEnd <= intersectStart)
                    continue;

                var destOffset = (int)(intersectStart - sliceRange.StartInclusive);
                var srcOffset = (int)(intersectStart - segmentRange.StartInclusive);
                var count = (int)(intersectEnd - intersectStart);
                if (srcOffset < 0 || srcOffset + count > body.Length)
                    return null;
                Buffer.BlockCopy(body, srcOffset, buffer, destOffset, count);
                copied += count;
            }

            if (copied < sliceRange.Count && sliceRange.EndExclusive < _map.FileLength)
                return null;
            return buffer;
        }

        public byte[]? TryGetCachedSlice(int globalSliceIndex, int sliceSize)
        {
            if (_targetSlices.Contains(globalSliceIndex))
                return null;

            var sliceRange = _map.SliceFileRange(globalSliceIndex);
            var buffer = new byte[sliceSize];
            var copied = 0;
            foreach (var segmentIndex in _map.SegmentIndicesForGlobalSlice(globalSliceIndex))
            {
                if (!_segmentBodies.TryGetValue(segmentIndex, out var body))
                    return null;
                var segmentRange = _map.SegmentRanges[segmentIndex];
                var intersectStart = Math.Max(sliceRange.StartInclusive, segmentRange.StartInclusive);
                var intersectEnd = Math.Min(sliceRange.EndExclusive, segmentRange.EndExclusive);
                if (intersectEnd <= intersectStart)
                    continue;
                var destOffset = (int)(intersectStart - sliceRange.StartInclusive);
                var srcOffset = (int)(intersectStart - segmentRange.StartInclusive);
                var count = (int)(intersectEnd - intersectStart);
                Buffer.BlockCopy(body, srcOffset, buffer, destOffset, count);
                copied += count;
            }

            return copied < sliceRange.Count && sliceRange.EndExclusive < _map.FileLength ? null : buffer;
        }

        public Task<byte[]?> TryGetCachedBodyAsync(int segmentIndex, CancellationToken ct)
        {
            _ = ct;
            return Task.FromResult(
                _segmentBodies.TryGetValue(segmentIndex, out var body) ? body : null);
        }

        private async Task<byte[]?> GetSegmentBodyAsync(int segmentIndex, CancellationToken ct)
        {
            if (_missingSegmentIndices.Contains(segmentIndex) || _corruptSegmentIndices.Contains(segmentIndex))
                return null;
            if (_segmentBodies.TryGetValue(segmentIndex, out var cached))
                return cached;

            await _fetchGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_segmentBodies.TryGetValue(segmentIndex, out cached))
                    return cached;
                if (_missingSegmentIndices.Contains(segmentIndex) || _corruptSegmentIndices.Contains(segmentIndex))
                    return null;

                var segmentId = _segmentIds[segmentIndex];
                try
                {
                    var response = await _client.DecodedBodyAsync(segmentId, ct).ConfigureAwait(false);
                    await using var stream = response.Stream!;
                    await using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
                    var bytes = ms.ToArray();
                    _onBytesRead?.Invoke(bytes.Length);
                    _segmentBodies[segmentIndex] = bytes;
                    Interlocked.Add(ref _cachedBodyBytes, bytes.Length);
                    return bytes;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    if (e.IsCancellationException(ct))
                        throw;

                    if (e.TryGetCausingException<UsenetArticleNotFoundException>(out _))
                    {
                        _missingSegmentIndices.Add(segmentIndex);
                        return null;
                    }

                    if (e.TryGetCausingException<UsenetCorruptArticleException>(out _))
                    {
                        _corruptSegmentIndices.Add(segmentIndex);
                        return null;
                    }

                    throw;
                }
            }
            finally
            {
                _fetchGate.Release();
            }
        }

        private async Task<byte[]?> FetchForeignSliceAsync(int globalSliceIndex, int sliceSize, CancellationToken ct)
        {
            var offset = 0;
            for (var fileIndex = 0; fileIndex < _par2.Main.FileIds.Count; fileIndex++)
            {
                var key = Convert.ToHexString(_par2.Main.FileIds[fileIndex]);
                if (!_par2.IfscsByFileId.TryGetValue(key, out var ifsc))
                    return null;

                var count = ifsc.Slices.Count;
                if (globalSliceIndex >= offset + count)
                {
                    offset += count;
                    continue;
                }

                if (fileIndex == _par2.TargetFileIndex)
                    return null;

                var fileBytes = await GetSiblingFileBytesAsync(fileIndex, ct).ConfigureAwait(false);
                if (fileBytes is null)
                    return null;

                var local = globalSliceIndex - offset;
                var start = checked(local * sliceSize);
                var slice = new byte[sliceSize];
                if (start >= fileBytes.Length)
                    return slice;

                var copy = Math.Min(sliceSize, fileBytes.Length - start);
                Buffer.BlockCopy(fileBytes, start, slice, 0, copy);
                return slice;
            }

            return null;
        }

        private async Task<byte[]?> GetSiblingFileBytesAsync(int fileIndex, CancellationToken ct)
        {
            if (_siblingFileBytes.TryGetValue(fileIndex, out var cached))
                return cached;

            var key = Convert.ToHexString(_par2.Main.FileIds[fileIndex]);
            if (!_par2.FileDescsById.TryGetValue(key, out var desc))
                return null;

            var nzbFile = FindContentNzbFile(_nzbDocument, desc.FileName);
            if (nzbFile is null || nzbFile.Segments.Count == 0)
                return null;

            await _fetchGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_siblingFileBytes.TryGetValue(fileIndex, out cached))
                    return cached;

                var segmentIds = nzbFile.GetSegmentIds();
                var fileSize = nzbFile.Segments.Sum(segment => segment.Bytes);
                await using var stream = _client.GetFileStream(segmentIds, fileSize, articleBufferSize: 0);
                await using var destination = new MemoryStream();
                await stream.CopyToAsync(destination, ct).ConfigureAwait(false);
                var bytes = destination.ToArray();
                _onBytesRead?.Invoke(bytes.Length);
                _siblingFileBytes[fileIndex] = bytes;
                return bytes;
            }
            finally
            {
                _fetchGate.Release();
            }
        }
    }
}
