using System.Runtime.CompilerServices;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Par2Recovery.Packets;
using NzbWebDAV.Par2Recovery.ReedSolomon;
using NzbWebDAV.Services.Observability;
using NzbWebDAV.Streams;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Services.Repair;

public partial class Par2RepairService
{
    internal const int MaxPar2MagicCandidates = 64;
    internal const int MaxPar2MetadataCandidates = 128;
    private const long MaxPar2MetadataScanBytes = 512L * 1024 * 1024;

    private sealed class RepairReadContext(long memoryLimit, int concurrency, Action<long> onRead) : IDisposable
    {
        public Par2MemoryBudget Budget { get; } = new(memoryLimit);
        public SemaphoreSlim FetchGate { get; } = new(concurrency, concurrency);
        public Dictionary<NzbFile, NzbFileObservation> Observations { get; } = new(ReferenceEqualityComparer.Instance);
        public Dictionary<string, UsenetYencHeader?> Headers { get; } = new(StringComparer.Ordinal);
        public HashSet<string> UnavailableIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> MissingIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> CorruptIds { get; } = new(StringComparer.Ordinal);
        public Dictionary<NzbFile, LongRange[]> VolumeRanges { get; } = new(ReferenceEqualityComparer.Instance);
        public HashSet<NzbFile> ParityFiles { get; } = new(ReferenceEqualityComparer.Instance);
        public Dictionary<string, MetadataCandidate> Sets { get; } = new(StringComparer.Ordinal);
        public List<RecvSlic> RecoveryPackets { get; } = [];
        public long BytesRead { get; private set; }
        public long MetadataBytesRead { get; private set; }
        public int CandidateCount { get; set; }
        public int MagicCount { get; set; }
        public string? RejectionReason { get; set; }

        public void NoteUnavailable(string id, Exception exception)
        {
            UnavailableIds.Add(id);
            if (exception.TryGetCausingException<UsenetArticleNotFoundException>(out _)) MissingIds.Add(id);
            else CorruptIds.Add(id);
        }

        public void ReadBytes(long bytes, bool metadata = false)
        {
            BytesRead = checked(BytesRead + bytes);
            onRead(bytes);
            if (metadata)
            {
                MetadataBytesRead = checked(MetadataBytesRead + bytes);
                if (MetadataBytesRead > MaxPar2MetadataScanBytes)
                    throw new RepairInfeasibleException("PAR2 metadata scan exceeds the 512 MiB byte limit.");
            }
        }

        public void Dispose()
        {
            foreach (var candidate in Sets.Values) candidate.Release();
            foreach (var packet in RecoveryPackets) packet.ReleaseMemory();
            FetchGate.Dispose();
        }
    }

    private sealed class MetadataCandidate(string setId)
    {
        public string SetId { get; } = setId;
        public MainPacket? Main { get; private set; }
        public Dictionary<string, FileDesc> Descriptors { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IfscPacket> Checksums { get; } = new(StringComparer.Ordinal);
        public bool Invalid { get; set; }

        public bool Add(Par2Packet packet)
        {
            var accepted = packet switch
            {
                MainPacket main => AddMain(main),
                FileDesc descriptor => AddPacket(Descriptors, Convert.ToHexString(descriptor.FileID), descriptor),
                IfscPacket checksum => AddPacket(Checksums, Convert.ToHexString(checksum.FileId), checksum),
                _ => false,
            };
            if (!accepted) packet.ReleaseMemory();
            return accepted;
        }

        private bool AddMain(MainPacket main)
        {
            if (Main is null) { Main = main; return true; }
            CheckDuplicate(Main, main);
            return false;
        }

        private static bool AddPacket<TPacket>(Dictionary<string, TPacket> packets, string key, TPacket packet) where TPacket : Par2Packet
        {
            if (packets.TryAdd(key, packet)) return true;
            CheckDuplicate(packets[key], packet);
            return false;
        }

        private static void CheckDuplicate(Par2Packet previous, Par2Packet next)
        {
            if (previous.Header.PacketHash.AsSpan().SequenceEqual(next.Header.PacketHash)) return;
            next.ReleaseMemory();
            throw new InvalidDataException("Conflicting critical packets share a PAR2 recovery set and packet identity.");
        }

        public void Release()
        {
            Main?.ReleaseMemory();
            foreach (var packet in Descriptors.Values) packet.ReleaseMemory();
            foreach (var packet in Checksums.Values) packet.ReleaseMemory();
        }
    }

    private async IAsyncEnumerable<Par2SetContext> DiscoverPar2SetsAsync(
        NzbDocument document, RepairReadContext reads, [EnumeratorCancellation] CancellationToken ct)
    {
        var named = document.Files.Where(IsPar2CandidateSubject)
            .OrderBy(file => Par2.ParVolume.IsMatch(file.GetSubjectFileName()))
            .ThenBy(file => file.Segments.Count)
            .ThenBy(file => file.Segments.FirstOrDefault()?.MessageId, StringComparer.Ordinal).ToList();
        reads.ParityFiles.UnionWith(named);
        foreach (var file in named)
        {
            var context = await ParseMetadataCandidateAsync(file, reads, ct).ConfigureAwait(false);
            if (context is not null) yield return context;
        }

        foreach (var file in document.Files.Where(file => !reads.ParityFiles.Contains(file))
                     .OrderBy(file => file.Segments.Count)
                     .ThenBy(file => file.Segments.FirstOrDefault()?.MessageId, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (reads.MagicCount >= MaxPar2MagicCandidates)
                throw new RepairInfeasibleException($"PAR2 magic search exhausted its {MaxPar2MagicCandidates}-candidate limit.");
            reads.MagicCount++;
            if (!await SniffPar2MagicAsync(file, reads, ct).ConfigureAwait(false)) continue;
            reads.ParityFiles.Add(file);
            var context = await ParseMetadataCandidateAsync(file, reads, ct).ConfigureAwait(false);
            if (context is not null) yield return context;
        }
    }

    private async Task<Par2SetContext?> ParseMetadataCandidateAsync(NzbFile file, RepairReadContext reads, CancellationToken ct)
    {
        if (++reads.CandidateCount > MaxPar2MetadataCandidates)
            throw new RepairInfeasibleException($"PAR2 metadata search exhausted its {MaxPar2MetadataCandidates}-candidate limit.");
        if (file.Segments.Count == 0) return null;
        MetadataCandidate? candidate = null;
        await reads.FetchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var header = await ReadHeaderCoreAsync(file.Segments[0].MessageId, reads, ct).ConfigureAwait(false)
                ?? await ReadHeaderCoreAsync(file.Segments[^1].MessageId, reads, ct).ConfigureAwait(false);
            if (header is null) return null;
            if (header.FileSize <= 0 || header.FileSize > MaxPar2MetadataScanBytes - reads.MetadataBytesRead)
                throw new RepairInfeasibleException("PAR2 metadata candidate exceeds the remaining scan byte limit.");
            await using var stream = new NzbFileStream(file.GetSegmentIds(), header.FileSize, _usenetClient, 0,
                usePipelinedBodyRequests: false, readStartWarmupEnabled: false);
            await using var counted = new RepairCountingStream(stream, bytes => reads.ReadBytes(bytes, metadata: true));
            while (stream.Position < stream.Length)
            {
                var packet = await Par2RepairReader.ReadVerifiedPacketAsync(counted,
                    new Par2RepairReader.ReadOptions(reads.Budget, false), ct).ConfigureAwait(false);
                var setId = Convert.ToHexString(packet.Header.RecoverySetID);
                if (candidate is not null && candidate.SetId != setId)
                {
                    packet.ReleaseMemory();
                    throw new InvalidDataException("A PAR2 metadata candidate contains mixed recovery-set IDs.");
                }
                if (candidate is null)
                {
                    if (!reads.Sets.TryGetValue(setId, out candidate))
                    {
                        reads.Budget.Charge(1024);
                        candidate = new MetadataCandidate(setId);
                        reads.Sets.Add(setId, candidate);
                    }
                    if (candidate.Invalid) { packet.ReleaseMemory(); return null; }
                }
                candidate.Add(packet);
            }
            if (candidate is null || candidate.Main is not { } main) return null;
            long releaseBytes = 0;
            var slices = 0;
            foreach (var id in main.FileIds)
            {
                var key = Convert.ToHexString(id);
                if (!candidate.Descriptors.TryGetValue(key, out var descriptor)
                    || !candidate.Checksums.TryGetValue(key, out var checksum)) return null;
                var length = checked((long)descriptor.FileLength);
                var expected = length / (long)main.SliceSize + (length % (long)main.SliceSize == 0 ? 0 : 1);
                if (expected != checksum.Slices.Count)
                    throw new InvalidDataException($"Slice checksums do not cover volume '{descriptor.FileName}'.");
                releaseBytes = checked(releaseBytes + length);
                slices = checked(slices + checksum.Slices.Count);
            }
            if (slices is <= 0 or > Gf16Field.MaxInputSlices)
                throw new InvalidDataException("PAR2 recovery set must contain 1 to 32,768 input slices.");
            if (releaseBytes > _configManager.GetPar2MaxReleaseGb() * 1024L * 1024 * 1024)
            {
                reads.RejectionReason = $"Recovery set size {releaseBytes} bytes exceeds release cap.";
                return null;
            }
            return new Par2SetContext(main, candidate.Descriptors, candidate.Checksums, candidate.SetId);
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or UsenetArticleNotFoundException or UsenetCorruptArticleException)
        {
            if (candidate is not null) candidate.Invalid = true;
            reads.RejectionReason = exception.Message;
            PrometheusMetrics.Current?.RecordPar2ValidationFailure("packet");
            Log.Warning("PAR2 metadata candidate rejected. Reason: {Reason}", exception.Message);
            return null;
        }
        finally { reads.FetchGate.Release(); }
    }

    private async Task<UsenetYencHeader?> ReadHeaderCoreAsync(string id, RepairReadContext reads, CancellationToken ct)
    {
        if (reads.UnavailableIds.Contains(id)) return null;
        if (reads.Headers.TryGetValue(id, out var cached)) return cached;
        reads.Budget.Charge(512 + 2L * id.Length);
        try
        {
            var response = await _usenetClient.DecodedBodyAsync(id, ct).ConfigureAwait(false);
            await using var stream = response.Stream!;
            var header = await stream.GetYencHeadersAsync(ct).ConfigureAwait(false);
            reads.Headers[id] = header;
            return header;
        }
        catch (Exception exception) when (exception is UsenetArticleNotFoundException or UsenetCorruptArticleException or InvalidDataException or EndOfStreamException)
        {
            reads.Headers[id] = null;
            return null;
        }
    }

    private async Task<bool> SniffPar2MagicAsync(NzbFile file, RepairReadContext reads, CancellationToken ct)
    {
        if (file.Segments.Count == 0) return false;
        await reads.FetchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var response = await _usenetClient.DecodedBodyAsync(file.Segments[0].MessageId, ct).ConfigureAwait(false);
            await using var stream = response.Stream!;
            var buffer = new byte[64];
            var count = 0;
            while (count < 8)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(count, buffer.Length - count), ct).ConfigureAwait(false);
                reads.ReadBytes(read, metadata: true);
                if (read == 0) return false;
                count += read;
            }
            return buffer.AsSpan(0, 8).SequenceEqual("PAR2\0PKT"u8);
        }
        catch (Exception exception) when (exception is UsenetArticleNotFoundException or UsenetCorruptArticleException or InvalidDataException or EndOfStreamException)
        { return false; }
        finally { reads.FetchGate.Release(); }
    }

    private async Task<List<Par2Reconstructor.RecoverySlice>> CollectMatchingRecoverySlicesAsync(
        NzbDocument document, Par2SetContext set, int needed, RepairReadContext reads, CancellationToken ct)
    {
        var byExponent = new Dictionary<uint, RecvSlic>();
        var candidates = reads.ParityFiles.OrderBy(file => file.Segments.Count)
            .ThenBy(file => file.Segments.FirstOrDefault()?.MessageId, StringComparer.Ordinal).ToList();
        foreach (var file in candidates)
        {
            await ReadRecoveryAsync(file).ConfigureAwait(false);
            if (byExponent.Count >= needed) return Result();
        }
        foreach (var file in document.Files.Where(file => !reads.ParityFiles.Contains(file))
                     .OrderBy(file => file.Segments.Count)
                     .ThenBy(file => file.Segments.FirstOrDefault()?.MessageId, StringComparer.Ordinal))
        {
            if (++reads.MagicCount > MaxPar2MagicCandidates)
                throw new RepairInfeasibleException("PAR2 recovery search exhausted its 64-candidate magic limit.");
            if (!await SniffPar2MagicAsync(file, reads, ct).ConfigureAwait(false)) continue;
            reads.ParityFiles.Add(file);
            await ReadRecoveryAsync(file).ConfigureAwait(false);
            if (byExponent.Count >= needed) break;
        }
        return Result();

        List<Par2Reconstructor.RecoverySlice> Result()
            => byExponent.OrderBy(pair => pair.Key).Select(pair => new Par2Reconstructor.RecoverySlice(pair.Key, pair.Value.Payload)).ToList();

        async Task ReadRecoveryAsync(NzbFile file)
        {
            if (file.Segments.Count == 0) return;
            await reads.FetchGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var header = await ReadHeaderCoreAsync(file.Segments[0].MessageId, reads, ct).ConfigureAwait(false)
                    ?? await ReadHeaderCoreAsync(file.Segments[^1].MessageId, reads, ct).ConfigureAwait(false);
                if (header is not { FileSize: > 0 }) return;
                await using var stream = new NzbFileStream(file.GetSegmentIds(), header.FileSize, _usenetClient, 0,
                    usePipelinedBodyRequests: false, readStartWarmupEnabled: false);
                await using var counted = new RepairCountingStream(stream, bytes => reads.ReadBytes(bytes));
                while (stream.Position < stream.Length)
                {
                    var packet = await Par2RepairReader.ReadVerifiedPacketAsync(counted,
                        new Par2RepairReader.ReadOptions(reads.Budget, true, set.RecoverySetId, (int)set.Main.SliceSize), ct).ConfigureAwait(false);
                    var retained = false;
                    try
                    {
                        if (Convert.ToHexString(packet.Header.RecoverySetID) != set.RecoverySetId) continue;
                        if (packet is not RecvSlic recovery)
                        {
                            reads.Sets[set.RecoverySetId].Add(packet);
                            retained = true;
                            continue;
                        }
                        if (byExponent.TryGetValue(recovery.Exponent, out var previous))
                        {
                            if (!previous.Payload.AsSpan().SequenceEqual(recovery.Payload))
                                throw new InvalidDataException("Conflicting recovery payloads share a PAR2 exponent.");
                        }
                        else if (byExponent.Count < needed)
                        {
                            byExponent.Add(recovery.Exponent, recovery);
                            reads.RecoveryPackets.Add(recovery);
                            retained = true;
                        }
                    }
                    finally { if (!retained) packet.ReleaseMemory(); }
                }
            }
            catch (Exception exception) when (exception is UsenetArticleNotFoundException or UsenetCorruptArticleException or EndOfStreamException)
            {
                Log.Warning("PAR2 recovery volume unavailable. Reason: {Reason}", exception.Message);
            }
            finally { reads.FetchGate.Release(); }
        }
    }

    private sealed class RepairCountingStream(Stream inner, Action<long> onRead) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            onRead(read);
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            onRead(read);
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed record NzbFileObservation(long? Length, string? First16KHash);
    private sealed class RepairInfeasibleException(string message) : Exception(message);
}