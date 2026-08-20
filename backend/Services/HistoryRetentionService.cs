using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Prunes aged SAB history rows without deleting mounted WebDAV content.
/// Uses RemoveHistoryItemsAsync(deleteFiles: false) so HistoryCleanupService
/// clears HistoryItemId links instead of removing DavItems.
/// Inspired by elfhosted/nzbdav database maintenance (PR #199 retention idea).
/// </summary>
public class HistoryRetentionService(
    ConfigManager configManager,
    IDbContextFactory<DavDatabaseContext> dbContextFactory) : BackgroundService
{
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SafeSweepAsync(stoppingToken).ConfigureAwait(false);

        var interval = GetInterval();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
                await SafeSweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
            {
                return;
            }
        }
    }

    internal static async Task<int> SweepAsync(
        DavDatabaseClient dbClient,
        int retentionDays,
        CancellationToken ct)
    {
        if (retentionDays <= 0) return 0;

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var totalRemoved = 0;

        while (true)
        {
            var ids = await dbClient.Ctx.HistoryItems
                .AsNoTracking()
                .Where(x => x.CreatedAt < cutoff)
                .OrderBy(x => x.CreatedAt)
                .Select(x => x.Id)
                .Take(BatchSize)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (ids.Count == 0) break;

            await dbClient.RemoveHistoryItemsAsync(
                    ids, deleteFiles: false, source: "history-retention", ct: ct)
                .ConfigureAwait(false);
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            dbClient.Ctx.ChangeTracker.Clear();
            totalRemoved += ids.Count;

            if (ids.Count < BatchSize) break;
        }

        return totalRemoved;
    }

    private async Task SafeSweepAsync(CancellationToken stoppingToken)
    {
        try
        {
            var retentionDays = configManager.GetHistoryRetentionDays();
            if (retentionDays <= 0) return;

            await using var dbContext = dbContextFactory.CreateDbContext();
            var dbClient = new DavDatabaseClient(dbContext);
            var removed = await SweepAsync(dbClient, retentionDays, stoppingToken).ConfigureAwait(false);
            if (removed > 0)
            {
                Log.Information(
                    "History retention removed {Removed} SAB history row(s) older than {Days} days (mounted files preserved)",
                    removed,
                    retentionDays);
            }
        }
        catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No delay here: the outer loop already waits the configured sweep
            // interval. The handler throttles corruption logging regardless.
            _ = BackgroundServiceErrorHandler.LogAndGetRetryDelay(ex, "History retention sweep failed.", TimeSpan.Zero);
        }
    }

    private static TimeSpan GetInterval()
    {
        var hours = EnvironmentUtil.GetLongVariable("DATABASE_MAINTENANCE_INTERVAL_HOURS") ?? 6;
        return TimeSpan.FromHours(Math.Max(1, hours));
    }
}
