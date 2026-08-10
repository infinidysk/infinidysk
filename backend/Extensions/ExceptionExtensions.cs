using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using NzbWebDAV.Exceptions;
using Serilog;

namespace NzbWebDAV.Extensions;

public static class ExceptionExtensions
{
    /// <summary>
    /// Operator-facing guidance logged when the database file itself is corrupt.
    /// Points at the guided restore flow and the recovery documentation.
    /// </summary>
    internal const string DatabaseCorruptionReason =
        "Database file is corrupt (SQLite error 11: database disk image is malformed). " +
        "Restore a backup from Settings → Backup & Restore, or see " +
        "https://www.infinidysk.com/operations/database-corruption/ for recovery steps.";

    public static bool IsRetryableDownloadException(this Exception exception)
    {
        return exception is RetryableDownloadException;
    }

    /// <summary>
    /// True when the exception chain contains SQLITE_CORRUPT (primary result code 11),
    /// meaning the database file itself is damaged. Transient busy/locked errors
    /// (codes 5/6/8/13) are deliberately excluded: corruption never heals on retry,
    /// while busy/locked conditions do.
    /// </summary>
    public static bool IsDatabaseCorruptionException(this Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            // SqliteErrorCode is the primary result code today (extended result codes
            // are never enabled in this stack). The mask keeps the check correct if
            // extended codes are ever turned on: SQLITE_CORRUPT_VTAB (267) and
            // SQLITE_CORRUPT_SEQUENCE (523) share primary code 11 in their low byte.
            if (current is SqliteException sqlite
                && (sqlite.SqliteErrorCode == 11 || (sqlite.SqliteExtendedErrorCode & 0xFF) == 11))
                return true;
        }

        return false;
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
    /// Returns a human-readable message for known/expected failures (transport,
    /// download, and database corruption) so callers can log a single line without
    /// a stack dump. Walks the exception chain and prefers the innermost matching
    /// message. Unexpected exceptions return false so full stack traces are preserved.
    /// </summary>
    public static bool TryGetKnownErrorMessage(this Exception exception, out string reason)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception.IsDatabaseCorruptionException())
        {
            reason = DatabaseCorruptionReason;
            return true;
        }

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
            or UnauthorizedAccessException
            or StreamingReadTimeoutException
            || exception.IsRetryableDownloadException()
            || exception.IsNonRetryableDownloadException();
    }

    public static bool TryGetCausingException<T>(this Exception exception, out T? exceptionType) where T : Exception
    {
        ArgumentNullException.ThrowIfNull(exception);

        // Depth-first walk so AggregateException inners are searched before
        // falling through to InnerException (which is often just the first).
        var stack = new Stack<Exception>();
        stack.Push(exception);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is T matching)
            {
                exceptionType = matching;
                return true;
            }

            if (current is AggregateException aggregate)
            {
                // Push in reverse so the first inner is examined first.
                var inners = aggregate.InnerExceptions;
                for (var i = inners.Count - 1; i >= 0; i--)
                    stack.Push(inners[i]);
            }
            else if (current.InnerException != null)
            {
                stack.Push(current.InnerException);
            }
        }

        exceptionType = null;
        return false;
    }
}
