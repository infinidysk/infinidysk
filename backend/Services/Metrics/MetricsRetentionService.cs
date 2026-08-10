using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Services.Metrics;

/// <summary>
/// Enforces retention windows on the metrics database. Raw fetch events have
/// the shortest TTL (24 h) since the rollups already carry the aggregate
/// information; minute rollups keep a week; hour rollups stay a year; the
/// daily catalogue snapshot is small enough to keep forever. Runs hourly,
/// re-claims free pages via incremental_vacuum on the same pass.
/// </summary>
public class MetricsRetentionService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan FetchTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan EventTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan MinuteRollupTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan SessionTtl = TimeSpan.FromDays(90);
    private static readonly TimeSpan HourlyRollupTtl = TimeSpan.FromDays(365);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run once at startup to clean up across a downtime.
        await SafeSweepAsync().ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
                await SafeSweepAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
            {
                return;
            }
        }
    }

    private static async Task SafeSweepAsync()
    {
        try
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await using var db = new MetricsDbContext();
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM SegmentFetches WHERE At < {0}", Cutoff(nowMs, FetchTtl)).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM MetricEvents WHERE At < {0}", Cutoff(nowMs, EventTtl)).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM ThroughputMinutes WHERE Minute < {0}", Cutoff(nowMs, MinuteRollupTtl)).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM ProviderMinutes WHERE Minute < {0}", Cutoff(nowMs, MinuteRollupTtl)).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM ReadSessions WHERE EndedAt < {0}", Cutoff(nowMs, SessionTtl)).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM ProviderHourly WHERE Hour < {0}", Cutoff(nowMs, HourlyRollupTtl)).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM FailoverMisses WHERE At < {0}", Cutoff(nowMs, FetchTtl)).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM FailoverHourly WHERE Hour < {0}", Cutoff(nowMs, HourlyRollupTtl)).ConfigureAwait(false);

            await db.Database.ExecuteSqlRawAsync("PRAGMA incremental_vacuum;").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ex.LogWarningKnownOrStack("MetricsRetentionService sweep failed.");
        }
    }

    private static long Cutoff(long nowMs, TimeSpan ttl) => nowMs - (long)ttl.TotalMilliseconds;
}
