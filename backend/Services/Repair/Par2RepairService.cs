using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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

public sealed class Par2RepairService : BackgroundService
{
    private const int MaxQueueLength = 50;
    private const int MaxAttempts = 3;

    private readonly ConfigManager _configManager;
    private readonly UsenetStreamingClient _usenetClient;
    private readonly RepairPatchStore _patchStore;
    private readonly Channel<RepairWorkItem> _queue;
    private readonly ConcurrentDictionary<Guid, byte> _queuedOrRunning = new();

    public Par2RepairService(
        ConfigManager configManager,
        UsenetStreamingClient usenetClient,
        RepairPatchStore patchStore)
    {
        _configManager = configManager;
        _usenetClient = usenetClient;
        _patchStore = patchStore;
        _queue = Channel.CreateBounded<RepairWorkItem>(new BoundedChannelOptions(MaxQueueLength)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public async Task EnqueueZeroFillAsync(
        string path,
        string segmentId,
        int segmentIndex,
        long fillBytes,
        CancellationToken ct = default)
    {
        if (!_configManager.IsPar2RepairEnabled()) return;

        await using var dbContext = new DavDatabaseContext();
        var dbClient = new DavDatabaseClient(dbContext);
        var davItem = await dbClient.GetItemByPathAsync(path, ct).ConfigureAwait(false);
        if (davItem == null) return;

        await EnqueueAsync(davItem, [segmentId], ct).ConfigureAwait(false);
    }

    public async Task EnqueueAsync(
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
    /// </summary>
    public Task<bool> TryPar2RepairAsync(
        DavItem davItem,
        IReadOnlyList<string>? missingSegmentIds,
        CancellationToken ct)
        => RunRepairAsync(davItem, missingSegmentIds, queueGuard: false, ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _patchStore.CatalogLoadTask.ConfigureAwait(false);

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
                Log.Information(
                    "PAR2 repair succeeded for {Path}: {Slices} slice(s), {Bytes} bytes read in {Elapsed}",
                    davItem.Path, result.SlicesReconstructed, result.BytesRead, stopwatch.Elapsed);
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

        var missingSegments = ResolveMissingSegments(job.MissingSegmentIds, segmentIds);
        if (missingSegments.Count == 0 && job.MissingSegmentIds.Length == 0)
        {
            missingSegments = segmentIds
                .Select((id, index) => new MissingSegment(id, index))
                .ToList();
        }

        if (missingSegments.Count == 0)
            return RepairExecutionResult.NotFeasible("No missing segments to repair.");

        var sliceSize = (int)par2Context.Main.SliceSize;
        var maxMissingSlices = _configManager.GetPar2MaxMissingSlices();
        var maxMemoryBytes = _configManager.GetPar2MaxMemoryMb() * 1024L * 1024L;
        var workingSetBytes = (long)(missingSegments.Count + 2) * sliceSize;
        if (workingSetBytes > maxMemoryBytes)
            return RepairExecutionResult.NotFeasible(
                $"PAR2 working set {workingSetBytes} bytes exceeds memory cap.");

        var releaseBytesCap = _configManager.GetPar2MaxReleaseGb() * 1024L * 1024L * 1024L;
        var releaseBytes = EstimateReleaseBytes(par2Context.Main, par2Context.FileDescsById);
        if (releaseBytes > releaseBytesCap)
            return RepairExecutionResult.NotFeasible(
                $"Recovery set size {releaseBytes} bytes exceeds release cap.");

        var segmentRanges = BuildSegmentRanges(nzbFile, segmentIds.Length, davItem.FileSize);
        var missingSliceIndices = MapMissingSegmentsToSlices(
            missingSegments, segmentRanges, sliceSize, par2Context.TargetFileIndex, par2Context.Main, par2Context.IfscsByFileId);
        if (missingSliceIndices.Count == 0)
            return RepairExecutionResult.NotFeasible("Missing segments do not map to PAR2 slices.");

        if (missingSliceIndices.Count > maxMissingSlices)
            return RepairExecutionResult.NotFeasible(
                $"Missing slice count {missingSliceIndices.Count} exceeds cap {maxMissingSlices}.");

        var fetchConcurrency = _configManager.GetPar2FetchConcurrency();
        using var fetchGate = new SemaphoreSlim(fetchConcurrency, fetchConcurrency);
        var bytesRead = 0L;

        var recoverySlices = await CollectRecoverySlicesAsync(
            par2Context.VolumeFiles,
            missingSliceIndices.Count,
            par2Context.Main.SliceSize,
            fetchGate,
            ct,
            onBytesRead: n => bytesRead += n).ConfigureAwait(false);
        if (recoverySlices.Count < missingSliceIndices.Count)
            return RepairExecutionResult.NotFeasible(
                $"Need {missingSliceIndices.Count} recovery slices but only collected {recoverySlices.Count}.");

        var accessor = new SliceSegmentAccessor(
            segmentIds,
            segmentRanges,
            contentNzb,
            _usenetClient,
            fetchGate,
            onBytesRead: n => bytesRead += n);

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
            return RepairExecutionResult.Failed(reconstruction.FailureReason ?? "Reconstruction failed.");
        }

        var patchTargets = job.MissingSegmentIds.Length > 0
            ? missingSegments
            : accessor.GetMissingSegments();
        if (patchTargets.Count == 0)
            return RepairExecutionResult.NotFeasible("No missing segments were confirmed during PAR2 repair.");

        var commits = ExtractSegmentPatches(
            patchTargets,
            segmentRanges,
            reconstruction.ReconstructedSlices,
            sliceSize,
            par2Context.TargetFileIndex,
            par2Context.Main,
            par2Context.IfscsByFileId,
            davItem.Name,
            davItem.FileSize ?? 0,
            segmentIds.Length);

        foreach (var patch in commits)
            _patchStore.CommitPatch(patch.SegmentId, patch.Bytes, patch.Header);

        PrometheusMetrics.Current?.AddPar2RepairBytesRead(bytesRead);
        PrometheusMetrics.Current?.AddPar2SegmentsReconstructed(commits.Count);
        job.MissingSegmentIds = patchTargets.Select(x => x.SegmentId).ToArray();
        return RepairExecutionResult.Succeeded(bytesRead, reconstruction.ReconstructedSlices.Count);
    }

    private static List<SegmentPatch> ExtractSegmentPatches(
        IReadOnlyList<MissingSegment> missingSegments,
        LongRange[] segmentRanges,
        Dictionary<int, byte[]> reconstructedSlices,
        int sliceSize,
        int targetFileIndex,
        MainPacket main,
        IReadOnlyDictionary<string, IfscPacket> ifscsByFileId,
        string fileName,
        long fileSize,
        int segmentCount)
    {
        var globalSliceOffset = GlobalSliceOffset(targetFileIndex, main, ifscsByFileId);
        var patches = new List<SegmentPatch>();

        foreach (var missing in missingSegments)
        {
            var range = segmentRanges[missing.Index];
            var bytes = new byte[range.Count];
            var copied = 0L;
            var fileOffset = range.StartInclusive;

            while (copied < range.Count)
            {
                var localSlice = (int)((fileOffset + copied) / sliceSize);
                var globalSlice = globalSliceOffset + localSlice;
                if (!reconstructedSlices.TryGetValue(globalSlice, out var slice))
                    throw new InvalidOperationException($"Reconstructed slice {globalSlice} missing for segment patch.");

                var offsetInSlice = (int)((fileOffset + copied) % sliceSize);
                var toCopy = (int)Math.Min(range.Count - copied, sliceSize - offsetInSlice);
                Buffer.BlockCopy(slice, offsetInSlice, bytes, (int)copied, toCopy);
                copied += toCopy;
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
        IReadOnlyDictionary<string, IfscPacket> ifscsByFileId)
    {
        var offset = 0;
        for (var i = 0; i < targetFileIndex; i++)
        {
            var key = Convert.ToHexString(main.FileIds[i]);
            offset += ifscsByFileId[key].Slices.Count;
        }

        return offset;
    }

    private static List<int> MapMissingSegmentsToSlices(
        IReadOnlyList<MissingSegment> missingSegments,
        LongRange[] segmentRanges,
        int sliceSize,
        int targetFileIndex,
        MainPacket main,
        IReadOnlyDictionary<string, IfscPacket> ifscsByFileId)
    {
        var globalOffset = GlobalSliceOffset(targetFileIndex, main, ifscsByFileId);
        var slices = new HashSet<int>();
        foreach (var missing in missingSegments)
        {
            var start = segmentRanges[missing.Index].StartInclusive;
            var end = segmentRanges[missing.Index].EndExclusive;
            for (var offset = start; offset < end; offset += sliceSize)
            {
                var localSlice = (int)(offset / sliceSize);
                slices.Add(globalOffset + localSlice);
            }
        }

        return slices.OrderBy(x => x).ToList();
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

        var result = new List<MissingSegment>();
        foreach (var id in requestedIds)
        {
            if (indexById.TryGetValue(id, out var index))
                result.Add(new MissingSegment(id, index));
        }

        return result;
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
                        && byExponent.TryAdd(recvSlic.Exponent, recvSlic.Payload))
                    {
                        if (byExponent.Count >= needed) break;
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
        long total = 0;
        foreach (var fileId in main.FileIds)
        {
            var key = Convert.ToHexString(fileId);
            if (fileDescs.TryGetValue(key, out var desc))
                total += (long)desc.FileLength;
        }

        return total;
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

    private sealed record RepairWorkItem(Guid DavItemId, string Path, string[] MissingSegmentIds);

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
        int SlicesReconstructed)
    {
        public static RepairExecutionResult Succeeded(long bytesRead, int slices)
            => new(true, false, null, bytesRead, slices);

        public static RepairExecutionResult NotFeasible(string reason)
            => new(false, true, reason, 0, 0);

        public static RepairExecutionResult Failed(string reason)
            => new(false, false, reason, 0, 0);
    }

    private sealed class SliceSegmentAccessor
    {
        private readonly string[] _segmentIds;
        private readonly LongRange[] _ranges;
        private readonly NzbFile _contentNzb;
        private readonly UsenetStreamingClient _client;
        private readonly SemaphoreSlim _fetchGate;
        private readonly Action<long>? _onBytesRead;
        private readonly Dictionary<int, byte[]> _segmentBodies = new();
        private readonly HashSet<int> _missingSegmentIndices = new();

        public SliceSegmentAccessor(
            string[] segmentIds,
            LongRange[] ranges,
            NzbFile contentNzb,
            UsenetStreamingClient client,
            SemaphoreSlim fetchGate,
            Action<long>? onBytesRead)
        {
            _segmentIds = segmentIds;
            _ranges = ranges;
            _contentNzb = contentNzb;
            _client = client;
            _fetchGate = fetchGate;
            _onBytesRead = onBytesRead;
        }

        public async Task<byte[]?> FetchSliceBytesAsync(int globalSliceIndex, int sliceSize, CancellationToken ct)
        {
            var fileOffset = (long)globalSliceIndex * sliceSize;
            var segmentIndex = FindSegmentIndex(fileOffset);
            if (segmentIndex < 0) return null;

            var body = await GetSegmentBodyAsync(segmentIndex, ct).ConfigureAwait(false);
            if (body == null) return null;

            var segmentStart = _ranges[segmentIndex].StartInclusive;
            var offsetInSegment = (int)(fileOffset - segmentStart);
            if (offsetInSegment < 0 || offsetInSegment + sliceSize > body.Length)
            {
                var slice = new byte[sliceSize];
                var available = Math.Min(sliceSize, body.Length - offsetInSegment);
                if (available > 0)
                    Buffer.BlockCopy(body, offsetInSegment, slice, 0, available);
                return slice;
            }

            var exact = new byte[sliceSize];
            Buffer.BlockCopy(body, offsetInSegment, exact, 0, sliceSize);
            return exact;
        }

        private int FindSegmentIndex(long fileOffset)
        {
            for (var i = 0; i < _ranges.Length; i++)
            {
                if (fileOffset >= _ranges[i].StartInclusive && fileOffset < _ranges[i].EndExclusive)
                    return i;
            }

            return -1;
        }

        private async Task<byte[]?> GetSegmentBodyAsync(int segmentIndex, CancellationToken ct)
        {
            if (_segmentBodies.TryGetValue(segmentIndex, out var cached))
                return cached;

            await _fetchGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_segmentBodies.TryGetValue(segmentIndex, out cached))
                    return cached;

                var segmentId = _segmentIds[segmentIndex];
                UsenetDecodedBodyResponse response;
                try
                {
                    response = await _client.DecodedBodyAsync(segmentId, ct).ConfigureAwait(false);
                }
                catch (UsenetArticleNotFoundException)
                {
                    _missingSegmentIndices.Add(segmentIndex);
                    return null;
                }

                await using var stream = response.Stream!;
                await using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
                var bytes = ms.ToArray();
                _onBytesRead?.Invoke(bytes.Length);
                _segmentBodies[segmentIndex] = bytes;
                return bytes;
            }
            finally
            {
                _fetchGate.Release();
            }
        }

        public List<MissingSegment> GetMissingSegments()
            => _missingSegmentIndices
                .OrderBy(i => i)
                .Select(i => new MissingSegment(_segmentIds[i], i))
                .ToList();
    }
}
