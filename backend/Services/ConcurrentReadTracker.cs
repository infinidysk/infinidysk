using NzbWebDAV.Config;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Services;

/// <summary>
/// Tracks overlapping WebDAV and /view reads and, when shared streams are
/// enabled, real attach hits versus private fallbacks.
/// </summary>
public sealed class ConcurrentReadTracker(
    TimeProvider? timeProvider = null,
    ConfigManager? configManager = null,
    SharedStreamRetentionAccount? retentionAccount = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConfigManager? _configManager = configManager;
    private readonly SharedStreamRetentionAccount _retention =
        retentionAccount ?? SharedStreamRetentionAccount.Instance;
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
    private long _sharedAttachHits;
    private long _sharedAttachMissesBehindWindow;
    private long _sharedAttachMissesAheadOfFrontier;
    private long _sharedAttachMissesEntryUnusable;
    private long _sharedAttachMissesAtEntryCap;
    private long _sharedAttachMissesAtGlobalCap;
    private long _sharedAttachMissesSmallRangeNoEntry;
    private long _sharedAttachMissesIneligible;
    private long _sharedAttachMissesNoCoveringEntry;
    private long _sharedEntriesCreated;
    private long _sharedEntriesReapedGrace;
    private long _sharedEntriesReapedFailure;
    private long _sharedReaderEvictions;
    private long _sharedReadersServedTotal;
    private long _sharedStreamTotalBytesPumped;
    private long _sharedStreamTotalEntryLifetimeMs;
    private long _sharedStreamRingLogicalBytes;
    private long _sharedStreamLiveEntries;
    private long _sharedStreamReadyEntries;
    private long _sharedStreamDrainingEntries;
    private long _sharedStreamLaggingReaders;
    private long _sharedStreamPressureDetaches;
    private long _sharedStreamPressureReaps;

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
                if (!IsSharedStreamsEnabled())
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
        var retention = _retention.Snapshot();
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
                _regionStarts[(int)ConcurrentReadRegion.SuffixRange],
                _sharedAttachHits,
                _sharedAttachMissesBehindWindow,
                _sharedAttachMissesAheadOfFrontier,
                _sharedAttachMissesEntryUnusable,
                _sharedAttachMissesAtEntryCap,
                _sharedAttachMissesAtGlobalCap,
                _sharedAttachMissesSmallRangeNoEntry,
                _sharedAttachMissesIneligible,
                _sharedAttachMissesNoCoveringEntry,
                _sharedEntriesCreated,
                _sharedEntriesReapedGrace,
                _sharedEntriesReapedFailure,
                _sharedReaderEvictions,
                _sharedReadersServedTotal,
                retention.RingRentedBytes,
                retention.RingRentedBytesPeak,
                _sharedStreamTotalBytesPumped,
                _sharedStreamTotalEntryLifetimeMs,
                _sharedStreamRingLogicalBytes,
                retention.PumpScratchRentedBytes,
                retention.PumpScratchRentedBytesPeak,
                _sharedStreamLiveEntries,
                _sharedStreamReadyEntries,
                _sharedStreamDrainingEntries,
                _sharedStreamLaggingReaders,
                _sharedStreamPressureDetaches,
                _sharedStreamPressureReaps);
        }
    }

    /// <summary>
    /// Counts a real private fallback for the current overlapping reader when
    /// shared streams are enabled. No-op when the feature is off (BeginRead
    /// already counted every overlap) or when this reader is not overlapping.
    /// </summary>
    public void RecordPrivateFallbackIfOverlapping()
    {
        if (!IsSharedStreamsEnabled())
            return;

        var context = _currentRead.Value;
        if (context is null)
            return;

        lock (_gate)
        {
            if (!_paths.TryGetValue(context.Path, out var state) ||
                !state.Readers.TryGetValue(context.ReaderId, out var reader) ||
                !reader.JoinedOverlap ||
                reader.FallbackRecorded)
            {
                return;
            }

            reader.FallbackRecorded = true;
            _privateFallbacksNoRegistry++;
        }
    }

    public void RecordSharedAttachHit()
    {
        lock (_gate)
        {
            _sharedAttachHits++;
            _sharedReadersServedTotal++;
        }
    }

    public void RecordSharedAttachMiss(SharedStreamAttachMissReason reason)
    {
        lock (_gate)
        {
            switch (reason)
            {
                case SharedStreamAttachMissReason.BehindWindow:
                    _sharedAttachMissesBehindWindow++;
                    break;
                case SharedStreamAttachMissReason.AheadOfFrontier:
                    _sharedAttachMissesAheadOfFrontier++;
                    break;
                case SharedStreamAttachMissReason.EntryUnusable:
                    _sharedAttachMissesEntryUnusable++;
                    break;
                case SharedStreamAttachMissReason.AtEntryCap:
                    _sharedAttachMissesAtEntryCap++;
                    break;
                case SharedStreamAttachMissReason.AtGlobalCap:
                    _sharedAttachMissesAtGlobalCap++;
                    break;
                case SharedStreamAttachMissReason.SmallRangeNoEntry:
                    _sharedAttachMissesSmallRangeNoEntry++;
                    break;
                case SharedStreamAttachMissReason.NoCoveringEntry:
                    _sharedAttachMissesNoCoveringEntry++;
                    break;
                default:
                    _sharedAttachMissesIneligible++;
                    break;
            }
        }
    }

    public void RecordSharedEntryCreated()
    {
        lock (_gate) _sharedEntriesCreated++;
    }

    public void RecordSharedEntryReaped(SharedStreamReapReason reason, long bytesPumped, long lifetimeMs)
    {
        lock (_gate)
        {
            if (reason == SharedStreamReapReason.Failure)
                _sharedEntriesReapedFailure++;
            else
                _sharedEntriesReapedGrace++;
            _sharedStreamTotalBytesPumped += Math.Max(0, bytesPumped);
            _sharedStreamTotalEntryLifetimeMs += Math.Max(0, lifetimeMs);
        }
    }

    public void RecordSharedReaderEvictions(int count)
    {
        if (count <= 0) return;
        lock (_gate) _sharedReaderEvictions += count;
    }

    public void RecordSharedReadersServed(int count)
    {
        if (count <= 0) return;
        lock (_gate) _sharedReadersServedTotal += count;
    }

    public void UpdateSharedRingRetainedBytes(long currentBytes)
    {
        lock (_gate)
            _sharedStreamRingLogicalBytes = Math.Max(0, currentBytes);
    }

    public void UpdateSharedStreamCensus(
        long liveEntries,
        long readyEntries,
        long drainingEntries,
        long laggingReaders)
    {
        lock (_gate)
        {
            _sharedStreamLiveEntries = Math.Max(0, liveEntries);
            _sharedStreamReadyEntries = Math.Max(0, readyEntries);
            _sharedStreamDrainingEntries = Math.Max(0, drainingEntries);
            _sharedStreamLaggingReaders = Math.Max(0, laggingReaders);
        }
    }

    public void RecordSharedPressureDetach()
    {
        lock (_gate) _sharedStreamPressureDetaches++;
    }

    public void RecordSharedPressureReap()
    {
        lock (_gate) _sharedStreamPressureReaps++;
    }

    private bool IsSharedStreamsEnabled() =>
        _configManager?.IsSharedStreamsEnabled() ?? false;

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
        public bool FallbackRecorded { get; set; }
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
    long SuffixRangeReads,
    long SharedAttachHits = 0,
    long SharedAttachMissesBehindWindow = 0,
    long SharedAttachMissesAheadOfFrontier = 0,
    long SharedAttachMissesEntryUnusable = 0,
    long SharedAttachMissesAtEntryCap = 0,
    long SharedAttachMissesAtGlobalCap = 0,
    long SharedAttachMissesSmallRangeNoEntry = 0,
    long SharedAttachMissesIneligible = 0,
    long SharedAttachMissesNoCoveringEntry = 0,
    long SharedEntriesCreated = 0,
    long SharedEntriesReapedGrace = 0,
    long SharedEntriesReapedFailure = 0,
    long SharedReaderEvictions = 0,
    long SharedReadersServedTotal = 0,
    long SharedStreamRingRetainedBytes = 0,
    long SharedStreamRingRetainedBytesPeak = 0,
    long SharedStreamTotalBytesPumped = 0,
    long SharedStreamTotalEntryLifetimeMs = 0,
    long SharedStreamRingLogicalBytes = 0,
    long SharedStreamPumpScratchRentedBytes = 0,
    long SharedStreamPumpScratchRentedBytesPeak = 0,
    long SharedStreamLiveEntries = 0,
    long SharedStreamReadyEntries = 0,
    long SharedStreamDrainingEntries = 0,
    long SharedStreamLaggingReaders = 0,
    long SharedStreamPressureDetaches = 0,
    long SharedStreamPressureReaps = 0)
{
    public long SharedAttachMisses =>
        SharedAttachMissesBehindWindow +
        SharedAttachMissesAheadOfFrontier +
        SharedAttachMissesEntryUnusable +
        SharedAttachMissesAtEntryCap +
        SharedAttachMissesAtGlobalCap +
        SharedAttachMissesSmallRangeNoEntry +
        SharedAttachMissesIneligible +
        SharedAttachMissesNoCoveringEntry;

    public long SharedAttachAttempts => SharedAttachHits + SharedAttachMisses;
}
