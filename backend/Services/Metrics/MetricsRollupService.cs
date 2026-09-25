using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Services.Metrics;

/// <summary>
/// Materializes per-minute and per-hour rollups from the raw SegmentFetch /
/// ReadSession event tables. Runs once a minute, idempotently upserting the
/// last fully-elapsed minute. The first tick replays at most 60 completed
/// minutes after a restart. On the hour boundary it folds the 60 finished
/// minutes into ProviderHourly. Historical provider hours are rebuilt from
/// minute rows; existing older FailoverHourly rows are preserved because their
/// raw edges may have been pruned, while missing hours are still materialized.
/// Re-running any window is safe.
///
/// Errors are hard fetch failures only (Status NOT IN Ok/Missing). Expected
/// provider misses (Status = Missing) are counted separately as Misses.
/// </summary>
public class MetricsRollupService(
    ProviderBytesTracker bytesTracker,
    ProviderLatencyTracker latencyTracker,
    MetricsWriter metricsWriter
) : BackgroundService
{
    private const long OneMinute = 60_000;
    private const long OneHour = 60 * OneMinute;
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions CompactJson = new();

    private long _lastMinuteRolled;
    private long _startupTargetMinute;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
                await RollupTickAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ex.LogWarningKnownOrStack("MetricsRollupService tick failed.");
            }
        }
    }

    private async Task RollupTickAsync()
    {
        await using var db = new MetricsDbContext();
        await RollupTickAsync(db, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            .ConfigureAwait(false);
    }

    internal async Task RollupTickAsync(MetricsDbContext db, long nowMs)
    {
        var currentMinute = FloorTo(nowMs, OneMinute);
        var targetMinute = currentMinute - OneMinute;

        if (_startupTargetMinute == 0)
            _startupTargetMinute = targetMinute;

        var isStartupReplay = _lastMinuteRolled < _startupTargetMinute;
        var start = isStartupReplay
            ? (_lastMinuteRolled == 0
                ? _startupTargetMinute - 59 * OneMinute
                : _lastMinuteRolled + OneMinute)
            : Math.Max(_lastMinuteRolled + OneMinute, targetMinute - 59 * OneMinute);
        var end = isStartupReplay
            ? _startupTargetMinute
            : targetMinute;

        for (var minute = start; minute <= end; minute += OneMinute)
        {
            await RollupMinuteAndHoursAsync(db, minute, _startupTargetMinute)
                .ConfigureAwait(false);
        }

        if (isStartupReplay && targetMinute > _startupTargetMinute)
        {
            start = Math.Max(_lastMinuteRolled + OneMinute, targetMinute - 59 * OneMinute);
            for (var minute = start; minute <= targetMinute; minute += OneMinute)
            {
                await RollupMinuteAndHoursAsync(db, minute, _startupTargetMinute)
                    .ConfigureAwait(false);
            }
        }

        // After fetch-row rollups have written/refreshed ProviderMinute rows, fold in
        // the per-provider byte counts captured by the streaming wrapper. UPSERT-adds
        // so re-runs (catch-up after restart) are idempotent: any closed minute
        // contributes at most once because DrainClosed pops the bucket.
        await ApplyByteCountersAsync(db, currentMinute).ConfigureAwait(false);
        FlushClosedLatency(currentMinute);
    }

    private async Task RollupMinuteAndHoursAsync(
        MetricsDbContext db,
        long minute,
        long startupTargetMinute)
    {
        var isFinalizedStartupHistory = minute < startupTargetMinute &&
            await db.ThroughputMinutes
                .AnyAsync(row => row.Minute == minute && row.ClientArticlesFinalized)
                .ConfigureAwait(false) &&
            !await db.SegmentFetches.AnyAsync(fetch =>
                fetch.At >= minute && fetch.At < minute + OneMinute &&
                !db.ProviderMinutes.Any(provider =>
                    provider.Minute == minute && provider.Provider == fetch.Provider))
                .ConfigureAwait(false);
        if (!isFinalizedStartupHistory)
            await RollupMinuteAsync(db, minute).ConfigureAwait(false);

        if (minute % OneHour == 0 && minute > 0)
        {
            await RollupHourAsync(db, minute - OneHour).ConfigureAwait(false);
            if (minute >= startupTargetMinute ||
                !await db.FailoverHourly.AnyAsync(row => row.Hour == minute - OneHour)
                    .ConfigureAwait(false))
            {
                await RollupFailoverHourAsync(db, minute - OneHour)
                    .ConfigureAwait(false);
            }
        }
        _lastMinuteRolled = minute;
    }

    internal void FlushClosedLatency(long currentMinute)
    {
        var generation = metricsWriter.CaptureResetGeneration();
        foreach (var item in latencyTracker.PrepareClosed(currentMinute))
        {
            var value = new MetricEvent
            {
                At = item.Key.Minute,
                Kind = "latency",
                Tag1 = item.Key.ProviderKey,
                Tag2 = LatencyNames.ToWireName(item.Key.Phase),
                RefId = $"{LatencyNames.ToWireName(item.Key.Workload)}/" +
                        LatencyNames.ToWireName(item.Key.Operation),
                Num = item.Snapshot.Count,
                Note = JsonSerializer.Serialize(new LatencyHistogramPayload(
                    Version: 1,
                    Counts: item.Snapshot.Counts,
                    SumMs: item.Snapshot.SumMs,
                    MaxMs: item.Snapshot.MaxMs), CompactJson),
            };

            switch (metricsWriter.TryRecordEvent(value, generation))
            {
                case EventEnqueueResult.Accepted:
                    latencyTracker.Acknowledge(item.Key);
                    break;
                case EventEnqueueResult.ResetRejected:
                    break;
                case EventEnqueueResult.QueueFull:
                    break;
            }
        }
    }

    private async Task ApplyByteCountersAsync(MetricsDbContext db, long currentMinute)
    {
        var drained = bytesTracker.DrainClosed(currentMinute);

        foreach (var (minute, providerKey, bytes) in drained)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO ProviderMinutes
                    (Minute, Provider, Articles, BytesFetched, Misses, Errors, Retries, SumDurationMs, Hist)
                VALUES ({0}, {1}, 0, {2}, 0, 0, 0, 0, NULL)
                ON CONFLICT(Minute, Provider) DO UPDATE SET
                    BytesFetched = ProviderMinutes.BytesFetched + excluded.BytesFetched;
                """,
                minute, providerKey, bytes).ConfigureAwait(false);

            var hour = FloorTo(minute, OneHour);
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO ProviderHourly
                    (Hour, Provider, Articles, ClientArticles, BytesFetched, Misses, Errors, Retries, SumDurationMs, P95DurationMs)
                VALUES ({0}, {1}, 0, 0, {2}, 0, 0, 0, 0, NULL)
                ON CONFLICT(Hour, Provider) DO UPDATE SET
                    BytesFetched = ProviderHourly.BytesFetched + excluded.BytesFetched;
                """,
                hour, providerKey, bytes).ConfigureAwait(false);

            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO ThroughputMinutes
                    (Minute, BytesServed, BytesFetched, Articles, ClientArticles, Misses, Errors, ActiveReadsMax)
                VALUES ({0}, 0, {1}, 0, 0, 0, 0, 0)
                ON CONFLICT(Minute) DO UPDATE SET
                    BytesFetched = ThroughputMinutes.BytesFetched + excluded.BytesFetched;
                """,
                minute, bytes).ConfigureAwait(false);
        }

        await ApplyPendingPeakRatesAsync(db, bytesTracker, currentMinute).ConfigureAwait(false);
    }

    internal static async Task ApplyPendingPeakRatesAsync(
        MetricsDbContext db,
        ProviderBytesTracker bytesTracker,
        long currentMinute)
    {
        await bytesTracker.PeakPersistenceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var peaks = bytesTracker.DrainClosedPeaks(currentMinute);
            var providerRates = bytesTracker.DrainProviderRates(currentMinute);
            try
            {
                await ApplyPeakRatesAsync(db, peaks, providerRates).ConfigureAwait(false);
            }
            catch
            {
                bytesTracker.RestorePeaks(peaks);
                bytesTracker.RestoreProviderRates(providerRates);
                throw;
            }
        }
        finally
        {
            bytesTracker.PeakPersistenceGate.Release();
        }
    }

    // MAX-merge so catch-up re-runs and restarts can never lower a persisted peak.
    internal static async Task ApplyPeakRatesAsync(
        MetricsDbContext db,
        IReadOnlyList<(long Minute, long PeakBytesPerSec)> peaks,
        IReadOnlyList<ProviderBytesTracker.ProviderRateSample>? providerRates = null)
    {
        if (peaks.Count == 0 && (providerRates is null || providerRates.Count == 0)) return;

        await using var transaction = await db.Database.BeginTransactionAsync().ConfigureAwait(false);
        foreach (var (minute, peak) in peaks)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO ThroughputMinutes
                    (Minute, BytesServed, BytesFetched, Articles, ClientArticles, Misses, Errors, ActiveReadsMax, PeakFetchBytesPerSec)
                VALUES ({0}, 0, 0, 0, 0, 0, 0, 0, {1})
                ON CONFLICT(Minute) DO UPDATE SET
                    PeakFetchBytesPerSec = MAX(ThroughputMinutes.PeakFetchBytesPerSec, excluded.PeakFetchBytesPerSec);
                """,
                minute, peak).ConfigureAwait(false);

            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO ThroughputHourly (Hour, PeakFetchBytesPerSec)
                VALUES ({0}, {1})
                ON CONFLICT(Hour) DO UPDATE SET
                    PeakFetchBytesPerSec = MAX(ThroughputHourly.PeakFetchBytesPerSec, excluded.PeakFetchBytesPerSec);
                """,
                FloorTo(minute, OneHour), peak).ConfigureAwait(false);
        }

        foreach (var sample in providerRates ?? [])
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO ProviderMinutes
                    (Minute, Provider, Articles, BytesFetched, Misses, Errors, Retries, SumDurationMs,
                     PeakBytesPerSec, ActiveBytes, ActiveSeconds)
                VALUES ({0}, {1}, 0, 0, 0, 0, 0, 0, {2}, {3}, {4})
                ON CONFLICT(Minute, Provider) DO UPDATE SET
                    PeakBytesPerSec = MAX(COALESCE(ProviderMinutes.PeakBytesPerSec, 0), excluded.PeakBytesPerSec),
                    ActiveBytes = COALESCE(ProviderMinutes.ActiveBytes, 0) + excluded.ActiveBytes,
                    ActiveSeconds = COALESCE(ProviderMinutes.ActiveSeconds, 0) + excluded.ActiveSeconds;
                """,
                sample.Minute, sample.Provider, sample.PeakBytesPerSec, sample.ActiveBytes, sample.ActiveSeconds)
                .ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO ProviderHourly
                    (Hour, Provider, Articles, ClientArticles, BytesFetched, Misses, Errors, Retries, SumDurationMs,
                     PeakBytesPerSec, ActiveBytes, ActiveSeconds)
                VALUES ({0}, {1}, 0, 0, 0, 0, 0, 0, 0, {2}, {3}, {4})
                ON CONFLICT(Hour, Provider) DO UPDATE SET
                    PeakBytesPerSec = MAX(COALESCE(ProviderHourly.PeakBytesPerSec, 0), excluded.PeakBytesPerSec),
                    ActiveBytes = COALESCE(ProviderHourly.ActiveBytes, 0) + excluded.ActiveBytes,
                    ActiveSeconds = COALESCE(ProviderHourly.ActiveSeconds, 0) + excluded.ActiveSeconds;
                """,
                FloorTo(sample.Minute, OneHour), sample.Provider, sample.PeakBytesPerSec, sample.ActiveBytes, sample.ActiveSeconds)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync().ConfigureAwait(false);
    }

    internal static async Task RollupMinuteAsync(MetricsDbContext db, long minute)
    {
        var next = minute + OneMinute;
        var streamingWorkload = (int)SegmentFetch.FetchWorkload.Streaming;
        var queueWorkload = (int)SegmentFetch.FetchWorkload.Queue;

        // ProviderMinute: per-provider counters. BytesFetched intentionally omitted from
        // ON CONFLICT — the tracker is the sole writer of that column.
        // FailoverSaves come from one event per cross-provider rescue, not from
        // SegmentFetch.Retries — Retries still counts same-provider self-retries for the
        // scoreboard. FailoverMisses can contain multiple edges for one rescue.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ProviderMinutes (Minute, Provider, Articles, ClientArticles, QueueArticles, ClientArticlesFinalized, BytesFetched, Misses, Errors, Retries, FailoverSaves, SumDurationMs, Hist)
            SELECT {0}, Provider,
                COUNT(*),
                SUM(CASE WHEN Workload = {2} THEN 1 ELSE 0 END),
                SUM(CASE WHEN Workload = {4} THEN 1 ELSE 0 END),
                1,
                0,
                SUM(CASE WHEN Status = 1 THEN 1 ELSE 0 END),
                SUM(CASE WHEN Status NOT IN (0, 1) THEN 1 ELSE 0 END),
                SUM(Retries),
                (SELECT COUNT(*) FROM MetricEvents e
                    WHERE e.Kind = {3} AND e.Tag1 = SegmentFetches.Provider
                        AND e.At >= {0} AND e.At < {1}),
                -- Ok-only durations so Overview "Avg ok ms" is not inflated by misses/errors.
                SUM(CASE WHEN Status = 0 THEN DurationMs ELSE 0 END),
                NULL
            FROM SegmentFetches
            WHERE At >= {0} AND At < {1}
            GROUP BY Provider
            ON CONFLICT(Minute, Provider) DO UPDATE SET
                Articles      = MAX(ProviderMinutes.Articles, excluded.Articles),
                ClientArticles = MAX(ProviderMinutes.ClientArticles, excluded.ClientArticles),
                QueueArticles = MAX(ProviderMinutes.QueueArticles, excluded.QueueArticles),
                ClientArticlesFinalized = excluded.ClientArticlesFinalized,
                Misses        = excluded.Misses,
                Errors        = excluded.Errors,
                Retries       = excluded.Retries,
                FailoverSaves = excluded.FailoverSaves,
                SumDurationMs = excluded.SumDurationMs;
            """,
                minute, next, streamingWorkload, MetricsWriter.FailoverSaveEventKind, queueWorkload).ConfigureAwait(false);

        // BytesFetched and PeakFetchBytesPerSec are owned by ProviderBytesTracker.
        // Aggregate in a plain read first: WAL readers never block writers, so a slow
        // ReadSessions scan cannot hold the metrics write lock.
        var totals = (await db.Database.SqlQueryRaw<MinuteTotals>(
            """
            SELECT
                COALESCE((SELECT SUM(BytesServed) FROM ReadSessions WHERE EndedAt >= {0} AND EndedAt < {1}), 0) AS BytesServed,
                COUNT(*) AS Articles,
                COALESCE(SUM(CASE WHEN Workload = {2} THEN 1 ELSE 0 END), 0) AS ClientArticles,
                COALESCE(SUM(CASE WHEN Workload = {3} THEN 1 ELSE 0 END), 0) AS QueueArticles,
                COALESCE(SUM(CASE WHEN Status = 1 THEN 1 ELSE 0 END), 0) AS Misses,
                COALESCE(SUM(CASE WHEN Status NOT IN (0, 1) THEN 1 ELSE 0 END), 0) AS Errors
            FROM SegmentFetches
            WHERE At >= {0} AND At < {1}
            """,
            minute, next, streamingWorkload, queueWorkload)
            .ToListAsync().ConfigureAwait(false)).Single();

        // Write the completion marker after ProviderMinutes so a failed partial rollup
        // is retried rather than mistaken for finalized history during startup replay.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ThroughputMinutes (Minute, BytesServed, BytesFetched, Articles, ClientArticles, QueueArticles, ClientArticlesFinalized, Misses, Errors, ActiveReadsMax)
            VALUES ({0}, {1}, 0, {2}, {3}, {4}, 1, {5}, {6}, 0)
            ON CONFLICT(Minute) DO UPDATE SET
                BytesServed  = excluded.BytesServed,
                Articles     = MAX(ThroughputMinutes.Articles, excluded.Articles),
                ClientArticles = MAX(ThroughputMinutes.ClientArticles, excluded.ClientArticles),
                QueueArticles = MAX(ThroughputMinutes.QueueArticles, excluded.QueueArticles),
                ClientArticlesFinalized = excluded.ClientArticlesFinalized,
                Misses       = excluded.Misses,
                Errors       = excluded.Errors;
            """,
            minute, totals.BytesServed, totals.Articles, totals.ClientArticles, totals.QueueArticles,
            totals.Misses, totals.Errors).ConfigureAwait(false);
    }

    internal sealed class MinuteTotals
    {
        public long BytesServed { get; set; }
        public long Articles { get; set; }
        public long ClientArticles { get; set; }
        public long QueueArticles { get; set; }
        public long Misses { get; set; }
        public long Errors { get; set; }
    }

    internal static async Task RollupHourAsync(MetricsDbContext db, long hour)
    {
        var next = hour + OneHour;
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ProviderHourly (Hour, Provider, Articles, ClientArticles, QueueArticles, BytesFetched, Misses, Errors, Retries, FailoverSaves, SumDurationMs, P95DurationMs, PeakBytesPerSec, ActiveBytes, ActiveSeconds)
            SELECT {0}, Provider,
                SUM(Articles),
                SUM(ClientArticles),
                SUM(QueueArticles),
                SUM(BytesFetched),
                SUM(Misses),
                SUM(Errors),
                SUM(Retries),
                SUM(FailoverSaves),
                SUM(SumDurationMs),
                NULL,
                MAX(PeakBytesPerSec),
                SUM(ActiveBytes),
                SUM(ActiveSeconds)
            FROM ProviderMinutes
            WHERE Minute >= {0} AND Minute < {1}
            GROUP BY Provider
            ON CONFLICT(Hour, Provider) DO UPDATE SET
                Articles      = excluded.Articles,
                ClientArticles = excluded.ClientArticles,
                QueueArticles = excluded.QueueArticles,
                BytesFetched  = excluded.BytesFetched,
                Misses        = excluded.Misses,
                Errors        = excluded.Errors,
                Retries       = excluded.Retries,
                FailoverSaves = excluded.FailoverSaves,
                PeakBytesPerSec = excluded.PeakBytesPerSec,
                ActiveBytes = excluded.ActiveBytes,
                ActiveSeconds = excluded.ActiveSeconds,
                SumDurationMs = excluded.SumDurationMs;
            """,
            hour, next).ConfigureAwait(false);
    }

    private static async Task RollupFailoverHourAsync(MetricsDbContext db, long hour)
    {
        var next = hour + OneHour;
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO FailoverHourly (Hour, FromProvider, ToProvider, Reason, Count)
            SELECT {0}, FromProvider, ToProvider, Reason, COUNT(*)
            FROM FailoverMisses
            WHERE At >= {0} AND At < {1}
            GROUP BY FromProvider, ToProvider, Reason
            ON CONFLICT(Hour, FromProvider, ToProvider, Reason) DO UPDATE SET
                Count = excluded.Count;
            """,
            hour, next).ConfigureAwait(false);
    }

    private static long FloorTo(long value, long step) => value - (value % step);
}
