using System.Collections.Concurrent;

namespace NzbWebDAV.Streams;

/// <summary>
/// Process-wide memory of playback-discovered holes so rclone's abort-and-retry
/// cannot reset the consecutive-miss counter by opening a new HTTP range.
/// </summary>
internal static class PlaybackHoleTracker
{
    internal static readonly TimeSpan ConsecutiveWindow = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan CleanupThreshold = TimeSpan.FromMinutes(5);

    private static readonly ConcurrentDictionary<string, FileState> Files =
        new(StringComparer.Ordinal);
    private static int _callCount;

    internal static TimeProvider Clock { get; set; } = TimeProvider.System;

    internal static void ResetForTests()
    {
        Files.Clear();
        Clock = TimeProvider.System;
        Volatile.Write(ref _callCount, 0);
    }

    public static void RecordHole(string? path, string segmentId, Exception exception)
    {
        if (!IsTrackablePath(path) || string.IsNullOrEmpty(segmentId))
            return;

        var now = Clock.GetUtcNow();
        var state = Files.GetOrAdd(path!, static _ => new FileState());
        lock (state)
        {
            Prune(state, now);
            state.LastEventUtc = now;
            state.HoleTimes.Add(now);
            state.MissingSegmentIds.Add(segmentId);
            state.LastException = exception;
        }

        MaybeCleanup(now);
    }

    public static void RecordGoodSegment(string? path)
    {
        if (!IsTrackablePath(path) || !Files.TryGetValue(path!, out var state))
            return;

        var now = Clock.GetUtcNow();
        lock (state)
        {
            state.HoleTimes.Clear();
            state.LastEventUtc = now;
            state.LastException = null;
        }
    }

    public static bool ShouldFailFast(string? path, out Exception? exception)
    {
        exception = null;
        if (!IsTrackablePath(path) || !Files.TryGetValue(path!, out var state))
            return false;

        var now = Clock.GetUtcNow();
        lock (state)
        {
            Prune(state, now);
            if (state.HoleTimes.Count < GapFillLimits.MaxConsecutiveZeroFills)
                return false;
            exception = state.LastException;
            return true;
        }
    }

    public static bool IsKnownMissingSegment(string? path, string segmentId)
    {
        if (!IsTrackablePath(path) || string.IsNullOrEmpty(segmentId))
            return false;
        if (!Files.TryGetValue(path!, out var state))
            return false;

        lock (state)
            return state.MissingSegmentIds.Contains(segmentId);
    }

    public static HashSet<string>? SnapshotMissingSegmentIds(string? path)
    {
        if (!IsTrackablePath(path) || !Files.TryGetValue(path!, out var state))
            return null;

        lock (state)
            return state.MissingSegmentIds.Count == 0 ? null : [..state.MissingSegmentIds];
    }

    /// <summary>
    /// Production playback keys the tracker by the DAV path (always rooted at '/').
    /// Basenames used by existing stream tests stay off the process-wide map so
    /// parallel cases cannot accumulate each other's holes.
    /// </summary>
    private static bool IsTrackablePath(string? path) =>
        !string.IsNullOrEmpty(path) && path[0] == '/';

    private static void Prune(FileState state, DateTimeOffset now)
    {
        var cutoff = now - ConsecutiveWindow;
        var holeTimes = state.HoleTimes;
        var write = 0;
        for (var read = 0; read < holeTimes.Count; read++)
        {
            if (holeTimes[read] >= cutoff)
                holeTimes[write++] = holeTimes[read];
        }

        if (write < holeTimes.Count)
            holeTimes.RemoveRange(write, holeTimes.Count - write);
    }

    private static void MaybeCleanup(DateTimeOffset now)
    {
        if (Interlocked.Increment(ref _callCount) % 256 != 0)
            return;

        foreach (var entry in Files)
        {
            lock (entry.Value)
            {
                if (now - entry.Value.LastEventUtc >= CleanupThreshold)
                    Files.TryRemove(entry.Key, out _);
            }
        }
    }

    private sealed class FileState
    {
        public DateTimeOffset LastEventUtc { get; set; }
        public Exception? LastException { get; set; }
        public List<DateTimeOffset> HoleTimes { get; } = [];
        public HashSet<string> MissingSegmentIds { get; } = new(StringComparer.Ordinal);
    }
}
