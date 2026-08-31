using System.Runtime.ExceptionServices;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// Observes sibling batch-lifecycle tasks without dropping fatal failures.
/// Fatality controls what is rethrown, not whether remaining work is awaited.
/// </summary>
internal static class BatchLifecycle
{
    public static ExceptionDispatchInfo PreferFailure(
        ExceptionDispatchInfo? current,
        Exception candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (current is null ||
            (candidate is OutOfMemoryException &&
             current.SourceException is not OutOfMemoryException))
        {
            return ExceptionDispatchInfo.Capture(candidate);
        }

        return current;
    }

    public static ExceptionDispatchInfo? Combine(
        ExceptionDispatchInfo? current,
        ExceptionDispatchInfo? candidate)
    {
        if (candidate is null)
            return current;
        return PreferFailure(current, candidate.SourceException);
    }

    public static async Task<ExceptionDispatchInfo?> ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return ExceptionDispatchInfo.Capture(exception);
        }
    }

    public static async Task ObserveAllAsync(Task first, Task second)
    {
        var failure = Combine(
            await ObserveAsync(first).ConfigureAwait(false),
            await ObserveAsync(second).ConfigureAwait(false));
        failure?.Throw();
    }
}
