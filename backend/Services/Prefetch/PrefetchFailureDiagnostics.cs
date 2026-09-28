using NzbWebDAV.Exceptions;
using Serilog;

namespace NzbWebDAV.Services.Prefetch;

internal static class PrefetchFailureDiagnostics
{
    // Exception messages and inner exceptions can contain credentials, URLs and
    // media paths. Persist/log only stable guidance, type names and job identity.
    internal static string Report(ILogger logger, PrefetchJob job, Exception exception, bool retryable)
    {
        var (category, guidance) = exception switch
        {
            UsenetArticleNotFoundException => ("source-unavailable", "A required article is unavailable. Check source health before retrying."),
            SeekPositionNotFoundException => ("source-layout", "A source byte position could not be resolved. Check the imported source before retrying."),
            UnauthorizedAccessException => ("storage-access", "A file or cache access was denied. Check storage permissions before retrying."),
            OperationCanceledException => ("unexpected-cancellation", "A source operation was cancelled unexpectedly. Retry the job; inspect the correlated log if it repeats."),
            IOException => ("source-or-cache-io", "Source or cache I/O failed. Check source health and storage availability."),
            ArgumentException => ("invalid-request", "The warming request or imported media is no longer valid. Review the item and warming limits."),
            _ => ("unexpected-failure", "Warming failed unexpectedly. Inspect the correlated diagnostic log before retrying.")
        };
        var exceptionType = exception.GetType().Name;
        logger.Warning("Smart Prefetch job {JobId} for item {ItemId}: {FailureCategory} ({ExceptionType}). {Reason} Retry policy: {RetryPolicy}",
            job.Id, job.ItemId, category, exceptionType, guidance, retryable ? "bounded automatic retry" : "explicit retry required");
        // Preserve code frames for unexpected bugs without serializing the
        // exception itself, its message, Data, or secret-bearing inner exceptions.
        if (category is "unexpected-failure" or "unexpected-cancellation")
            logger.Debug("Smart Prefetch job {JobId} exception code frames: {StackTrace}", job.Id, exception.StackTrace);
        return $"[{category}/{exceptionType}] {guidance} " +
            (retryable ? "Verified coverage retained; automatic retries are bounded." : "Verified coverage retained; retry explicitly.");
    }
}
