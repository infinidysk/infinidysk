using System.Collections.Concurrent;

namespace NzbWebDAV.Services;

/// <summary>
/// In-memory counter of consecutive permanent streaming failures (missing usenet articles
/// or structurally corrupt archives) per
/// <c>DavItem</c>. Incremented by <c>ExceptionMiddleware</c> whenever it observes a qualifying
/// failure; consulted by urgent-repair scheduling and cleared after a successful full read,
/// health check, repair, or deletion.
///
/// Deliberately in-memory rather than persisted: failures recur naturally on replay, so a
/// process restart simply resets the count, which is an acceptable trade-off for avoiding a
/// schema migration for a niche opt-in feature.
/// </summary>
public class StreamingFailureTracker
{
    private const int MaximumAttributedSegmentIds = 64;
    private readonly ConcurrentDictionary<Guid, StreamingFailureSnapshot> _failures = new();

    /// <summary>Records a definitive article failure and returns the new immutable snapshot.</summary>
    public StreamingFailureSnapshot RecordAttributedFailure(Guid davItemId, string segmentId)
    {
        ArgumentException.ThrowIfNullOrEmpty(segmentId);
        return _failures.AddOrUpdate(
            davItemId,
            _ => new StreamingFailureSnapshot(1, false, [segmentId]),
            (_, previous) => previous.WithAttributedFailure(segmentId, MaximumAttributedSegmentIds));
    }

    /// <summary>Records a structural failure whose responsible segment cannot be proven.</summary>
    public StreamingFailureSnapshot RecordUnattributedFailure(Guid davItemId)
    {
        return _failures.AddOrUpdate(
            davItemId,
            _ => new StreamingFailureSnapshot(1, true, []),
            (_, previous) => previous.WithUnattributedFailure());
    }

    /// <summary>Increments a structural failure for compatibility with existing callers.</summary>
    public int RecordFailure(Guid davItemId)
    {
        return RecordUnattributedFailure(davItemId).Count;
    }

    /// <summary>Returns the current failure count for the item (0 if never recorded).</summary>
    public int GetFailureCount(Guid davItemId)
    {
        return GetSnapshot(davItemId).Count;
    }

    public StreamingFailureSnapshot GetSnapshot(Guid davItemId)
    {
        return _failures.TryGetValue(davItemId, out var snapshot)
            ? snapshot
            : StreamingFailureSnapshot.Empty;
    }

    /// <summary>Clears the counter after a successful full read, health check, repair, or deletion.</summary>
    public void ClearFailure(Guid davItemId)
    {
        _failures.TryRemove(davItemId, out _);
    }
}

public readonly record struct StreamingFailureSnapshot(
    int Count,
    bool HasUnattributedFailure,
    string[] SegmentIds)
{
    public static readonly StreamingFailureSnapshot Empty = new(0, false, []);

    public bool HasTargetableSegmentIds => Count > 0 && !HasUnattributedFailure && SegmentIds.Length > 0;

    internal StreamingFailureSnapshot WithAttributedFailure(string segmentId, int maximumSegmentIds)
    {
        if (SegmentIds.Contains(segmentId, StringComparer.Ordinal) || SegmentIds.Length >= maximumSegmentIds)
            return this with { Count = Count + 1 };

        return this with { Count = Count + 1, SegmentIds = [.. SegmentIds, segmentId] };
    }

    internal StreamingFailureSnapshot WithUnattributedFailure() =>
        this with { Count = Count + 1, HasUnattributedFailure = true };
}
