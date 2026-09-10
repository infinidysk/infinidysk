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
        Dictionary<string, LongRange> TrustedRanges);

    private sealed record SourceLayout(int FileIndex, NzbFile File, FileDesc Descriptor,
        IfscPacket Checksums, Par2FileSliceMap Map, string[] SegmentIds, bool PayloadOwned);

    private async Task<RepairPayload> LoadRepairPayloadAsync(DavItem item, RepairReadContext reads, CancellationToken ct)
    {
        await using var context = CreateContext();
        var client = new DavDatabaseClient(context);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var ranges = new Dictionary<string, LongRange>(StringComparer.Ordinal);
        DavNzbFile? plain = null;
        switch (item.SubType)
        {
            case DavItem.ItemSubType.NzbFile:
                plain = await client.GetDavNzbFileAsync(item, ct).ConfigureAwait(false)
                    ?? throw new MissingFilePayloadException(item, item.SubType);
                AddPart(plain.SegmentIds, plain.SegmentByteRanges, plain.SegmentByteRangesTrusted);
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
                    AddPart(part.SegmentIds, part.SegmentByteRanges, part.SegmentByteRangesTrusted);
                foreach (var part in multipart.Metadata.PendingParts)
                    AddPart(part.SegmentIds, null, false);
                break;
            default:
                throw new RepairInfeasibleException("This item has no supported Usenet streaming payload.");
        }

        if (ids.Count == 0) throw new MissingFilePayloadException(item, item.SubType);
        return new RepairPayload(plain, ids, ranges);

        void AddPart(string[] segmentIds, LongRange[]? segmentRanges, bool? trusted)
        {
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

        var coveredOwners = new HashSet<NzbFile>(ReferenceEqualityComparer.Instance);
        await foreach (var set in DiscoverPar2SetsAsync(document, reads, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var sourceCandidates = document.Files.Where(file => !reads.ParityFiles.Contains(file) && file.Segments.Count > 0).ToArray();
            reads.AdmitSourceComparisons(sourceCandidates.Length, set.Main.FileIds.Count);
            var layouts = new List<SourceLayout>();
            var usedFiles = new HashSet<NzbFile>(ReferenceEqualityComparer.Instance);
            var complete = true;
            for (var fileIndex = 0; fileIndex < set.Main.FileIds.Count; fileIndex++)
            {
                var key = Convert.ToHexString(set.Main.FileIds[fileIndex]);
                var descriptor = set.FileDescsById[key];
                var checksums = set.IfscsByFileId[key];
                SourceLayout? match = null;
                foreach (var candidate in sourceCandidates
                             .OrderByDescending(file => string.Equals(file.GetSubjectFileName(), Path.GetFileName(descriptor.FileName), StringComparison.OrdinalIgnoreCase))
                             .ThenBy(file => file.Segments[0].MessageId, StringComparer.Ordinal))
                {
                    var layout = await TryResolveVolumeAsync(candidate, descriptor, checksums, fileIndex, set, payload, reads, ct)
                        .ConfigureAwait(false);
                    if (layout is null) continue;
                    if (match is not null)
                        throw new RepairInfeasibleException($"PAR2 volume '{descriptor.FileName}' has ambiguous NZB identity.");
                    match = layout;
                }
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
        int fileIndex, Par2SetContext set, RepairPayload payload, RepairReadContext reads, CancellationToken ct)
    {
        var observation = await ObserveVolumeAsync(file, reads, ct).ConfigureAwait(false);
        var length = checked((long)descriptor.FileLength);
        if (observation.Length != length) return null;
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

        if (!observation.PrefixAttempted)
        {
            var prefix = await ReadVolumePrefixHashAsync(layout, reads, ct).ConfigureAwait(false);
            observation = observation with { First16KHash = prefix, PrefixAttempted = true };
            reads.Observations[file] = observation;
        }
        if (observation.First16KHash == Convert.ToHexString(descriptor.File16kHash)) return layout;

        var proven = 0;
        foreach (var local in Enumerable.Range(0, map.SliceCount).OrderBy(index => index == 0 ? 0 : index == map.SliceCount - 1 ? 1 : 2))
        {
            var bytes = await ReadIdentitySliceAsync(layout, map.GlobalSliceBase + local, reads, ct).ConfigureAwait(false);
            if (bytes is null || !Par2Reconstructor.VerifySliceChecksum(bytes, checksums.Slices[local])) continue;
            if (++proven == 2) break;
        }
        return proven > 0 ? layout : null;
    }

    private async Task<NzbFileObservation> ObserveVolumeAsync(NzbFile file, RepairReadContext reads, CancellationToken ct)
    {
        if (reads.Observations.TryGetValue(file, out var cached)) return cached;
        reads.Budget.Charge(256);
        var indices = Enumerable.Range(0, file.Segments.Count)
            .OrderBy(index => index == 0 ? 0 : index == file.Segments.Count - 1 ? 1 : 2)
            .ToArray();
        var concurrency = Math.Max(1, Math.Min(reads.FetchGate.CurrentCount, indices.Length));
        foreach (var window in indices.Chunk(concurrency))
        {
            ct.ThrowIfCancellationRequested();
            var probes = new HeaderProbeResult[window.Length];
            var tasks = new Task<HeaderProbeResult>[window.Length];
            for (var offset = 0; offset < window.Length; offset++)
            {
                var index = window[offset];
                var id = file.Segments[index].MessageId;
                if (reads.UnavailableIds.Contains(id))
                {
                    tasks[offset] = Task.FromResult(new HeaderProbeResult(index, id, null, false, true));
                    continue;
                }
                if (reads.Headers.TryGetValue(id, out var cachedHeader))
                {
                    tasks[offset] = Task.FromResult(new HeaderProbeResult(index, id, cachedHeader, false, cachedHeader is null));
                    continue;
                }
                reads.AdmitIdentityWork(0, request: true);
                reads.Budget.Charge(512 + 2L * id.Length);
                tasks[offset] = FetchHeaderObservationAsync(index, id, reads, ct);
            }
            probes = await Task.WhenAll(tasks).ConfigureAwait(false);

            foreach (var probe in probes)
            {
                if (probe.Header is not null)
                    reads.Headers[probe.Id] = probe.Header;
                else if (probe.IsUnavailable)
                    reads.NoteUnavailable(probe.Id, probe.IsMissing
                        ? new UsenetArticleNotFoundException(probe.Id)
                        : new InvalidDataException("PAR2 identity header probe failed."));
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

    private async Task<HeaderProbeResult> FetchHeaderObservationAsync(int index, string id, RepairReadContext reads, CancellationToken ct)
    {
        await reads.FetchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var response = await _usenetClient.DecodedBodyAsync(id, ct).ConfigureAwait(false);
            await using var stream = response.Stream!;
            return new HeaderProbeResult(index, id, await stream.GetYencHeadersAsync(ct).ConfigureAwait(false), false, false);
        }
        catch (Exception exception) when (exception is UsenetArticleNotFoundException or UsenetCorruptArticleException or InvalidDataException or EndOfStreamException)
        {
            return new HeaderProbeResult(index, id, null, exception is UsenetArticleNotFoundException, true);
        }
        finally { reads.FetchGate.Release(); }
    }

    private sealed record HeaderProbeResult(int Index, string Id, UsenetYencHeader? Header, bool IsMissing, bool IsUnavailable);

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
    private async Task<string?> ReadVolumePrefixHashAsync(SourceLayout layout, RepairReadContext reads, CancellationToken ct)
    {
        var length = (int)Math.Min(16 * 1024, layout.Map.FileLength);
        if (layout.Map.SegmentRanges.Where(range => range.StartInclusive < length)
            .Select((_, index) => layout.SegmentIds[index]).Any(reads.UnavailableIds.Contains)) return null;
        reads.AdmitIdentityWork(length);
        using var reservation = reads.Budget.Reserve(length + 1024L);
        var prefix = new byte[length];
        var offset = 0;
        await reads.FetchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var index = 0; offset < length; index++)
            {
                var body = await ReadIdentityBodyCoreAsync(layout, index, reads, ct).ConfigureAwait(false);
                if (body is null) return null;
                var count = (int)Math.Min(layout.Map.SegmentRanges[index].Count, length - offset);
                body.AsSpan(0, count).CopyTo(prefix.AsSpan(offset));
                offset += count;
            }
            return Convert.ToHexString(MD5.HashData(prefix));
        }
        finally { reads.FetchGate.Release(); }
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