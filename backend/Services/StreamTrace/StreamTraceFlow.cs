using NzbWebDAV.Clients.Usenet;

namespace NzbWebDAV.Services.StreamTrace;

/// <summary>
/// A trace range opened on first use while tracing is enabled, so a producer that started
/// before tracing was switched on still gets its own range.
/// </summary>
internal sealed class StreamTraceLazyRange(Func<StreamTraceRangeContext?> open)
{
    private readonly object _gate = new();
    private StreamTraceRangeContext? _range;
    private bool _closed;

    public StreamTraceRangeContext? Resolve()
    {
        lock (_gate)
        {
            if (_closed || _range is not null || !StreamTrace.IsEnabled) return _range;
            return _range = open();
        }
    }

    /// <summary>Stops further opens and returns the range to end, if one was opened.</summary>
    public StreamTraceRangeContext? Close()
    {
        lock (_gate)
        {
            _closed = true;
            return _range;
        }
    }
}

/// <summary>Trace identity of the flow that built a producer, resolved when it emits.</summary>
internal sealed class StreamTraceFlow
{
    private readonly StreamTraceRangeContext? _range;
    private readonly Guid? _readSessionId;
    private readonly StreamTraceLazyRange? _lazyRange;

    private StreamTraceFlow(
        StreamTraceRangeContext? range, Guid? readSessionId, StreamTraceLazyRange? lazyRange, int? partIndex)
    {
        _range = range;
        _readSessionId = readSessionId;
        _lazyRange = lazyRange;
        PartIndex = partIndex;
    }

    /// <summary>Multipart volume the producer reads, so overlapping volume pipelines stay distinguishable.</summary>
    public int? PartIndex { get; }

    public static StreamTraceFlow? Capture(int? partIndex = null)
    {
        var range = MultiProviderNntpClient.BoundStreamTraceRange;
        var lazyRange = MultiProviderNntpClient.CurrentLazyStreamTraceRange;
        var readSessionId = MultiProviderNntpClient.CurrentReadSessionId;
        return range is null && lazyRange is null && readSessionId is null
            ? null
            : new StreamTraceFlow(range, readSessionId, lazyRange, partIndex);
    }

    public (Guid SessionId, long? Generation)? Resolve()
    {
        if ((_range ?? _lazyRange?.Resolve()) is { } range) return (range.SessionId, range.Generation);
        return _readSessionId is { } sessionId ? (sessionId, null) : null;
    }
}

/// <summary>A live producer sampled once a second while tracing is enabled.</summary>
internal interface IStreamTraceSampled
{
    /// <summary>Emits one sample; must skip once the producer's trace has closed.</summary>
    void Sample();
}

/// <param name="Phase">Fixed code for where the reader was blocked when the wait started.</param>
internal sealed record StreamTraceWait(
    StreamTraceKind Kind, Guid SessionId, long? RangeGeneration, string Phase, TimeSpan Elapsed)
{
    public int? PipelineId { get; init; }
    public int? PartIndex { get; init; }
    public int? SegmentIndex { get; init; }
    public long? Offset { get; init; }
    public int? IssueAgeMs { get; init; }
    public int? RespondedAhead { get; init; }
    public int? QueuedSegments { get; init; }
    public int? NotQueuedMs { get; init; }
    public int? AwaitingResponseMs { get; init; }
    public int? BodyDrainingMs { get; init; }
    // False for background preparation no reader was waiting on.
    public bool ReaderBlocked { get; init; } = true;
    // "cancelled" or "faulted" when the wait ended without data; null when it completed.
    public string? Outcome { get; init; }
}

/// <param name="WaitPhase">Phase of the reader's in-progress wait, or null when not waiting.</param>
internal readonly record struct StreamTracePipelineSample(
    Guid SessionId,
    long? RangeGeneration,
    int PipelineId,
    int? PartIndex,
    int SegmentIndex,
    int QueuedSegments,
    int AwaitingSegments,
    int RespondedAhead,
    int ActiveBatches,
    int? BatchSize,
    long InFlightBytes,
    string? WaitPhase,
    int? WaitMs);

/// <param name="AdmissionFree">Transfer slots admission would grant right now, summed across providers.</param>
internal readonly record struct StreamTracePoolProbe(
    int? Active, int? Live, int? Max, int? AdmissionFree, int? AdmissionWaiting);
