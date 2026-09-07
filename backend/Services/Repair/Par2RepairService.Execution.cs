using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Par2Recovery.ReedSolomon;
using NzbWebDAV.Services.Observability;
using Serilog;

namespace NzbWebDAV.Services.Repair;

public partial class Par2RepairService
{
    private sealed record RepairTarget(SourceLayout Layout, List<MissingSegment> Segments);

    private async Task<RepairExecutionResult> ExecuteRepairJobAsync(DavItem item, Par2RepairJob job, CancellationToken ct)
    {
        if (item.SubType is not (DavItem.ItemSubType.NzbFile or DavItem.ItemSubType.RarFile or DavItem.ItemSubType.MultipartFile))
            return RepairExecutionResult.NotFeasible("This item has no supported Usenet streaming payload.");
        if (item.SubType != DavItem.ItemSubType.NzbFile && job.MissingSegmentIds.Length == 0)
            return RepairExecutionResult.NotFeasible("PAR2 full verification supports plain NZB files only; multi-volume repair requires specific segment ids.");
        if (item.NzbBlobId is not Guid nzbBlobId)
            return RepairExecutionResult.NotFeasible("NZB blob id is missing for this file.");

        var maxMemoryBytes = _configManager.GetPar2MaxMemoryMb() * 1024L * 1024;
        var concurrency = _configManager.GetPar2FetchConcurrency();
        using var reads = new RepairReadContext(maxMemoryBytes, concurrency, bytes => Interlocked.Add(ref _activeBytesRead, bytes));
        RepairPayload? payload = null;
        ResolvedSliceAccessor? accessor = null;
        List<SourceLayout>? layouts = null;
        try
        {
            SetRepairPhase("metadata", maxMemoryBytes);
            reads.Budget.Charge(1024L * 1024 + concurrency * 2L * 1024 * 1024);
            await using var nzbStream = BlobStore.ReadBlob(nzbBlobId);
            if (nzbStream is null) return RepairExecutionResult.NotFeasible("NZB blob is no longer available.");
            var xmlCharacters = Math.Max(1, Math.Min(nzbStream.Length, maxMemoryBytes / 16));
            var document = await NzbDocument.LoadAsync(nzbStream,
                new NzbReadOptions(xmlCharacters, reads.Budget.Charge, reads.Budget.Reserve), ct).ConfigureAwait(false);
            payload = await LoadRepairPayloadAsync(item, reads, ct).ConfigureAwait(false);
            reads.UnavailableIds.UnionWith(job.MissingSegmentIds);
            var resolved = await ResolveRecoverySetAsync(document, payload, reads, ct).ConfigureAwait(false);
            var set = resolved.Set;
            layouts = resolved.Layouts;
            var sliceSize = checked((int)set.Main.SliceSize);
            var unavailableSlices = new HashSet<int>();
            accessor = new ResolvedSliceAccessor(layouts, _usenetClient, reads, unavailableSlices);
            _activeSource = accessor;
            foreach (var layout in layouts)
            {
                reads.Budget.Charge(512L + layout.SegmentIds.Length * 128L);
                accessor.UseLayout(layout);
                for (var index = 0; index < layout.SegmentIds.Length; index++)
                {
                    var id = layout.SegmentIds[index];
                    if (reads.UnavailableIds.Contains(id)) unavailableSlices.UnionWith(layout.Map.GlobalSlicesForSegment(index));
                    if (reads.MissingIds.Contains(id)) accessor.NoteMissing(index);
                    if (reads.CorruptIds.Contains(id)) accessor.NoteCorrupt(index);
                }
                if (payload.PlainFile is { } plain && layout.PayloadOwned)
                {
                    foreach (var index in ValidIndices(plain.MissingSegmentIndices, layout.SegmentIds.Length)) accessor.NoteMissing(index);
                    foreach (var index in ValidIndices(plain.CorruptSegmentIndices, layout.SegmentIds.Length)) accessor.NoteCorrupt(index);
                }
            }

            var maxMissing = _configManager.GetPar2MaxMissingSlices();
            if (unavailableSlices.Count > maxMissing) return SliceCapFailure();
            SetRepairPhase("discovery", maxMemoryBytes);
            using (reads.Budget.Reserve(2L * sliceSize + 8192))
            {
                accessor.SetRetainedByteLimit(maxMemoryBytes - reads.Budget.ReservedBytes);
                foreach (var layout in layouts)
                {
                    accessor.UseLayout(layout);
                    var unavailableSegments = layout.SegmentIds.Select((id, index) => (id, index))
                        .Where(segment => reads.UnavailableIds.Contains(segment.id)).Select(segment => segment.index).ToHashSet();
                    if (await DiscoverUnavailableSourcesAsync(accessor, layout.Map, layout.Checksums,
                            unavailableSegments, unavailableSlices, maxMissing, ct).ConfigureAwait(false))
                        return SliceCapFailure();
                }
                accessor.BeginSequentialPass();
            }
            Log.Information("PAR2 source discovery completed for {Path}: Volumes={Volumes} MissingSlices={MissingSlices} BytesRead={BytesRead}",
                item.Path, layouts.Count, unavailableSlices.Count, reads.BytesRead);
            if (unavailableSlices.Count == 0) return RepairExecutionResult.Verified(reads.BytesRead);

            var targets = layouts.Where(layout => layout.PayloadOwned)
                .Select(layout => new RepairTarget(layout, SegmentsOverlappingSlices(layout.Map, unavailableSlices, layout.SegmentIds)
                    .Where(segment => payload.SegmentIds.Contains(segment.SegmentId)).ToList()))
                .Where(target => target.Segments.Count > 0).ToList();
            if (targets.Count == 0)
                return RepairExecutionResult.NotFeasible("No missing or corrupt payload articles were confirmed during PAR2 repair.", reads.BytesRead);
            var patchBytes = targets.Sum(target => target.Segments.Sum(segment => target.Layout.Map.SegmentRanges[segment.Index].Count));
            if (patchBytes > _patchStore.MaxBytes)
                return RepairExecutionResult.NotFeasible("The repair batch exceeds the patch store's effective capacity.", reads.BytesRead);
            var missing = unavailableSlices.OrderBy(index => index).ToList();
            var sourceWindow = layouts.Max(layout => layout.Map.EstimateMaxOverlappingSegmentBytes() + 128L * layout.SegmentIds.Length);
            var nonSourceBytes = checked(EstimateNonSourceWorkingSetBytes(missing.Count, missing.Count, patchBytes, sliceSize)
                + 4L * missing.Count * missing.Count + 1024L * missing.Count + PooledSliceCapacity(sliceSize) + 8192);
            var estimate = checked(reads.Budget.ReservedBytes + nonSourceBytes + sourceWindow);
            if (estimate > maxMemoryBytes)
                return RepairExecutionResult.NotFeasible($"PAR2 working set {estimate} bytes exceeds memory cap.", reads.BytesRead);
            Interlocked.Exchange(ref _activeEstimatedWorkingSetBytes, estimate);

            SetRepairPhase("recovery-volumes", maxMemoryBytes);
            using var reconstructionMemory = reads.Budget.Reserve(nonSourceBytes - missing.Count * (sliceSize + 512L));
            var recovery = await CollectMatchingRecoverySlicesAsync(document, set, missing.Count, reads, ct).ConfigureAwait(false);
            if (recovery.Count < missing.Count)
                return RepairExecutionResult.NotFeasible($"Need {missing.Count} recovery slices but only collected {recovery.Count}.", reads.BytesRead);

            accessor.SetRetainedByteLimit(maxMemoryBytes - reads.Budget.ReservedBytes);
            SetRepairPhase("reconstruction", maxMemoryBytes);
            var reconstruction = await new Par2Reconstructor().ReconstructAsync(set.Main, set.FileDescsById,
                set.IfscsByFileId, missing, recovery, accessor.FetchSliceBytesAsync, ct).ConfigureAwait(false);
            if (!reconstruction.Success)
            {
                PrometheusMetrics.Current?.RecordPar2ValidationFailure("slice");
                return RepairExecutionResult.Failed(reconstruction.FailureReason ?? "Reconstruction failed.", reads.BytesRead);
            }

            var patches = new List<SegmentPatch>();
            foreach (var target in targets)
            {
                var layout = target.Layout;
                accessor.UseLayout(layout);
                accessor.BeginSequentialPass();
                SetRepairPhase("assembling-patches", maxMemoryBytes);
                patches.AddRange(await ExtractSegmentPatchesAsync(target.Segments, layout.Map, reconstruction.ReconstructedSlices,
                    accessor, layout.Descriptor.FileName, layout.Map.FileLength, layout.SegmentIds.Length, ct).ConfigureAwait(false));
                SetRepairPhase("whole-file-verification", maxMemoryBytes);
                accessor.BeginSequentialPass();
                var failure = await TryVerifyWholeFileMd5Async(layout.Descriptor, layout.Map,
                    reconstruction.ReconstructedSlices, accessor, ct).ConfigureAwait(false);
                if (failure is null) continue;
                PrometheusMetrics.Current?.RecordPar2ValidationFailure("file");
                return RepairExecutionResult.Failed(failure, reads.BytesRead);
            }

            ct.ThrowIfCancellationRequested();
            SetRepairPhase("committing-patches", maxMemoryBytes);
            _patchStore.CommitPatches(patches.Select(patch => (patch.SegmentId, patch.Bytes, patch.Header)).ToList());
            PrometheusMetrics.Current?.AddPar2RepairBytesRead(reads.BytesRead);
            PrometheusMetrics.Current?.AddPar2SlicesReconstructed(reconstruction.ReconstructedSlices.Count);
            PrometheusMetrics.Current?.AddPar2SegmentsCommitted(patches.Count);
            job.MissingSegmentIds = patches.Select(patch => patch.SegmentId).ToArray();
            return RepairExecutionResult.Succeeded(reads.BytesRead, reconstruction.ReconstructedSlices.Count, patches.Count);

            RepairExecutionResult SliceCapFailure()
                => RepairExecutionResult.NotFeasible($"Missing slice count {unavailableSlices.Count} exceeds cap {maxMissing}.", reads.BytesRead);
        }
        catch (Exception exception) when (exception is RepairInfeasibleException or Par2BudgetExceededException
                                           or Par2MemoryCapExceededException or InvalidDataException or OverflowException)
        {
            return RepairExecutionResult.NotFeasible(exception.Message, reads.BytesRead);
        }
        finally
        {
            try
            {
                if (!ct.IsCancellationRequested && payload?.PlainFile is { } plain && accessor is not null
                    && layouts?.SingleOrDefault(layout => layout.PayloadOwned) is { } target)
                {
                    accessor.UseLayout(target);
                    await PersistDiscoveredDamageAsync(item, plain, accessor, ct).ConfigureAwait(false);
                }
            }
            finally { accessor?.Dispose(); }
        }
    }

    private static long PooledSliceCapacity(int sliceSize)
        => System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, sliceSize));
}