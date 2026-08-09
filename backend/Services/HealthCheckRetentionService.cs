using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Prunes aged HealthCheckResults so the table does not grow without bound.
/// HealthCheckStats stay consistent via existing AFTER DELETE triggers.
/// Inspired by elfhosted/nzbdav database maintenance (PR #199 retention idea).
/// </summary>
public class HealthCheckRetentionService(ConfigManager configManager) : BackgroundService
{
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
        DavDatabaseContext dbContext,
        int retentionDays,
        CancellationToken ct)
    {
        if (retentionDays <= 0) return 0;

        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        return await dbContext.HealthCheckResults
            .Where(x => x.CreatedAt < cutoff)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }

    private async Task SafeSweepAsync(CancellationToken stoppingToken)
    {
        try
        {
            var retentionDays = configManager.GetHealthResultRetentionDays();
            if (retentionDays <= 0) return;

            await using var dbContext = new DavDatabaseContext();
            var deleted = await SweepAsync(dbContext, retentionDays, stoppingToken).ConfigureAwait(false);
            if (deleted > 0)
            {
                Log.Information(
                    "Health-check retention removed {Deleted} rows older than {Days} days",
                    deleted,
                    retentionDays);
            }
        }
        catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Health-check retention sweep failed: {Message}", ex.Message);
        }
    }

    private static TimeSpan GetInterval()
    {
        var hours = EnvironmentUtil.GetLongVariable("DATABASE_MAINTENANCE_INTERVAL_HOURS") ?? 6;
        return TimeSpan.FromHours(Math.Max(1, hours));
    }
}
