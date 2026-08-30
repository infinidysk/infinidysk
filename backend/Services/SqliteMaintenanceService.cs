using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Services;

/// <summary>
/// Periodically runs <c>PRAGMA optimize</c> so the query planner has fresh
/// <c>sqlite_stat1</c> statistics, and checkpoints the main DB WAL so it does
/// not stay ballooned after bulk imports. First sweep ~2 minutes after startup,
/// then every 6 hours.
/// </summary>
public class SqliteMaintenanceService(IDbContextFactory<DavDatabaseContext> dbContextFactory) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TickInterval = TimeSpan.FromHours(6);

    internal const int MaxTransientRetries = 3;
    internal static readonly TimeSpan TransientRetryBaseDelay = TimeSpan.FromMilliseconds(250);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered() || stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SafeSweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                SigtermUtil.IsSigtermTriggered() || stoppingToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered() || stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private Task SafeSweepAsync(CancellationToken stoppingToken) =>
        RunWithSqliteContentionRetryAsync(
            async cancellationToken =>
            {
                await using var db = dbContextFactory.CreateDbContext();
                await using var metrics = new MetricsDbContext();
                await SweepAsync(db, metrics, cancellationToken).ConfigureAwait(false);
            },
            stoppingToken);

    internal static async Task RunWithSqliteContentionRetryAsync(
        Func<CancellationToken, Task> sweep,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        delayAsync ??= static (delay, token) => Task.Delay(delay, token);

        var maxAttempts = MaxTransientRetries + 1;
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await sweep(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (
                ex is not OutOfMemoryException &&
                ex.IsSqliteBusyOrLockedException())
            {
                // If shutdown raced the SQLite failure, convert it to normal
                // cancellation instead of retrying or treating contention as success.
                cancellationToken.ThrowIfCancellationRequested();

                if (attempt >= maxAttempts)
                {
                    ex.LogWarningKnownOrStack(
                        "SQLite maintenance sweep skipped after {AttemptCount} attempts; " +
                        "the next scheduled sweep will try again",
                        attempt);
                    return;
                }

                var delay = TimeSpan.FromMilliseconds(
                    TransientRetryBaseDelay.TotalMilliseconds * attempt);

                // Warn on first observation only. The terminal branch above warns
                // separately if all attempts are exhausted.
                if (attempt == 1)
                {
                    ex.LogWarningKnownOrStack(
                        "SQLite maintenance sweep deferred by database contention " +
                        "(attempt {Attempt}/{MaxAttempts}); retrying in {RetryDelayMs} ms",
                        attempt,
                        maxAttempts,
                        (long)delay.TotalMilliseconds);
                }

                await delayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Runs analysis_limit + optimize on both databases and truncating WAL
    /// checkpoint on the main database. Exposed for tests.
    /// </summary>
    internal static async Task SweepAsync(
        DavDatabaseContext db,
        MetricsDbContext metrics,
        CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA analysis_limit = 400;", cancellationToken)
                .ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync("PRAGMA optimize;", cancellationToken)
                .ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken)
                .ConfigureAwait(false);
        }

        await metrics.Database.ExecuteSqlRawAsync("PRAGMA analysis_limit = 400;", cancellationToken)
            .ConfigureAwait(false);
        await metrics.Database.ExecuteSqlRawAsync("PRAGMA optimize;", cancellationToken)
            .ConfigureAwait(false);
    }
}
