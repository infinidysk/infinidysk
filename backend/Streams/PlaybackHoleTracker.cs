using System.Collections.Concurrent;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;

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

    /// <summary>Returns a lease the opened stream must dispose; it keeps the budget alive while idle.</summary>
    public static IDisposable? SetDamageBudget(string? path, PlaybackDamageBudget? budget)
    {
        if (!IsTrackablePath(path))
            return null;

        var now = Clock.GetUtcNow();
        if (budget is null)
        {
            if (Files.TryGetValue(path!, out var existing))
            {
                lock (existing)
                {
                    existing.Budget = null;
                    existing.MissingIndices.Clear();
                    existing.BudgetExceeded = false;
                    existing.BudgetException = null;
                }
            }
            return null;
        }

        while (true)
        {
            var state = Files.GetOrAdd(path!, _ => new FileState { LastEventUtc = now });
            lock (state)
            {
                // A concurrent sweep may have evicted this entry before the lock was taken.
                if (!Files.TryGetValue(path!, out var current) || !ReferenceEquals(current, state))
                    continue;
                state.OpenStreams++;
                ResetIfStale(state, now);
                state.Budget = budget;
                state.MissingIndices.Clear();
                string? firstMissingId = null;
                foreach (var (id, index) in state.MissingSegmentIds
                             .Select(id => (id, Array.IndexOf(budget.SegmentIds, id)))
                             .Where(entry => entry.Item2 >= 0))
                {
                    state.MissingIndices.Add(index);
                    firstMissingId ??= id;
                }
                state.BudgetExceeded = state.MissingIndices.Count > 0 && budget.IsExceeded(state.MissingIndices, out _);
                state.BudgetException = state.BudgetExceeded
                    ? state.BudgetException ?? state.LastException ?? new UsenetArticleNotFoundException(firstMissingId!)
                    : null;
            }

            MaybeCleanup(now);
            return new StreamLease(state);
        }
    }

    /// <summary>Holes in a row a stream may pad before failing the read.</summary>
    public static int ConsecutiveFillLimit(string? path)
    {
        if (IsTrackablePath(path) && Files.TryGetValue(path!, out var state))
        {
            lock (state)
            {
                if (state.Budget is { } budget)
                    return budget.ConsecutiveFillLimit;
            }
        }

        return GapFillLimits.MaxConsecutiveZeroFills;
    }

    public static void RecordHole(string? path, string segmentId, Exception exception)
    {
        if (!IsTrackablePath(path) || string.IsNullOrEmpty(segmentId))
            return;

        var now = Clock.GetUtcNow();
        var state = Files.GetOrAdd(path!, static _ => new FileState());
        lock (state)
        {
            ResetIfStale(state, now);
            Prune(state, now);
            state.LastEventUtc = now;
            state.HoleTimes.Add(now);
            // An inconclusive miss still counts toward consecutive-hole fail-fast, but later reads
            // must ask the providers again instead of treating the segment as known-missing.
            if (!exception.IsInconclusiveArticleMiss())
            {
                state.MissingSegmentIds.Add(segmentId);
                // ponytail: linear id lookup per confirmed hole; fine while the budget caps holes in the tens.
                if (state.Budget is { } budget
                    && Array.IndexOf(budget.SegmentIds, segmentId) is >= 0 and var index
                    && state.MissingIndices.Add(index))
                {
                    state.BudgetExceeded = budget.IsExceeded(state.MissingIndices, out _);
                    if (state.BudgetExceeded)
                        state.BudgetException ??= exception;
                }
            }
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
            ResetIfStale(state, now);
            Prune(state, now);
            // Cumulative budget failure outlives the healthy-segment reset that only ends a consecutive run.
            if (state.BudgetExceeded)
            {
                exception = state.BudgetException;
                return true;
            }

            var limit = state.Budget?.ConsecutiveFillLimit ?? GapFillLimits.MaxConsecutiveZeroFills;
            if (state.HoleTimes.Count < limit)
                return false;
            exception = state.LastException;
            return true;
        }
    }

    public static bool IsKnownMissingSegment(string? path, string segmentId)
    {
        var known = false;
        var now = Clock.GetUtcNow();
        if (IsTrackablePath(path) && !string.IsNullOrEmpty(segmentId)
            && Files.TryGetValue(path!, out var state))
        {
            lock (state)
            {
                // Missing-segment memory must not outlive provider recovery: after a
                // PAR2 repair or backfill the same path would otherwise keep being
                // served zero bytes until an unrelated 256th RecordHole sweeps.
                if (!ExpireIfStale(path!, state, now))
                    known = state.MissingSegmentIds.Contains(segmentId);
            }
        }

        MaybeCleanup(now);
        return known;
    }

    public static HashSet<string>? SnapshotMissingSegmentIds(string? path)
    {
        HashSet<string>? snapshot = null;
        var now = Clock.GetUtcNow();
        if (IsTrackablePath(path) && Files.TryGetValue(path!, out var state))
        {
            lock (state)
            {
                if (!ExpireIfStale(path!, state, now) && state.MissingSegmentIds.Count > 0)
                    snapshot = [.. state.MissingSegmentIds];
            }
        }

        MaybeCleanup(now);
        return snapshot;
    }

    /// <summary>
    /// Production playback keys the tracker by the DAV path (always rooted at '/').
    /// Basenames used by existing stream tests stay off the process-wide map so
    /// parallel cases cannot accumulate each other's holes.
    /// </summary>
    private static bool IsTrackablePath(string? path) =>
        !string.IsNullOrEmpty(path) && path[0] == '/';

    // Caller must hold the state lock.
    private static bool IsStale(FileState state, DateTimeOffset now) =>
        now - state.LastEventUtc >= CleanupThreshold;

    // Caller must hold the state lock. Expired observations must not keep failing a since-repaired file.
    private static void ResetIfStale(FileState state, DateTimeOffset now)
    {
        if (!IsStale(state, now))
            return;
        state.HoleTimes.Clear();
        state.MissingSegmentIds.Clear();
        state.MissingIndices.Clear();
        state.LastException = null;
        state.BudgetExceeded = false;
        state.BudgetException = null;
        // The caller is touching the file now; without this a later lookup would drop the active budget.
        state.LastEventUtc = now;
    }

    // Caller must hold the state lock. Returns true when the stale entry was removed.
    private static bool ExpireIfStale(string path, FileState state, DateTimeOffset now)
    {
        if (!IsStale(state, now))
            return false;
        if (state.OpenStreams == 0)
            return Files.TryRemove(path, out _);
        // An open stream's budget must keep enforcing; only its expired observations go.
        ResetIfStale(state, now);
        return false;
    }

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
                ExpireIfStale(entry.Key, entry.Value, now);
        }
    }

    private sealed class StreamLease(FileState state) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            lock (state)
                state.OpenStreams--;
        }
    }

    private sealed class FileState
    {
        public DateTimeOffset LastEventUtc { get; set; }
        public Exception? LastException { get; set; }
        public List<DateTimeOffset> HoleTimes { get; } = [];
        public HashSet<string> MissingSegmentIds { get; } = new(StringComparer.Ordinal);
        public HashSet<int> MissingIndices { get; } = [];
        public PlaybackDamageBudget? Budget { get; set; }
        public int OpenStreams { get; set; }
        public bool BudgetExceeded { get; set; }
        public Exception? BudgetException { get; set; }
    }
}
