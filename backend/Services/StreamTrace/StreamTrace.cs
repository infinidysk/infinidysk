using System.Runtime.CompilerServices;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Logging;
using Serilog;

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

    private static readonly LogThrottle SampleFailureThrottle = new();
    private static readonly ConditionalWeakTable<IStreamTraceSampled, object> Sampled = new();
    private static readonly object SampledMarker = new();
    private static Timer? _sampleTimer;

    /// <summary>
    /// Samples the producer once a second for as long as it is registered, starting whenever
    /// tracing is enabled, so activation also reaches producers that are already running or blocked.
    /// </summary>
    internal static void RegisterSampled(IStreamTraceSampled source)
    {
        Sampled.AddOrUpdate(source, SampledMarker);
        if (Volatile.Read(ref _sampleTimer) is null)
        {
            Timer timer;
            // The shared timer must not keep the first registrant's trace scopes alive.
            using (ExecutionContext.SuppressFlow())
                timer = new Timer(static _ => SampleAll(), null, Timeout.Infinite, Timeout.Infinite);
            if (Interlocked.CompareExchange(ref _sampleTimer, timer, null) is null)
                timer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            else
                timer.Dispose();
        }

        if (IsEnabled) Sample(source);
    }

    internal static void UnregisterSampled(IStreamTraceSampled source) => Sampled.Remove(source);

    internal static void SampleAll()
    {
        if (!IsEnabled) return;
        foreach (var (source, _) in Sampled)
            Sample(source);
    }

    private static void Sample(IStreamTraceSampled source)
    {
        try
        {
            source.Sample();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (SampleFailureThrottle.ShouldLog("sample", TimeSpan.FromMinutes(1), out var suppressed))
                Log.Debug(e, "Stream trace sample failed. SuppressedCount: {SuppressedCount}", suppressed);
        }
    }

    private static readonly AsyncLocal<bool> BackgroundScope = new();

    /// <summary>True while preparing data no reader is waiting on yet, such as priming the next volume.</summary>
    internal static bool InBackground => BackgroundScope.Value;

    internal static BackgroundTraceScope BeginBackground()
    {
        var previous = BackgroundScope.Value;
        BackgroundScope.Value = true;
        return new BackgroundTraceScope(previous);
    }

    internal readonly struct BackgroundTraceScope(bool previous) : IDisposable
    {
        public void Dispose() => BackgroundScope.Value = previous;
    }

    internal static void TryHeadWaitSummary(
        Guid sessionId, long? rangeGeneration, int pipelineId, int? partIndex, string summary, TimeSpan totalWait,
        int heads)
    {
        if (_buffer is { Enabled: true } buffer)
            buffer.HeadWaitSummary(sessionId, rangeGeneration, pipelineId, partIndex, summary, totalWait, heads);
    }

    internal static void TryPipelineSample(StreamTracePipelineSample sample)
    {
        if (_buffer is not { Enabled: true } buffer) return;
        buffer.PipelineSample(sample, ProbePools());
    }

    internal static void TryPumpSample(
        StreamTraceRangeContext range, long bytesPumped, string state, int readers, long? readerLeadBytes)
    {
        if (_buffer is not { Enabled: true } buffer) return;
        buffer.PumpSample(range, bytesPumped, state, readers, readerLeadBytes, ProbePools());
    }

    private static StreamTracePoolProbe ProbePools()
    {
        try
        {
            if (_connectionProbe?.Invoke() is not { } pools) return default;
            int active = 0, live = 0, max = 0, free = 0, waiting = 0;
            var admitted = false;
            foreach (var pool in pools)
            {
                active += pool.ActiveConnections;
                live += pool.LiveConnections;
                max += pool.EffectiveMaxConnections;
                if (pool.Admission is not { } admission) continue;
                admitted = true;
                free += Math.Max(0, admission.EffectiveTransferLimit - admission.ActiveTransferOperations);
                waiting += admission.WaitingTransferOperations;
            }

            return new StreamTracePoolProbe(active, live, max, admitted ? free : null, admitted ? waiting : null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (SampleFailureThrottle.ShouldLog("probe", TimeSpan.FromMinutes(1), out var suppressed))
                Log.Debug(e, "Stream trace pool probe failed. SuppressedCount: {SuppressedCount}", suppressed);
            return default;
        }
    }

    internal static void TryWait(
        StreamTraceKind kind,
        string phase,
        TimeSpan elapsed,
        int? partIndex = null,
        long? offset = null)
    {
        if (_buffer is not { Enabled: true } buffer) return;
        var range = MultiProviderNntpClient.CurrentStreamTraceRange;
        var sessionId = range?.SessionId ?? MultiProviderNntpClient.CurrentReadSessionId;
        if (sessionId is not { } value) return;
        buffer.Wait(new StreamTraceWait(kind, value, range?.Generation, phase, elapsed)
        {
            PartIndex = partIndex,
            Offset = offset,
            ReaderBlocked = !InBackground,
        });
    }

    internal static void TryWait(StreamTraceWait wait)
    {
        if (_buffer is { Enabled: true } buffer) buffer.Wait(wait);
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
