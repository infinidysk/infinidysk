namespace NzbWebDAV.Services.StreamTrace;

/// <summary>
/// Process-wide accessor for <see cref="StreamTraceBuffer"/> so deep stream
/// code (MultiSegmentStream, NzbFileStream) can emit without DI plumbing.
/// Configured once at startup from Program.cs.
/// </summary>
public static class StreamTrace
{
    private static StreamTraceBuffer? _buffer;

    public static void Configure(StreamTraceBuffer buffer) => _buffer = buffer;

    public static StreamTraceBuffer? Buffer => _buffer;

    public static void TrySeek(Guid sessionId, long offset)
        => _buffer?.Seek(sessionId, offset);

    public static void TryZeroFill(Guid sessionId, string segmentId, long bytes)
        => _buffer?.ZeroFill(sessionId, segmentId, bytes);

    public static void TryRetry(Guid sessionId, string segmentId, int attempt, string? message = null)
        => _buffer?.Retry(sessionId, segmentId, attempt, message);

    public static void TryPrefetchWidth(Guid sessionId, int previousBatchSize, int batchSize)
        => _buffer?.PrefetchWidth(sessionId, previousBatchSize, batchSize);

    internal static void TryBatchPlan(
        Guid sessionId,
        bool eligible,
        string reason,
        int? plannedSegments = null,
        long? plannedBytes = null,
        int? initialBatchWidth = null,
        int? configuredMaximumBatchWidth = null,
        int? effectiveConnectionTarget = null,
        int? activeReaderShareCount = null,
        int? effectivePrimaryTransferCapacity = null,
        int? wideningObservationFloor = null)
        => _buffer?.BatchPlan(
            sessionId, eligible, reason, plannedSegments, plannedBytes, initialBatchWidth,
            configuredMaximumBatchWidth, effectiveConnectionTarget, activeReaderShareCount,
            effectivePrimaryTransferCapacity, wideningObservationFloor);

    internal static void TryStreamStartup(
        Guid sessionId,
        long? rangeGeneration,
        string phase,
        long? bytes = null,
        TimeSpan? elapsed = null)
        => _buffer?.StreamStartup(sessionId, rangeGeneration, phase, bytes, elapsed);

    public static void TryStall(StreamTraceRangeContext? range, StreamStallKind kind, TimeSpan elapsed)
        => _buffer?.AddStall(range, kind, elapsed);

    public static void TryConnectionAcquired(StreamTraceRangeContext? range, TimeSpan wait, bool wasReused)
        => _buffer?.ConnectionAcquired(range, wait, wasReused);
}
