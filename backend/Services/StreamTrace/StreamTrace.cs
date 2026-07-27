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

    public static void TryStall(Guid? sessionId, StreamStallKind kind, TimeSpan elapsed)
    {
        if (sessionId is { } id) _buffer?.AddStall(id, kind, elapsed);
    }

    public static void TryConnectionAcquired(Guid? sessionId, TimeSpan wait, bool wasReused)
    {
        if (sessionId is { } id) _buffer?.ConnectionAcquired(id, wait, wasReused);
    }

    /// <summary>
    /// True when a stall measurement is worth taking. Callers in hot loops check this
    /// before reading timestamps so tracing stays free when it is off.
    /// </summary>
    public static bool IsRecording => _buffer?.Enabled == true;
}
