namespace NzbWebDAV.Exceptions;

/// <summary>
/// Thrown when a WebDAV/`/view` GET or range read stalls writing to the client for longer
/// than the configured streaming-write-timeout — the client stopped reading but kept the
/// connection open (HTTP/2 flow control, tunnel, or proxy), or trickled fewer than 64 KB
/// per timeout window while other streams were waiting on Article RAM. Treated as a
/// client-initiated abort (the response is a clean close, not a 500), and cancelling the
/// linked read token releases the stream's in-flight article budget so the host does not
/// wedge until restart.
/// </summary>
public class StreamingWriteTimeoutException(string message, Exception? innerException = null)
    : OperationCanceledException(message, innerException)
{
    public const string PerWriteStallReason = "streaming-write-timeout";
    public const string AggregateReclaimReason = "streaming-write-reclaim";

    /// <summary>
    /// Operator-facing reason token. Per-write stalls use
    /// <see cref="PerWriteStallReason"/>; contention-gated trickle reclaim uses
    /// <see cref="AggregateReclaimReason"/>.
    /// </summary>
    public string Reason { get; init; } = PerWriteStallReason;
}
