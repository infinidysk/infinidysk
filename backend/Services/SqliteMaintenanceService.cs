using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using NzbWebDAV.Utils;
using Serilog;

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
            await SafeSweepAsync().ConfigureAwait(false);

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

    private async Task SafeSweepAsync()
    {
        try
        {
            await using var db = dbContextFactory.CreateDbContext();
            await using var metrics = new MetricsDbContext();
            await SweepAsync(db, metrics).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            Log.Warning(ex, "SqliteMaintenanceService sweep failed");
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
