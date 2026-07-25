namespace NzbWebDAV.Exceptions;

/// <summary>
/// Thrown when a WebDAV/`/view` GET or range read exceeds the configured
/// streaming-read-timeout while waiting on NNTP admission (download semaphore,
/// connection-pool gate) or segment delivery. Distinct from a client-initiated
/// disconnect (<see cref="OperationCanceledException"/> with <c>RequestAborted</c>
/// cancelled) — this represents the backend failing to deliver within budget.
/// </summary>
public class StreamingReadTimeoutException(string message, Exception? innerException = null)
    : Exception(message, innerException)
{
}
