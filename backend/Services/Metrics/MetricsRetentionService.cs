using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Services.Metrics;

/// <summary>
/// Enforces retention windows on the metrics database. Raw fetch events have
/// the shortest TTL (configurable, default 24 h) since the rollups already carry
/// the aggregate information; minute rollups keep a week; hour rollups stay a year;
/// the daily catalogue snapshot is small enough to keep forever. Runs hourly,
/// re-claims free pages via incremental_vacuum on the same pass.
/// </summary>
public class MetricsRetentionService(ConfigManager configManager) : BackgroundService
{
    internal const int MinFetchRetentionHours = 1;

    private static readonly TimeSpan TickInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan EventTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan MinuteRollupTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan SessionTtl = TimeSpan.FromDays(90);
    private static readonly TimeSpan HourlyRollupTtl = TimeSpan.FromDays(365);
    private static readonly TimeSpan ArrImportEventTtl = TimeSpan.FromDays(90);

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

    private async Task SafeSweepAsync()
    {
        try
        {
            var fetchTtl = TimeSpan.FromHours(
                Math.Max(configManager.GetMetricsFetchRetentionHours(), MinFetchRetentionHours));
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await using var db = new MetricsDbContext();
            await SweepAsync(db, nowMs, fetchTtl).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ex.LogWarningKnownOrStack("MetricsRetentionService sweep failed.");
        }
    }

    internal const int SegmentFetchDeleteBatchSize = 50_000;
    private const int IncrementalVacuumPages = 4_000;

    internal static async Task SweepAsync(MetricsDbContext db, long nowMs, TimeSpan fetchTtl)
    {
        await DeleteSegmentFetchesInBatchesAsync(db, Cutoff(nowMs, fetchTtl)).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM MetricEvents WHERE At < {0}", Cutoff(nowMs, EventTtl)).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM ThroughputMinutes WHERE Minute < {0}", Cutoff(nowMs, MinuteRollupTtl)).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM ProviderMinutes WHERE Minute < {0}", Cutoff(nowMs, MinuteRollupTtl)).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM ReadSessions WHERE EndedAt < {0}", Cutoff(nowMs, SessionTtl)).ConfigureAwait(false);
        await FoldAndPruneProviderHourlyAsync(db, Cutoff(nowMs, HourlyRollupTtl)).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM FailoverMisses WHERE At < {0}", Cutoff(nowMs, fetchTtl)).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM FailoverHourly WHERE Hour < {0}", Cutoff(nowMs, HourlyRollupTtl)).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM ArrImportEvents WHERE ImportedAtMs < {0}", Cutoff(nowMs, ArrImportEventTtl))
            .ConfigureAwait(false);

        await db.Database.ExecuteSqlRawAsync($"PRAGMA incremental_vacuum({IncrementalVacuumPages});")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Rowid-batched DELETE so a multi-GB SegmentFetches sweep cannot hold the
    /// write lock past MetricsWriter's 5s busy_timeout. Matches
    /// <see cref="OverviewStatsReset.WipeProviderAsync"/>.
    /// </summary>
    private static async Task DeleteSegmentFetchesInBatchesAsync(MetricsDbContext db, long cutoff)
    {
        var batchSize = Math.Max(1, SegmentFetchDeleteBatchSize);
        int batch;
        do
        {
            batch = await db.Database.ExecuteSqlRawAsync(
                """
                DELETE FROM SegmentFetches WHERE rowid IN
                    (SELECT rowid FROM SegmentFetches WHERE At < {0} LIMIT {1})
                """,
                new object[] { cutoff, batchSize }).ConfigureAwait(false);
            if (batch > 0)
                await Task.Yield();
        } while (batch > 0);
    }

    private static async Task FoldAndPruneProviderHourlyAsync(MetricsDbContext db, long cutoff)
    {
        await using var transaction = await db.Database.BeginTransactionAsync().ConfigureAwait(false);
        try
        {
            var folds = await db.ProviderHourly
                .Where(h => h.Hour < cutoff)
                .GroupBy(h => h.Provider)
                .Select(g => new
                {
                    Provider = g.Key,
                    BytesFetched = g.Sum(x => x.BytesFetched),
                    Articles = g.Sum(x => x.Articles),
                    Misses = g.Sum(x => x.Misses),
                    Errors = g.Sum(x => x.Errors),
                    Retries = g.Sum(x => x.Retries),
                    SumDurationMs = g.Sum(x => x.SumDurationMs),
                    FailoverSaves = g.Sum(x => x.FailoverSaves),
                    FirstHour = g.Min(x => x.Hour),
                })
                .ToListAsync().ConfigureAwait(false);

            if (folds.Count > 0)
            {
                var providers = folds.Select(f => f.Provider).ToList();
                var existing = await db.ProviderLifetimeTotals
                    .Where(x => providers.Contains(x.Provider))
                    .ToDictionaryAsync(x => x.Provider)
                    .ConfigureAwait(false);

                foreach (var fold in folds)
                {
                    if (!existing.TryGetValue(fold.Provider, out var total))
                    {
                        total = new ProviderLifetimeTotal { Provider = fold.Provider };
                        db.ProviderLifetimeTotals.Add(total);
                        existing[fold.Provider] = total;
                    }

                    total.BytesFetched += fold.BytesFetched;
                    total.Articles += fold.Articles;
                    total.Misses += fold.Misses;
                    total.Errors += fold.Errors;
                    total.Retries += fold.Retries;
                    total.SumDurationMs += fold.SumDurationMs;
                    total.FailoverSaves += fold.FailoverSaves;
                    total.FirstHour = total.FirstHour is null
                        ? fold.FirstHour
                        : Math.Min(total.FirstHour.Value, fold.FirstHour);
                }

                await db.SaveChangesAsync().ConfigureAwait(false);
            }

            await db.ProviderHourly
                .Where(h => h.Hour < cutoff)
                .ExecuteDeleteAsync().ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static long Cutoff(long nowMs, TimeSpan ttl) => nowMs - (long)ttl.TotalMilliseconds;
}
