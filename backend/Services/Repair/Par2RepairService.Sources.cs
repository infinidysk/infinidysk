using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Services.Repair;

public partial class Par2RepairService
{
    private sealed class ResolvedSliceAccessor : IDisposable
    {
        private readonly IReadOnlyList<SourceLayout> _layouts;
        private readonly UsenetStreamingClient _client;
        private readonly RepairReadContext _reads;
        private readonly Func<int, bool> _isUnavailableSlice;
        private readonly Dictionary<int, (byte[] Bytes, IDisposable Reservation)> _bodies = new();
        private readonly Dictionary<int, HashSet<int>> _missing = new();
        private readonly Dictionary<int, HashSet<int>> _corrupt = new();
        private SourceLayout _layout;
        private long _cachedBodyBytes;
        private long _peakCachedBodyBytes;
        private long _retainedByteLimit;

        public ResolvedSliceAccessor(IReadOnlyList<SourceLayout> layouts, UsenetStreamingClient client,
            RepairReadContext reads, Func<int, bool> isUnavailableSlice)
        {
            _layouts = layouts;
            _layout = layouts[0];
            _client = client;
            _reads = reads;
            _isUnavailableSlice = isUnavailableSlice;
            _retainedByteLimit = reads.Budget.Limit;
            foreach (var layout in layouts)
            {
                _missing[layout.FileIndex] = [];
                _corrupt[layout.FileIndex] = [];
            }
        }

        public IReadOnlyCollection<int> MissingSegmentIndices => _missing[_layout.FileIndex];
        public IReadOnlyCollection<int> CorruptSegmentIndices => _corrupt[_layout.FileIndex];
        public long CachedBodyBytes => Interlocked.Read(ref _cachedBodyBytes);
        public long PeakCachedBodyBytes => Interlocked.Read(ref _peakCachedBodyBytes);
        public long RetainedByteLimit => Interlocked.Read(ref _retainedByteLimit);

        public void NoteMissing(int index) { _missing[_layout.FileIndex].Add(index); Evict(index); }
        public void NoteCorrupt(int index) { _corrupt[_layout.FileIndex].Add(index); Evict(index); }

        public void UseLayout(SourceLayout layout)
        {
            if (_layout.FileIndex == layout.FileIndex) return;
            BeginSequentialPass();
            _layout = layout;
        }

        public void BeginSequentialPass()
        {
            foreach (var body in _bodies.Values) body.Reservation.Dispose();
            _bodies.Clear();
            Interlocked.Exchange(ref _cachedBodyBytes, 0);
        }

        public void SetRetainedByteLimit(long limit)
        {
            if (limit <= 0) throw new Par2MemoryCapExceededException("PAR2 repair has no memory available for source segments.");
            Interlocked.Exchange(ref _retainedByteLimit, limit);
            if (CachedBodyBytes > limit) BeginSequentialPass();
        }

        public async Task<byte[]?> FetchSliceBytesAsync(int globalSlice, int sliceSize, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_isUnavailableSlice(globalSlice)) return null;
            var layout = FindLayout(globalSlice);
            if (layout is null) return null;
            UseLayout(layout);
            var range = layout.Map.SliceFileRange(globalSlice);
            foreach (var index in _bodies.Keys.Where(index => layout.Map.SegmentRanges[index].EndExclusive <= range.StartInclusive).ToArray())
                Evict(index);
            var bytes = new byte[sliceSize];
            long copied = 0;
            foreach (var index in layout.Map.SegmentIndicesForGlobalSlice(globalSlice))
            {
                var body = await GetBodyAsync(index, ct).ConfigureAwait(false);
                if (body is null) return null;
                var segment = layout.Map.SegmentRanges[index];
                var start = Math.Max(range.StartInclusive, segment.StartInclusive);
                var end = Math.Min(range.EndExclusive, segment.EndExclusive);
                var count = checked((int)(end - start));
                body.AsSpan((int)(start - segment.StartInclusive), count).CopyTo(bytes.AsSpan((int)(start - range.StartInclusive)));
                copied += count;
            }
            return copied == range.Count ? bytes : null;
        }

        public Task<byte[]?> GetSegmentBodyForPatchAsync(int index, CancellationToken ct)
            => _layout.Map.GlobalSlicesForSegment(index).All(_isUnavailableSlice)
                ? Task.FromResult<byte[]?>(null) : GetBodyAsync(index, ct);

        private SourceLayout? FindLayout(int globalSlice)
        {
            var lower = 0;
            var upper = _layouts.Count - 1;
            while (lower <= upper)
            {
                var middle = lower + (upper - lower) / 2;
                var candidate = _layouts[middle];
                if (globalSlice < candidate.Map.GlobalSliceBase) upper = middle - 1;
                else if (globalSlice >= candidate.Map.GlobalSliceBase + candidate.Map.SliceCount) lower = middle + 1;
                else return candidate;
            }
            return null;
        }

        private async Task<byte[]?> GetBodyAsync(int index, CancellationToken ct)
        {
            if (MissingSegmentIndices.Contains(index) || CorruptSegmentIndices.Contains(index)) return null;
            if (_bodies.TryGetValue(index, out var cached)) return cached.Bytes;
            var length = checked((int)_layout.Map.SegmentRanges[index].Count);
            if (length > RetainedByteLimit - CachedBodyBytes)
                throw new Par2MemoryCapExceededException("PAR2 source window exceeds the repair memory cap.");
            using var reservation = new DisposableOwner<IDisposable>(() => _reads.Budget.Reserve(length + 128L));
            await _reads.FetchGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var response = await _client.DecodedBodyAsync(_layout.SegmentIds[index], ct).ConfigureAwait(false);
                await using var stream = response.Stream!;
                await using var counted = new RepairCountingStream(stream, bytes => _reads.ReadBytes(bytes));
                var bytes = new byte[length];
                await counted.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
                var extra = new byte[1];
                if (await counted.ReadAsync(extra, ct).ConfigureAwait(false) != 0)
                    throw new InvalidDataException("PAR2 source article exceeds its validated volume range.");
                _bodies[index] = (bytes, reservation.Value);
                reservation.ReleaseOwnership();
                var retained = Interlocked.Add(ref _cachedBodyBytes, bytes.Length);
                Interlocked.Exchange(ref _peakCachedBodyBytes, Math.Max(PeakCachedBodyBytes, retained));
                return bytes;
            }
            catch (Exception exception) when (IsUnavailableArticle(exception))
            {
                _reads.NoteUnavailable(_layout.SegmentIds[index], exception);
                if (_reads.MissingIds.Contains(_layout.SegmentIds[index])) NoteMissing(index);
                else NoteCorrupt(index);
                return null;
            }
            finally { _reads.FetchGate.Release(); }
        }

        private void Evict(int index)
        {
            if (!_bodies.Remove(index, out var body)) return;
            body.Reservation.Dispose();
            Interlocked.Add(ref _cachedBodyBytes, -body.Bytes.Length);
        }

        public void Dispose() => BeginSequentialPass();
    }
}