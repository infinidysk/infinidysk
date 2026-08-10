using NzbWebDAV.Extensions;
using NzbWebDAV.Logging;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Shared error handling for background services that poll the main database in a
/// loop. Database corruption (SQLITE_CORRUPT) never heals between retries, so it is
/// logged on a long throttle and retried on a long delay instead of repeating a log
/// line every few seconds for the lifetime of the process. All other exceptions keep
/// the caller's normal delay.
/// </summary>
internal static class BackgroundServiceErrorHandler
{
    /// <summary>Retry delay used while the database is corrupt.</summary>
    public static readonly TimeSpan CorruptionDelay = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan CorruptionLogInterval = TimeSpan.FromMinutes(5);

    // Keyed per message template, so each calling service gets its own throttle window.
    private static readonly LogThrottle CorruptionLogThrottle = new();

    /// <summary>
    /// Logs the loop failure and returns how long the loop should wait before its next
    /// iteration. Corruption is throttled (with a suppressed-repeat count) and backed
    /// off; everything else goes through <see cref="ExceptionExtensions.LogWarningKnownOrStack"/>
    /// and keeps <paramref name="normalDelay"/>.
    /// </summary>
    public static TimeSpan LogAndGetRetryDelay(Exception exception, string messageTemplate, TimeSpan normalDelay)
    {
        if (!exception.IsDatabaseCorruptionException())
        {
            exception.LogWarningKnownOrStack(messageTemplate);
            return normalDelay;
        }

        if (CorruptionLogThrottle.ShouldLog(messageTemplate, CorruptionLogInterval, out var suppressed))
        {
            // Always resolves for corruption (IsDatabaseCorruptionException was true).
            exception.TryGetKnownErrorMessage(out var reason);
            if (suppressed > 0)
            {
                Log.Warning(
                    messageTemplate + " Reason: {Reason} (suppressed {Suppressed} repeats)",
                    reason,
                    suppressed);
            }
            else
            {
                Log.Warning(messageTemplate + " Reason: {Reason}", reason);
            }
        }

        return CorruptionDelay;
    }
}
