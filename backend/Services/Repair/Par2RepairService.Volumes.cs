using System.Security.Cryptography;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Par2Recovery.Packets;
using NzbWebDAV.Par2Recovery.ReedSolomon;
using UsenetSharp.Models;

namespace NzbWebDAV.Services.Repair;

public partial class Par2RepairService
{
    private sealed record RepairPayload(DavNzbFile? PlainFile, HashSet<string> SegmentIds,
        Dictionary<string, LongRange> TrustedRanges, List<(string[] SegmentIds, Par2FileProof Proof)> Proofs);

    private sealed record SourceLayout(int FileIndex, NzbFile File, FileDesc Descriptor,
        IfscPacket Checksums, Par2FileSliceMap Map, string[] SegmentIds, bool PayloadOwned);

    private async Task<RepairPayload> LoadRepairPayloadAsync(DavItem item, RepairReadContext reads, CancellationToken ct)
    {
        await using var context = CreateContext();
        var client = new DavDatabaseClient(context);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var ranges = new Dictionary<string, LongRange>(StringComparer.Ordinal);
        var proofs = new List<(string[] SegmentIds, Par2FileProof Proof)>();
        DavNzbFile? plain = null;
        switch (item.SubType)
        {
            case DavItem.ItemSubType.NzbFile:
                plain = await client.GetDavNzbFileAsync(item, ct).ConfigureAwait(false)
                    ?? throw new MissingFilePayloadException(item, item.SubType);
                AddPart(plain.SegmentIds, plain.SegmentByteRanges, plain.SegmentByteRangesTrusted, plain.VerificationProof);
                foreach (var index in ValidIndices(plain.MissingSegmentIndices, plain.SegmentIds.Length)
                             .Concat(ValidIndices(plain.CorruptSegmentIndices, plain.SegmentIds.Length)))
                    reads.UnavailableIds.Add(plain.SegmentIds[index]);
                break;
            case DavItem.ItemSubType.RarFile:
                var rar = await client.GetDavRarFileAsync(item, ct).ConfigureAwait(false)
                    ?? throw new MissingFilePayloadException(item, item.SubType);
                foreach (var part in rar.RarParts)
                    AddPart(part.SegmentIds, part.SegmentByteRanges, part.SegmentByteRangesTrusted);
                break;
            case DavItem.ItemSubType.MultipartFile:
                var multipart = await client.GetDavMultipartFileAsync(item, ct).ConfigureAwait(false)
                    ?? throw new MissingFilePayloadException(item, item.SubType);
                foreach (var part in multipart.Metadata.FileParts)
                    AddPart(part.SegmentIds, part.SegmentByteRanges, part.SegmentByteRangesTrusted, part.VerificationProof);
                foreach (var part in multipart.Metadata.PendingParts ?? [])
                    AddPart(part.SegmentIds, null, false, part.VerificationProof);
                break;
            default:
                throw new RepairInfeasibleException("This item has no supported Usenet streaming payload.");
        }

        if (ids.Count == 0) throw new MissingFilePayloadException(item, item.SubType);
        return new RepairPayload(plain, ids, ranges, proofs);

        void AddPart(string[] segmentIds, LongRange[]? segmentRanges, bool? trusted, Par2FileProof? proof = null)
        {
            if (proof is not null && segmentIds.Length > 0 && proof.IsValidFor(proof.FileLength))
                proofs.Add((segmentIds, proof));
            if (segmentIds.Any(string.IsNullOrWhiteSpace) || segmentIds.Distinct(StringComparer.Ordinal).Count() != segmentIds.Length)
                throw new RepairInfeasibleException("Streaming payload has empty or duplicate article IDs in a volume part.");
            if (trusted == true && segmentRanges?.Length != segmentIds.Length)
                throw new RepairInfeasibleException("Trusted streaming payload ranges do not match their article IDs.");
            for (var index = 0; index < segmentIds.Length; index++)
            {
                var id = segmentIds[index];
                if (ids.Add(id)) reads.Budget.Charge(512L + id.Length * 4L);
                if (trusted != true) continue;
                var range = segmentRanges![index];
                if (ranges.TryGetValue(id, out var previous) && previous != range)
                    throw new RepairInfeasibleException("Trusted streaming payload ranges disagree for the same article.");
                ranges[id] = range;
            }
        }
    }

    private async Task<(Par2SetContext Set, List<SourceLayout> Layouts)> ResolveRecoverySetAsync(
        NzbDocument document, RepairPayload payload, RepairReadContext reads, CancellationToken ct)
    {
        if (payload.SegmentIds.Any(NzbFile.IsOmittedSegmentId))
        {
            foreach (var file in document.Files)
                file.RestoreStoredOmittedSegments(payload.SegmentIds, reads.Budget.Charge);
        }
        var owners = new Dictionary<string, NzbFile?>(StringComparer.Ordinal);
        foreach (var file in document.Files)
        foreach (var segment in file.Segments)
        {
            reads.Budget.Charge(96);
            if (!owners.TryAdd(segment.MessageId, file)) owners[segment.MessageId] = null;
        }
        var requestedOwners = new HashSet<NzbFile>(ReferenceEqualityComparer.Instance);
        foreach (var id in reads.UnavailableIds)
        {
            if (!payload.SegmentIds.Contains(id) || !owners.TryGetValue(id, out var owner) || owner is null)
                throw new RepairInfeasibleException("Reported article IDs must belong uniquely to this item's retained NZB payload.");
            requestedOwners.Add(owner);
        }
        if (requestedOwners.Count == 0 && payload.PlainFile is not null)
        {
            foreach (var id in payload.SegmentIds)
            {
                if (!owners.TryGetValue(id, out var owner) || owner is null)
                    throw new RepairInfeasibleException("Plain streaming payload does not map uniquely to the retained NZB.");
                requestedOwners.Add(owner);
            }
        }

        // Import-time identity: null marks a volume whose parts carry conflicting proofs.
        var proofs = new Dictionary<NzbFile, Par2FileProof?>(ReferenceEqualityComparer.Instance);
        foreach (var (ids, proof) in payload.Proofs)
        {
            if (!owners.TryGetValue(ids[0], out var owner) || owner is null
                || ids.Any(id => !owners.TryGetValue(id, out var other) || other != owner)) continue;
            proofs[owner] = proofs.TryGetValue(owner, out var previous)
                            && (previous is null || !previous.FileId.AsSpan().SequenceEqual(proof.FileId)) ? null : proof;
        }

        var coveredOwners = new HashSet<NzbFile>(ReferenceEqualityComparer.Instance);
        reads.RequiredSources.UnionWith(requestedOwners);
        await foreach (var set in DiscoverPar2SetsAsync(document, reads, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            reads.RequiredSourceMissSlices.Clear();
            var sourceCandidates = document.Files.Where(file => !reads.ParityFiles.Contains(file) && file.Segments.Count > 0).ToArray();
            reads.AdmitSourceComparisons(sourceCandidates.Length, set.Main.FileIds.Count);
            var layouts = new List<SourceLayout>();
            var usedFiles = new HashSet<NzbFile>(ReferenceEqualityComparer.Instance);
            var complete = true;
            var capped = false;
            for (var fileIndex = 0; fileIndex < set.Main.FileIds.Count; fileIndex++)
            {
                var key = Convert.ToHexString(set.Main.FileIds[fileIndex]);
                var descriptor = set.FileDescsById[key];
                var checksums = set.IfscsByFileId[key];
                SourceLayout? match = null;
                var ordered = sourceCandidates
                    .OrderByDescending(file => string.Equals(file.GetSubjectFileName(), Path.GetFileName(descriptor.FileName), StringComparison.OrdinalIgnoreCase))
                    .ThenBy(file => file.Segments[0].MessageId, StringComparer.Ordinal).ToArray();
                // Slice proofs are costly: unreadable prefixes try them only when nothing proved a match,
                // and mismatched (possibly corrupt) prefixes only after that also failed.
                for (var pass = 0; pass < 3 && match is null && !capped; pass++)
                {
                    foreach (var candidate in ordered)
                    {
                        SourceLayout? layout;
                        try
                        {
                            layout = await TryResolveVolumeAsync(candidate, descriptor, checksums, fileIndex, set, payload,
                                proofs.GetValueOrDefault(candidate), pass, reads, ct).ConfigureAwait(false);
                        }
                        catch (RepairInfeasibleException exception) when (reads.SourceSliceCapExceeded)
                        {
                            // Another set may use larger slices; only this set is proven infeasible.
                            reads.SourceSliceCapExceeded = false;
                            reads.RejectionReason = exception.Message;
                            complete = false;
                            capped = true;
                            break;
                        }
                        if (layout is null) continue;
                        if (match is not null)
                            throw new RepairInfeasibleException($"PAR2 volume '{descriptor.FileName}' has ambiguous NZB identity.");
                        match = layout;
                    }
                }
                if (capped) break;
                if (match is null) { complete = false; continue; }
                if (!usedFiles.Add(match.File))
                    throw new RepairInfeasibleException("One posted volume matches multiple recoverable PAR2 files.");
                layouts.Add(match);
            }
            coveredOwners.UnionWith(usedFiles.Where(requestedOwners.Contains));
            if (complete && requestedOwners.IsSubsetOf(usedFiles))
                return (set, layouts);
            reads.Sets[set.RecoverySetId].Retire();
        }

        if (requestedOwners.Count > 1 && requestedOwners.IsSubsetOf(coveredOwners))
            throw new RepairInfeasibleException("Missing segments span multiple PAR2 recovery sets; cross-set repair is not supported.");
        throw new RepairInfeasibleException(reads.RejectionReason ?? "No matching PAR2 recovery set found in the NZB.");
    }

    private async Task<SourceLayout?> TryResolveVolumeAsync(NzbFile file, FileDesc descriptor, IfscPacket checksums,
        int fileIndex, Par2SetContext set, RepairPayload payload, Par2FileProof? proof, int pass,
        RepairReadContext reads, CancellationToken ct)
    {
        var length = checked((long)descriptor.FileLength);
        bool prefixProven;
        if (proof is not null)
        {
            if (proof.FileLength != length || !proof.FileId.AsSpan().SequenceEqual(descriptor.FileID)) return null;
            prefixProven = true;
        }
        else
        {
            var observation = await ObserveVolumeAsync(file, payload, set.Main.SliceSize, reads, ct).ConfigureAwait(false);
            if (observation.Length != length) return null;
            prefixProven = observation.First16KHash == Convert.ToHexString(descriptor.File16kHash);
            // A mismatched prefix may be readable corruption, so it is deferred to the last slice-proof pass.
            var deferPass = prefixProven ? 0 : observation.First16KHash is null ? 1 : 2;
            if (pass != deferPass) return null;
        }
        if (!prefixProven && pass == 0) return null;
        var ids = file.GetSegmentIds();
        LongRange[] ranges;
        try
        {
            ranges = payload.PlainFile is { SegmentByteRangesTrusted: true } plain
                     && ids.SequenceEqual(plain.SegmentIds)
                ? BuildSegmentRanges(plain, ids.Length, length)
                : await ResolveVolumeRangesAsync(file, payload, length, reads, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            reads.RejectionReason = $"Cannot resolve volume '{descriptor.FileName}': {exception.Message}";
            return null;
        }
        if (!Par2FileSliceMap.TryCreate(length, GlobalSliceOffset(fileIndex, set.Main, set.IfscsByFileId),
                (int)set.Main.SliceSize, checksums.Slices.Count, ranges, out var map, out _) || map is null)
            return null;
        var layout = new SourceLayout(fileIndex, file, descriptor, checksums, map, ids, ids.Any(payload.SegmentIds.Contains));
        if (prefixProven) return layout;

        var proven = 0;
        var mismatched = 0;
        foreach (var local in Enumerable.Range(0, map.SliceCount).OrderBy(index => index == 0 ? 0 : index == map.SliceCount - 1 ? 1 : 2))
        {
            var bytes = await ReadIdentitySliceAsync(layout, map.GlobalSliceBase + local, reads, ct).ConfigureAwait(false);
            if (bytes is null) continue;
            if (Par2Reconstructor.VerifySliceChecksum(bytes, checksums.Slices[local]))
            {
                if (++proven == 2) break;
            }
            // Beyond the missing-slice cap the volume is unrepairable even if it is the right one.
            else if (++mismatched > reads.MaxMissingSlices)
            {
                reads.RejectionReason = $"Volume candidate for '{descriptor.FileName}' fails more than {reads.MaxMissingSlices} PAR2 slice checksums.";
                return null;
            }
        }
        return proven > 0 ? layout : null;
    }

    private async Task<NzbFileObservation> ObserveVolumeAsync(NzbFile file, RepairPayload payload, ulong sliceSize,
        RepairReadContext reads, CancellationToken ct)
    {
        if (reads.Observations.TryGetValue(file, out var cached)) return cached;
        reads.Budget.Charge(256);
        if (await ReadVolumePrefixAsync(file, reads, ct).ConfigureAwait(false) is { } read)
        {
            var observed = new NzbFileObservation(read.FileLength, read.Hash);
            reads.Observations[file] = observed;
            return observed;
        }
        var indices = Enumerable.Range(0, file.Segments.Count)
            .OrderBy(index => index == 0 ? 0 : index == file.Segments.Count - 1 ? 1 : 2)
            .ToArray();
        var concurrency = Math.Max(1, Math.Min(reads.FetchGate.CurrentCount, indices.Length));
        foreach (var window in indices.Chunk(concurrency))
        {
            ct.ThrowIfCancellationRequested();
            var tasks = new Task<HeaderProbeResult>[window.Length];
            for (var offset = 0; offset < window.Length; offset++)
            {
                var index = window[offset];
                var id = file.Segments[index].MessageId;
                if (reads.UnavailableIds.Contains(id))
                {
                    tasks[offset] = Task.FromResult(new HeaderProbeResult(id, null));
                    continue;
                }
                if (reads.Headers.TryGetValue(id, out var cachedHeader))
                {
                    tasks[offset] = Task.FromResult(new HeaderProbeResult(id, cachedHeader));
                    continue;
                }
                reads.AdmitIdentityWork(0, request: true);
                reads.Budget.Charge(512 + 2L * id.Length);
            }

            for (var offset = 0; offset < window.Length; offset++)
            {
                var index = window[offset];
                var id = file.Segments[index].MessageId;
                if (tasks[offset] is null)
                    tasks[offset] = FetchHeaderObservationAsync(id, reads, ct);
            }
            var probes = await Task.WhenAll(tasks).ConfigureAwait(false);

            foreach (var probe in probes)
            {
                if (probe.Header is not null)
                    reads.Headers[probe.Id] = probe.Header;
                else if (probe.IsUnavailable)
                    reads.NoteUnavailable(probe.Id, probe.IsMissing
                        ? new UsenetArticleNotFoundException(probe.Id)
                        : new InvalidDataException("PAR2 identity header probe failed."));
                if (probe.Header is null && reads.UnavailableIds.Contains(probe.Id))
                    reads.NoteSourceMiss(file,
                        payload.TrustedRanges.TryGetValue(probe.Id, out var range) ? range : null, checked((long)sliceSize));
                if (probe.Header is not { FileSize: > 0 }) continue;
                var observation = new NzbFileObservation(probe.Header.FileSize, null);
                reads.Observations[file] = observation;
                return observation;
            }
        }
        var unavailable = new NzbFileObservation(null, null);
        reads.Observations[file] = unavailable;
        return unavailable;
    }

    private async Task<HeaderProbeResult> FetchHeaderObservationAsync(string id, RepairReadContext reads, CancellationToken ct)
    {
        await reads.FetchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var response = await _usenetClient.DecodedBodyAsync(id, ct).ConfigureAwait(false);
            await using var stream = response.Stream!;
            var header = await stream.GetYencHeadersAsync(ct).ConfigureAwait(false);
            HealthCheckActivity.Report();
            return new HeaderProbeResult(id, header);
        }
        catch (Exception exception) when (exception is UsenetArticleNotFoundException or UsenetCorruptArticleException or InvalidDataException or EndOfStreamException)
        {
            HealthCheckActivity.Report();
            return new HeaderProbeResult(id, null, true, exception is UsenetArticleNotFoundException);
        }
        finally { reads.FetchGate.Release(); }
    }

    private sealed record HeaderProbeResult(
        string Id,
        UsenetYencHeader? Header,
        bool IsUnavailable = false,
        bool IsMissing = false);

    private async Task<LongRange[]> ResolveVolumeRangesAsync(NzbFile file, RepairPayload payload, long length,
        RepairReadContext reads, CancellationToken ct)
    {
        if (reads.VolumeRanges.TryGetValue(file, out var cached)) return cached;
        reads.Budget.Charge(checked(256L + file.Segments.Count * 192L));
        var evidence = new LongRange?[file.Segments.Count];
        var indexed = file.GetSegmentByteRangeIndex();
        for (var index = 0; index < evidence.Length; index++)
        {
            if (payload.TrustedRanges.TryGetValue(file.Segments[index].MessageId, out var trusted))
                evidence[index] = trusted;
            if (indexed.IsTrusted && indexed.Ranges is { } indexedRanges)
                Merge(index, indexedRanges[index]);
        }
        await reads.FetchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var index = 0; index < evidence.Length; index++)
            {
                var id = file.Segments[index].MessageId;
                if (evidence[index] is not null && !reads.Headers.ContainsKey(id)) continue;
                var header = await ReadHeaderCoreAsync(id, reads, ct, identity: true).ConfigureAwait(false);
                if (header is null) continue;
                if (header.FileSize != length || header.TotalParts != evidence.Length || header.PartNumber != index + 1
                    || header.PartOffset < 0 || header.PartSize <= 0)
                    throw new InvalidDataException("yEnc length, part count, or article order conflicts with the posted volume.");
                Merge(index, LongRange.FromStartAndSize(header.PartOffset, header.PartSize));
            }
        }
        finally { reads.FetchGate.Release(); }
        var ranges = CompleteVolumeRanges(length, evidence);
        reads.VolumeRanges[file] = ranges;
        return ranges;

        void Merge(int index, LongRange range)
        {
            if (evidence[index] is { } previous && previous != range)
                throw new InvalidDataException("Exact range evidence conflicts for a posted article.");
            evidence[index] = range;
        }
    }

#pragma warning disable CA5351
    // Null when the first article yields no yEnc size; a null Hash means the 16 KiB prefix is unreadable.
    private async Task<(long FileLength, string? Hash)?> ReadVolumePrefixAsync(NzbFile file, RepairReadContext reads, CancellationToken ct)
    {
        const int prefixLimit = 16 * 1024;
        using var reservation = reads.Budget.Reserve(prefixLimit + 1024L);
        byte[]? prefix = null;
        long fileLength = 0;
        var offset = 0;
        await reads.FetchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var index = 0; index < file.Segments.Count && (prefix is null || offset < prefix.Length); index++)
            {
                var id = file.Segments[index].MessageId;
                if (reads.UnavailableIds.Contains(id)) break;
                reads.AdmitIdentityWork(0, request: true);
                reads.Budget.Charge(512 + 2L * id.Length);
                try
                {
                    var response = await _usenetClient.DecodedBodyAsync(id, ct).ConfigureAwait(false);
                    await using var stream = response.Stream!;
                    var header = await stream.GetYencHeadersAsync(ct).ConfigureAwait(false);
                    HealthCheckActivity.Report();
                    if (header is null) break;
                    reads.Headers[id] = header;
                    if (prefix is null)
                    {
                        if (header.FileSize <= 0) return null;
                        fileLength = header.FileSize;
                        prefix = new byte[(int)Math.Min(prefixLimit, fileLength)];
                        reads.AdmitIdentityWork(prefix.Length);
                    }
                    if (header.FileSize != fileLength || header.PartOffset != offset || header.PartSize <= 0) break;
                    var count = (int)Math.Min(header.PartSize, prefix.Length - offset);
                    await using var counted = new RepairCountingStream(stream, bytes => reads.ReadBytes(bytes));
                    await counted.ReadExactlyAsync(prefix.AsMemory(offset, count), ct).ConfigureAwait(false);
                    offset += count;
                }
                catch (Exception exception) when (IsUnavailableArticle(exception))
                {
                    HealthCheckActivity.Report();
                    reads.NoteUnavailable(id, exception);
                    break;
                }
            }
        }
        finally { reads.FetchGate.Release(); }
        if (prefix is null) return null;
        return (fileLength, offset == prefix.Length ? Convert.ToHexString(MD5.HashData(prefix)) : null);
    }
#pragma warning restore CA5351

    private async Task<byte[]?> ReadIdentitySliceAsync(SourceLayout layout, int globalSlice, RepairReadContext reads, CancellationToken ct)
    {
        var segmentIndices = layout.Map.SegmentIndicesForGlobalSlice(globalSlice).ToArray();
        if (segmentIndices.Any(index => reads.UnavailableIds.Contains(layout.SegmentIds[index]))) return null;
        reads.AdmitIdentityWork(layout.Map.SliceSize);
        using var reservation = reads.Budget.Reserve(layout.Map.SliceSize + 1024L);
        var buffer = new byte[layout.Map.SliceSize];
        var slice = layout.Map.SliceFileRange(globalSlice);
        await reads.FetchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var index in segmentIndices)
            {
                var range = layout.Map.SegmentRanges[index];
                var body = await ReadIdentityBodyCoreAsync(layout, index, reads, ct).ConfigureAwait(false);
                if (body is null) return null;
                var start = Math.Max(slice.StartInclusive, range.StartInclusive);
                var end = Math.Min(slice.EndExclusive, range.EndExclusive);
                body.AsSpan((int)(start - range.StartInclusive), (int)(end - start)).CopyTo(buffer.AsSpan((int)(start - slice.StartInclusive)));
            }
            return buffer;
        }
        finally { reads.FetchGate.Release(); }
    }

    private async Task<byte[]?> ReadIdentityBodyCoreAsync(SourceLayout layout, int index, RepairReadContext reads, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var id = layout.SegmentIds[index];
        if (reads.UnavailableIds.Contains(id)) return null;
        var length = layout.Map.SegmentRanges[index].Count;
        if (reads.IdentityBodyId == id && reads.IdentityBody?.LongLength == length) return reads.IdentityBody;
        reads.ClearIdentityBody();
        reads.AdmitIdentityWork(checked(length + 1), request: true);
        reads.IdentityBodyReservation.TakeOwnership(() => reads.Budget.Reserve(length + 1024));
        try
        {
            var body = new byte[checked((int)length)];
            var response = await _usenetClient.DecodedBodyAsync(id, ct).ConfigureAwait(false);
            await using var stream = response.Stream!;
            await using var counted = new RepairCountingStream(stream, bytes => reads.ReadBytes(bytes));
            await counted.ReadExactlyAsync(body, ct).ConfigureAwait(false);
            var extra = new byte[1];
            if (await counted.ReadAsync(extra, ct).ConfigureAwait(false) != 0)
                throw new InvalidDataException("PAR2 identity article exceeds its validated volume range.");
            reads.IdentityBodyId = id;
            reads.IdentityBody = body;
            return body;
        }
        catch (Exception exception) when (IsUnavailableArticle(exception))
        {
            reads.ClearIdentityBody();
            reads.NoteUnavailable(id, exception);
            return null;
        }
    }

    private static bool IsUnavailableArticle(Exception exception)
        => exception is InvalidDataException or EndOfStreamException
           || exception.TryGetCausingException<UsenetArticleNotFoundException>(out _)
           || exception.TryGetCausingException<UsenetCorruptArticleException>(out _);

    internal static LongRange[] CompleteVolumeRanges(long fileLength, LongRange?[] evidence)
    {
        if (fileLength <= 0 || evidence.Length == 0)
            throw new InvalidDataException("Posted volume length and segment count must be positive.");

        var ranges = new LongRange[evidence.Length];
        long offset = 0;
        for (var index = 0; index < evidence.Length; index++)
        {
            var range = evidence[index];
            if (range is null)
            {
                if (index + 1 < evidence.Length && evidence[index + 1] is null)
                    throw new InvalidDataException("Adjacent unavailable articles have ambiguous volume boundaries.");
                var end = index + 1 == evidence.Length ? fileLength : evidence[index + 1]!.StartInclusive;
                range = LongRange.FromStartAndSize(offset, checked(end - offset));
            }

            if (range.StartInclusive != offset || range.Count is <= 0 or > int.MaxValue
                || range.EndExclusive > fileLength)
                throw new InvalidDataException("Posted volume article ranges must provide exact contiguous coverage.");
            ranges[index] = range;
            offset = range.EndExclusive;
        }

        if (offset != fileLength)
            throw new InvalidDataException("Posted volume article ranges do not cover its exact length.");
        return ranges;
    }
}