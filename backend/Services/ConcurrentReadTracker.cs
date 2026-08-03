namespace NzbWebDAV.Services;

/// <summary>
/// Measures opportunities a hypothetical shared stream could serve. It does not
/// alter streaming behavior: every overlapping request still uses a private stream.
/// </summary>
public sealed class ConcurrentReadTracker(TimeProvider? timeProvider = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, PathState> _paths = new(StringComparer.Ordinal);
    private readonly AsyncLocal<ReadContext?> _currentRead = new();
    private long _nextReaderId;
    private long _readerStarts;
    private long _overlapEvents;
    private long _privateFallbacksNoRegistry;
    private long _duplicateInFlightSegmentFetches;
    private long _peakConcurrentReaders;
    private long _completedReads;
    private long _totalReadLifetimeMs;
    private long _maxReadLifetimeMs;
    private long _startDistanceSamples;
    private long _totalStartDistanceBytes;
    private long _maxStartDistanceBytes;
    private readonly long[] _regionStarts = new long[Enum.GetValues<ConcurrentReadRegion>().Length];

    public ReadScope BeginRead(
        string path,
        long? startOffset,
        ConcurrentReadRegion region)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (startOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(startOffset));
        path = "/" + path.TrimStart('/');

        var readerId = Interlocked.Increment(ref _nextReaderId);
        var previous = _currentRead.Value;
        var startedAt = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (!_paths.TryGetValue(path, out var state))
            {
                state = new PathState();
                _paths.Add(path, state);
            }

            var overlaps = state.Readers.Count > 0;
            var reader = new ReaderState(startOffset, overlaps);
            state.Readers.Add(readerId, reader);
            _readerStarts++;
            _regionStarts[(int)region]++;

            if (overlaps)
            {
                _overlapEvents++;
                _privateFallbacksNoRegistry++;
                RecordStartDistanceLocked(state, readerId, reader);
            }

            _peakConcurrentReaders = Math.Max(_peakConcurrentReaders, state.Readers.Count);
        }

        var context = new ReadContext(path, readerId);
        _currentRead.Value = context;
        return new ReadScope(this, context, previous, startedAt);
    }

    /// <summary>
    /// Marks one logical segment transfer as in flight. A duplicate is counted only
    /// when another reader for the same path is simultaneously fetching that segment.
    /// </summary>
    public IDisposable BeginSegmentFetch(string segmentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        var context = _currentRead.Value;
        if (context is null) return NoopScope.Instance;

        lock (_gate)
        {
            if (!_paths.TryGetValue(context.Path, out var state) ||
                !state.Readers.ContainsKey(context.ReaderId))
            {
                return NoopScope.Instance;
            }

            if (!state.InFlightSegments.TryGetValue(segmentId, out var readers))
            {
                readers = [];
                state.InFlightSegments.Add(segmentId, readers);
            }

            if (readers.Keys.Any(x => x != context.ReaderId))
                _duplicateInFlightSegmentFetches++;

            readers.TryGetValue(context.ReaderId, out var count);
            readers[context.ReaderId] = count + 1;
        }

        return new SegmentFetchScope(this, context, segmentId);
    }

    public ConcurrentReadSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new ConcurrentReadSnapshot(
                _readerStarts,
                _overlapEvents,
                _privateFallbacksNoRegistry,
                _duplicateInFlightSegmentFetches,
                _peakConcurrentReaders,
                _paths.Count(x => x.Value.Readers.Count > 1),
                _paths.Sum(x => x.Value.InFlightSegments.Sum(
                    segment => segment.Value.Values.Sum())),
                _completedReads,
                _totalReadLifetimeMs,
                _maxReadLifetimeMs,
                _startDistanceSamples,
                _totalStartDistanceBytes,
                _maxStartDistanceBytes,
                _regionStarts[(int)ConcurrentReadRegion.Full],
                _regionStarts[(int)ConcurrentReadRegion.StartRange],
                _regionStarts[(int)ConcurrentReadRegion.OffsetRange],
                _regionStarts[(int)ConcurrentReadRegion.SuffixRange]);
        }
    }

    private void UpdateStart(ReadContext context, long startOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startOffset);
        lock (_gate)
        {
            if (!_paths.TryGetValue(context.Path, out var state) ||
                !state.Readers.TryGetValue(context.ReaderId, out var reader))
            {
                return;
            }

            reader.StartOffset = startOffset;
            RecordStartDistanceLocked(state, context.ReaderId, reader);
        }
    }

    private void RecordStartDistanceLocked(
        PathState state,
        long readerId,
        ReaderState reader)
    {
        if (!reader.JoinedOverlap ||
            reader.DistanceRecorded ||
            reader.StartOffset is not { } start)
        {
            return;
        }

        var otherStarts = state.Readers
            .Where(x => x.Key != readerId && x.Value.StartOffset.HasValue)
            .Select(x => x.Value.StartOffset!.Value)
            .ToArray();
        if (otherStarts.Length == 0) return;

        var distance = otherStarts.Min(x => start >= x ? start - x : x - start);
        reader.DistanceRecorded = true;
        _startDistanceSamples++;
        _totalStartDistanceBytes += distance;
        _maxStartDistanceBytes = Math.Max(_maxStartDistanceBytes, distance);
    }

    private void EndRead(ReadScope scope)
    {
        lock (_gate)
        {
            if (_paths.TryGetValue(scope.Context.Path, out var state))
            {
                state.Readers.Remove(scope.Context.ReaderId);
                RemovePathIfIdleLocked(scope.Context.Path, state);
            }

            var elapsed = _timeProvider.GetUtcNow() - scope.StartedAt;
            var elapsedMs = Math.Max(0, (long)elapsed.TotalMilliseconds);
            _completedReads++;
            _totalReadLifetimeMs += elapsedMs;
            _maxReadLifetimeMs = Math.Max(_maxReadLifetimeMs, elapsedMs);
        }
    }

    private void EndSegmentFetch(ReadContext context, string segmentId)
    {
        lock (_gate)
        {
            if (!_paths.TryGetValue(context.Path, out var state) ||
                !state.InFlightSegments.TryGetValue(segmentId, out var readers) ||
                !readers.TryGetValue(context.ReaderId, out var count))
            {
                return;
            }

            if (count == 1)
                readers.Remove(context.ReaderId);
            else
                readers[context.ReaderId] = count - 1;
            if (readers.Count == 0)
                state.InFlightSegments.Remove(segmentId);
            RemovePathIfIdleLocked(context.Path, state);
        }
    }

    private void RemovePathIfIdleLocked(string path, PathState state)
    {
        if (state.Readers.Count == 0 && state.InFlightSegments.Count == 0)
            _paths.Remove(path);
    }

    public sealed class ReadScope : IDisposable
    {
        private readonly ConcurrentReadTracker _tracker;
        private readonly ReadContext? _previous;
        private int _disposed;

        internal ReadScope(
            ConcurrentReadTracker tracker,
            ReadContext context,
            ReadContext? previous,
            DateTimeOffset startedAt)
        {
            _tracker = tracker;
            Context = context;
            _previous = previous;
            StartedAt = startedAt;
        }

        internal ReadContext Context { get; }
        internal DateTimeOffset StartedAt { get; }

        public void UpdateStart(long startOffset)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _tracker.UpdateStart(Context, startOffset);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _tracker._currentRead.Value = _previous;
            _tracker.EndRead(this);
        }
    }

    private sealed class SegmentFetchScope(
        ConcurrentReadTracker tracker,
        ReadContext context,
        string segmentId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                tracker.EndSegmentFetch(context, segmentId);
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static NoopScope Instance { get; } = new();
        public void Dispose() { }
    }

    internal sealed record ReadContext(string Path, long ReaderId);

    private sealed class PathState
    {
        public Dictionary<long, ReaderState> Readers { get; } = [];
        public Dictionary<string, Dictionary<long, int>> InFlightSegments { get; } =
            new(StringComparer.Ordinal);
    }

    private sealed class ReaderState(long? startOffset, bool joinedOverlap)
    {
        public long? StartOffset { get; set; } = startOffset;
        public bool JoinedOverlap { get; } = joinedOverlap;
        public bool DistanceRecorded { get; set; }
    }
}

public enum ConcurrentReadRegion
{
    Full,
    StartRange,
    OffsetRange,
    SuffixRange,
}

public readonly record struct ConcurrentReadSnapshot(
    long ReaderStarts,
    long OverlapEvents,
    long PrivateFallbacksNoRegistry,
    long DuplicateInFlightSegmentFetches,
    long PeakConcurrentReaders,
    long CurrentOverlappingPaths,
    long CurrentInFlightSegmentFetches,
    long CompletedReads,
    long TotalReadLifetimeMs,
    long MaxReadLifetimeMs,
    long StartDistanceSamples,
    long TotalStartDistanceBytes,
    long MaxStartDistanceBytes,
    long FullReads,
    long StartRangeReads,
    long OffsetRangeReads,
    long SuffixRangeReads);
