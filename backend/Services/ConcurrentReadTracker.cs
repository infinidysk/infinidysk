using System.Collections.Concurrent;

namespace NzbWebDAV.Services;

/// <summary>
/// Tracks concurrent read sessions on the same content path to measure potential
/// gains from shared streams or segment-fetch deduplication. This is instrumentation
/// only — it does not alter streaming behavior.
/// </summary>
public sealed class ConcurrentReadTracker
{
    private readonly ConcurrentDictionary<string, PathState> _paths = new(StringComparer.Ordinal);
    private long _overlapEvents;
    private long _duplicateSegmentFetches;
    private long _peakConcurrentReaders;

    /// <summary>Number of times a new reader joined a path that already had active readers.</summary>
    public long OverlapEvents => Interlocked.Read(ref _overlapEvents);

    /// <summary>
    /// Segment fetches that would have been deduplicatable if a shared stream existed
    /// (same segment ID fetched while another reader on the same path was also active).
    /// </summary>
    public long DuplicateSegmentFetches => Interlocked.Read(ref _duplicateSegmentFetches);

    /// <summary>Highest concurrent reader count observed on any single path.</summary>
    public long PeakConcurrentReaders => Interlocked.Read(ref _peakConcurrentReaders);

    /// <summary>Register a reader starting on a path. Returns a disposable scope.</summary>
    public IDisposable BeginRead(string path)
    {
        var state = _paths.GetOrAdd(path, _ => new PathState());
        var count = Interlocked.Increment(ref state.ActiveReaders);

        if (count > 1)
            Interlocked.Increment(ref _overlapEvents);

        UpdatePeak(count);
        return new ReadScope(this, path);
    }

    /// <summary>Record a segment fetch on a path that has concurrent readers.</summary>
    public void RecordSegmentFetch(string path, string segmentId)
    {
        if (!_paths.TryGetValue(path, out var state)) return;
        if (Volatile.Read(ref state.ActiveReaders) <= 1) return;

        var seenSet = state.RecentSegments;
        if (!seenSet.TryAdd(segmentId, Environment.TickCount64))
        {
            Interlocked.Increment(ref _duplicateSegmentFetches);
        }

        TrimOldSegments(seenSet);
    }

    /// <summary>Current snapshot for diagnostics/support packs.</summary>
    public ConcurrentReadSnapshot Snapshot() => new(
        OverlapEvents,
        DuplicateSegmentFetches,
        PeakConcurrentReaders,
        _paths.Count(kv => Volatile.Read(ref kv.Value.ActiveReaders) > 1));

    private void EndRead(string path)
    {
        if (!_paths.TryGetValue(path, out var state)) return;
        var remaining = Interlocked.Decrement(ref state.ActiveReaders);
        if (remaining <= 0)
        {
            state.RecentSegments.Clear();
            _paths.TryRemove(path, out _);
        }
    }

    private void UpdatePeak(long current)
    {
        while (true)
        {
            var peak = Interlocked.Read(ref _peakConcurrentReaders);
            if (current <= peak) return;
            if (Interlocked.CompareExchange(ref _peakConcurrentReaders, current, peak) == peak) return;
        }
    }

    private static void TrimOldSegments(ConcurrentDictionary<string, long> segments)
    {
        if (segments.Count <= 256) return;
        var cutoff = Environment.TickCount64 - 30_000;
        foreach (var kv in segments)
        {
            if (kv.Value < cutoff)
                segments.TryRemove(kv);
        }
    }

    private sealed class PathState
    {
        public int ActiveReaders;
        public readonly ConcurrentDictionary<string, long> RecentSegments = new(StringComparer.Ordinal);
    }

    private sealed class ReadScope(ConcurrentReadTracker tracker, string path) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                tracker.EndRead(path);
        }
    }
}

public readonly record struct ConcurrentReadSnapshot(
    long OverlapEvents,
    long DuplicateSegmentFetches,
    long PeakConcurrentReaders,
    long CurrentOverlappingPaths);
