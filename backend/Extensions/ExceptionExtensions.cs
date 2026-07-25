using System.Net.Sockets;
using NzbWebDAV.Exceptions;
using Serilog;

namespace NzbWebDAV.Extensions;

public static class ExceptionExtensions
{
    public static bool IsRetryableDownloadException(this Exception exception)
    {
        return exception is RetryableDownloadException;
    }

    /// <summary>
    /// True when the exception chain indicates a transient transport failure that
    /// should pause-and-retry the queue item. Already-classified download exceptions
    /// return false (do not re-wrap retryable; never soften non-retryable).
    /// </summary>
    public static bool IsTransientTransportException(this Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is RetryableDownloadException or NonRetryableDownloadException)
                return false;
            if (current is TimeoutException or SocketException or IOException)
                return true;
        }
        return false;
    }

    public static bool IsNonRetryableDownloadException(this Exception exception)
    {
        return exception is NonRetryableDownloadException
            or SharpCompress.Common.InvalidFormatException
            or SharpCompress.Common.IncompleteArchiveException
            or SharpCompress.Common.Rar.RarHeaderReadException;
    }

    public static bool IsCancellationException(this Exception exception)
    {
        return exception is TaskCanceledException or OperationCanceledException;
    }

    public static bool IsCancellationException(
        this Exception exception,
        CancellationToken cancellationToken)
    {
        return exception.IsCancellationException() &&
            cancellationToken.IsCancellationRequested;
    }

    /// <summary>
    /// Returns a human-readable message for known/expected transport and download
    /// failures so callers can log a single line without a stack dump. Walks the
    /// exception chain and prefers the innermost matching message. Unexpected
    /// exceptions return false so full stack traces are preserved.
    /// </summary>
    public static bool TryGetKnownErrorMessage(this Exception exception, out string reason)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string? found = null;
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (IsKnownTransportOrDownloadException(current))
                found = current.Message;
        }

        if (found != null)
        {
            reason = found;
            return true;
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// Logs a Warning with only the known error reason when the exception is an
    /// expected transport/download failure; otherwise logs with the full stack.
    /// </summary>
    public static void LogWarningKnownOrStack(
        this Exception exception,
        string messageTemplate,
        params object?[] propertyValues)
    {
        if (exception.TryGetKnownErrorMessage(out var reason))
        {
            var args = new object?[propertyValues.Length + 1];
            propertyValues.CopyTo(args, 0);
            args[^1] = reason;
            Log.Warning(messageTemplate + " Reason: {Reason}", args);
            return;
        }

        Log.Warning(exception, messageTemplate, propertyValues);
    }

    private static bool IsKnownTransportOrDownloadException(Exception exception)
    {
        return exception is TimeoutException
            or SocketException
            or IOException
            or StreamingReadTimeoutException
            || exception.IsRetryableDownloadException()
            || exception.IsNonRetryableDownloadException();
    }

    public static bool TryGetCausingException<T>(this Exception exception, out T? exceptionType) where T : Exception
    {
        ArgumentNullException.ThrowIfNull(exception);
        var current = exception;

        while (current != null)
        {
            if (current is T matching)
            {
                exceptionType = matching;
                return true;
            }

            current = current.InnerException;
        }

        exceptionType = null;
        return false;
    }
}
