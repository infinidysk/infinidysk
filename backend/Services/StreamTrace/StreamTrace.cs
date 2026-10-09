using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;

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

    internal static void TryHedgeIssued(Guid sessionId, string segmentId, int segmentIndex, int waitMs, int hedgeDelayMs)
        => _buffer?.HedgeIssued(sessionId, segmentId, segmentIndex, waitMs, hedgeDelayMs);

    internal static void TryHedgeResolved(Guid sessionId, string segmentId, int segmentIndex, string outcome, int decisionMs)
        => _buffer?.HedgeResolved(sessionId, segmentId, segmentIndex, outcome, decisionMs);

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

    /// <summary>Reader waits shorter than this are steady-state noise, not stalls.</summary>
    internal static readonly TimeSpan WaitThreshold = TimeSpan.FromMilliseconds(50);

    internal static bool IsEnabled => _buffer?.Enabled == true;

    private static Func<IReadOnlyList<ProviderConnectionSnapshot>>? _connectionProbe;

    public static void ConfigureConnectionProbe(Func<IReadOnlyList<ProviderConnectionSnapshot>> probe)
        => _connectionProbe = probe;

    internal static Guid? CurrentSessionId =>
        MultiProviderNntpClient.CurrentStreamTraceRange?.SessionId ?? MultiProviderNntpClient.CurrentReadSessionId;

    internal static void TryHeadWaitSummary(
        Guid sessionId, long? rangeGeneration, string summary, TimeSpan totalWait, int heads)
    {
        if (_buffer is { Enabled: true } buffer)
            buffer.HeadWaitSummary(sessionId, rangeGeneration, summary, totalWait, heads);
    }

    internal static void TryPipelineSample(
        Guid sessionId,
        long? rangeGeneration,
        int segmentIndex,
        int queuedSegments,
        int awaitingSegments,
        int respondedAhead,
        int activeBatches,
        int? batchSize,
        long inFlightBytes,
        int? headWaitMs)
    {
        if (_buffer is not { Enabled: true } buffer) return;
        var (poolActive, poolLive, poolMax) = ProbePools();
        buffer.PipelineSample(
            sessionId, rangeGeneration, segmentIndex, queuedSegments, awaitingSegments, respondedAhead,
            activeBatches, batchSize, inFlightBytes, headWaitMs, poolActive, poolLive, poolMax);
    }

    internal static void TryPumpSample(StreamTraceRangeContext range, long bytesPumped)
    {
        if (_buffer is not { Enabled: true } buffer) return;
        var (poolActive, poolLive, poolMax) = ProbePools();
        buffer.PumpSample(range, bytesPumped, poolActive, poolLive, poolMax);
    }

    private static (int? Active, int? Live, int? Max) ProbePools()
    {
        if (_connectionProbe?.Invoke() is not { } pools) return (null, null, null);
        return (pools.Sum(p => p.ActiveConnections), pools.Sum(p => p.LiveConnections),
            pools.Sum(p => p.EffectiveMaxConnections));
    }

    internal static void TryWait(
        StreamTraceKind kind,
        string phase,
        TimeSpan elapsed,
        int? segmentIndex = null,
        int? partIndex = null,
        long? offset = null,
        int? issueAgeMs = null,
        int? respondedAhead = null,
        int? queuedSegments = null)
    {
        if (_buffer is not { Enabled: true } buffer) return;
        var range = MultiProviderNntpClient.CurrentStreamTraceRange;
        var sessionId = range?.SessionId ?? MultiProviderNntpClient.CurrentReadSessionId;
        if (sessionId is not { } value) return;
        buffer.Wait(
            kind, value, range?.Generation, phase, elapsed, segmentIndex, partIndex, offset,
            issueAgeMs, respondedAhead, queuedSegments);
    }

    public static void TryStall(StreamTraceRangeContext? range, StreamStallKind kind, TimeSpan elapsed)
        => _buffer?.AddStall(range, kind, elapsed);

    public static void TryConnectionAcquired(StreamTraceRangeContext? range, TimeSpan wait, bool wasReused)
        => _buffer?.ConnectionAcquired(range, wait, wasReused);

    public static void TryConnectionAttemptFailed(StreamTraceRangeContext? range, TimeSpan wait)
        => _buffer?.ConnectionAttemptFailed(range, wait);

    public static void TryPermitWait(StreamTraceRangeContext? range, TimeSpan wait)
        => _buffer?.PermitWait(range, wait);
}
