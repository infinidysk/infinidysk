namespace NzbWebDAV.Exceptions;

/// <summary>
/// Signals that a maintenance task's bounded contention-retry policy exhausted its attempts
/// or unit-time budget while waiting on transient database contention (busy/locked/serialization
/// failures). Distinct from cancellation: this is a policy timeout, not a caller/shutdown abort.
/// </summary>
public sealed class CleanupContentionException(string phase, int attempts, Exception? innerException)
    : Exception(
        $"Cleanup phase '{phase}' exhausted contention retries after {attempts} attempt(s).",
        innerException)
{
    public string Phase { get; } = phase;
    public int Attempts { get; } = attempts;
}
