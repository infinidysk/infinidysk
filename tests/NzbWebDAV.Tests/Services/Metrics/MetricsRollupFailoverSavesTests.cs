using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Services.Metrics;

namespace NzbWebDAV.Tests.Services.Metrics;

/// <summary>
/// Backup rescues (ProviderMinutes.FailoverSaves) come from one event per rescue,
/// not from SegmentFetch.Retries. Same-provider self-retries keep Retries visible
/// on the scoreboard without inflating Overview Backup rescues.
/// </summary>
public sealed class MetricsRollupFailoverSavesTests
{
    private const long Minute = 1_700_000_060_000L;
    private const long OneMinuteMs = 60_000;
    private const long OneHourMs = 60 * OneMinuteMs;
    private const long ReplayHour = 100 * OneHourMs;
    private const long CurrentMinute = ReplayHour + 26 * OneMinuteMs;
    private const long TickNow = CurrentMinute + 30_000;
    private const long TargetMinute = CurrentMinute - OneMinuteMs;
    private const long OldestMinute = CurrentMinute - OneHourMs;
    private const long MissedMinute = TargetMinute - 5 * OneMinuteMs;

    [Fact]
    public async Task RollupMinute_SameProviderRetry_PreservesRetriesWithoutFailoverSave()
    {
        await withMetricsDb(async context =>
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO SegmentFetches
                    (At, Provider, ReadSessionId, QueueItemId, Bytes, DurationMs, Status, Retries)
                VALUES
                    ({0}, 'solo', NULL, NULL, 0, 50, 2, 0),
                    ({1}, 'solo', NULL, NULL, 0, 20, 0, 1);
                """,
                Minute + 1, Minute + 2);

            await MetricsRollupService.RollupMinuteAsync(context, Minute);

            var row = await context.ProviderMinutes.AsNoTracking().SingleAsync();
            Assert.Equal("solo", row.Provider);
            Assert.Equal(1, row.Retries);
            Assert.Equal(0, row.FailoverSaves);
            Assert.Equal(2, row.Articles);
        });
    }

    [Fact]
    public async Task RollupMinute_CrossProviderRescue_CountsDistinctFailoverSave()
    {
        await withMetricsDb(async context =>
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO SegmentFetches
                    (At, Provider, ReadSessionId, QueueItemId, Bytes, DurationMs, Status, Retries)
                VALUES
                    ({0}, 'primary', NULL, NULL, 0, 50, 1, 0),
                    ({1}, 'backup', NULL, NULL, 0, 30, 0, 1);

                INSERT INTO FailoverMisses
                    (At, FromProvider, ToProvider, Reason)
                VALUES
                    ({2}, 'primary', 'backup', 1);

                INSERT INTO MetricEvents
                    (At, Kind, RefId, Tag1, Tag2, Num, Note)
                VALUES
                    ({2}, 'failover-save', NULL, 'backup', NULL, NULL, NULL);
                """,
                Minute + 1, Minute + 2, Minute + 2);

            await MetricsRollupService.RollupMinuteAsync(context, Minute);

            var rows = await context.ProviderMinutes.AsNoTracking()
                .OrderBy(r => r.Provider)
                .ToListAsync();
            Assert.Equal(2, rows.Count);

            var backup = Assert.Single(rows, r => r.Provider == "backup");
            Assert.Equal(1, backup.Retries);
            Assert.Equal(1, backup.FailoverSaves);

            var primary = Assert.Single(rows, r => r.Provider == "primary");
            Assert.Equal(0, primary.FailoverSaves);
        });
    }

    [Fact]
    public async Task RollupMinute_ConcurrentSavesWithSameTimestamp_CountSeparately()
    {
        await withMetricsDb(async context =>
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO SegmentFetches
                    (At, Provider, ReadSessionId, QueueItemId, Bytes, DurationMs, Status, Retries)
                VALUES
                    ({0}, 'rescuer', NULL, NULL, 0, 30, 0, 2);

                INSERT INTO FailoverMisses
                    (At, FromProvider, ToProvider, Reason)
                VALUES
                    ({1}, 'a', 'rescuer', 2),
                    ({1}, 'b', 'rescuer', 1);

                INSERT INTO MetricEvents
                    (At, Kind, RefId, Tag1, Tag2, Num, Note)
                VALUES
                    ({1}, 'failover-save', NULL, 'rescuer', NULL, NULL, NULL),
                    ({1}, 'failover-save', NULL, 'rescuer', NULL, NULL, NULL);
                """,
                Minute + 1, Minute + 5);

            await MetricsRollupService.RollupMinuteAsync(context, Minute);

            var row = await context.ProviderMinutes.AsNoTracking().SingleAsync();
            Assert.Equal(2, row.Retries);
            Assert.Equal(2, row.FailoverSaves);
        });
    }

    [Fact]
    public async Task RollupMinute_RetainedSourceRows_PreservesFinalizedClientArticles()
    {
        await withMetricsDb(async context =>
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO SegmentFetches
                    (At, Provider, ReadSessionId, QueueItemId, Workload, Bytes, DurationMs, Status, Retries)
                VALUES
                    ({0}, 'streaming', NULL, NULL, 1, 0, 20, 0, 0),
                    ({1}, 'streaming', NULL, NULL, 1, 0, 30, 0, 0);
                """,
                Minute + 1, Minute + 2);

            await MetricsRollupService.RollupMinuteAsync(context, Minute);
            await context.Database.ExecuteSqlRawAsync(
                "DELETE FROM SegmentFetches WHERE At = {0}", Minute + 1);

            await MetricsRollupService.RollupMinuteAsync(context, Minute);

            var throughput = await context.ThroughputMinutes.AsNoTracking().SingleAsync();
            var provider = await context.ProviderMinutes.AsNoTracking().SingleAsync();
            Assert.Equal(2, throughput.ClientArticles);
            Assert.Equal(2, provider.ClientArticles);
            Assert.True(throughput.ClientArticlesFinalized);
            Assert.True(provider.ClientArticlesFinalized);
        });
    }

    [Fact]
    public async Task RollupMinute_LateClientFetch_IncreasesFinalizedClientArticles()
    {
        await withMetricsDb(async context =>
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO SegmentFetches
                    (At, Provider, ReadSessionId, QueueItemId, Workload, Bytes, DurationMs, Status, Retries)
                VALUES ({0}, 'streaming', NULL, NULL, 1, 0, 20, 0, 0);
                """,
                Minute + 1);

            await MetricsRollupService.RollupMinuteAsync(context, Minute);
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO SegmentFetches
                    (At, Provider, ReadSessionId, QueueItemId, Workload, Bytes, DurationMs, Status, Retries)
                VALUES ({0}, 'streaming', NULL, NULL, 1, 0, 30, 0, 0);
                """,
                Minute + 2);
            await MetricsRollupService.RollupMinuteAsync(context, Minute);

            var throughput = await context.ThroughputMinutes.AsNoTracking().SingleAsync();
            var provider = await context.ProviderMinutes.AsNoTracking().SingleAsync();
            Assert.Equal(2, throughput.ClientArticles);
            Assert.Equal(2, provider.ClientArticles);
        });
    }

    [Fact]
    public async Task RollupTick_FirstProcessTick_RecoversPersistedRawMinutes()
    {
        await withMetricsDb(async context =>
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO SegmentFetches
                    (At, Provider, ReadSessionId, QueueItemId, Workload,
                     Bytes, DurationMs, Status, Retries)
                VALUES
                    ({0}, 'provider-a', NULL, NULL, 1, 0, 20, 0, 0),
                    ({1}, 'provider-a', NULL, NULL, 1, 0, 90, 1, 0),
                    ({2}, 'provider-a', NULL, NULL, 1, 0, 70, 2, 1);

                INSERT INTO ReadSessions
                    (Id, StartedAt, EndedAt, DurationMs, Path,
                     BytesServed, BytesFetched, FailoverSaves, EndReason)
                VALUES
                    ({3}, {4}, {5}, 2000, '/synthetic/example.bin', 321, 0, 0, 0);

                INSERT INTO MetricEvents
                    (At, Kind, RefId, Tag1, Tag2, Num, Note)
                VALUES
                    ({6}, 'failover-save', NULL, 'provider-a', NULL, NULL, NULL);
                """,
                MissedMinute + 1, MissedMinute + 2, MissedMinute + 3,
                Guid.NewGuid(), MissedMinute, MissedMinute + 2000,
                MissedMinute + 4);

            Assert.False(await context.ThroughputMinutes.AnyAsync());
            Assert.False(await context.ProviderMinutes.AnyAsync());

            using var writer = new MetricsWriter(
                () => throw new InvalidOperationException("Test seeds metrics directly."));
            using var service = new MetricsRollupService(
                new ProviderBytesTracker(), new ProviderLatencyTracker(), writer);

            await service.RollupTickAsync(context, TickNow);

            var throughput = await context.ThroughputMinutes.AsNoTracking()
                .SingleAsync(row => row.Minute == MissedMinute);
            var provider = await context.ProviderMinutes.AsNoTracking()
                .SingleAsync(row => row.Minute == MissedMinute && row.Provider == "provider-a");

            Assert.Equal(3, throughput.Articles);
            Assert.Equal(3, throughput.ClientArticles);
            Assert.True(throughput.ClientArticlesFinalized);
            Assert.Equal(321, throughput.BytesServed);
            Assert.Equal(1, throughput.Misses);
            Assert.Equal(1, throughput.Errors);
            Assert.Equal(3, provider.Articles);
            Assert.Equal(3, provider.ClientArticles);
            Assert.True(provider.ClientArticlesFinalized);
            Assert.Equal(1, provider.Misses);
            Assert.Equal(1, provider.Errors);
            Assert.Equal(1, provider.Retries);
            Assert.Equal(1, provider.FailoverSaves);
            Assert.Equal(20, provider.SumDurationMs);
            Assert.Equal(3, await context.SegmentFetches.CountAsync());
            Assert.Equal(1, await context.ReadSessions.CountAsync());
        });
    }

    [Fact]
    public async Task RollupTick_FirstProcessTick_IsBoundedAndExcludesCurrentMinute()
    {
        await withMetricsDb(async context =>
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO SegmentFetches
                    (At, Provider, ReadSessionId, QueueItemId, Workload,
                     Bytes, DurationMs, Status, Retries)
                VALUES
                    ({0}, 'provider-a', NULL, NULL, 1, 0, 20, 0, 0),
                    ({1}, 'provider-a', NULL, NULL, 1, 0, 20, 0, 0),
                    ({2}, 'provider-a', NULL, NULL, 1, 0, 20, 0, 0);
                """,
                OldestMinute - OneMinuteMs + 1, OldestMinute + 1, CurrentMinute + 1);

            using var writer = new MetricsWriter(
                () => throw new InvalidOperationException("Test seeds metrics directly."));
            using var service = new MetricsRollupService(
                new ProviderBytesTracker(), new ProviderLatencyTracker(), writer);

            await service.RollupTickAsync(context, TickNow);

            Assert.Equal(60, await context.ThroughputMinutes.CountAsync());
            Assert.Equal(OldestMinute,
                await context.ThroughputMinutes.MinAsync(row => row.Minute));
            Assert.Equal(TargetMinute,
                await context.ThroughputMinutes.MaxAsync(row => row.Minute));
            Assert.Equal(1, await context.ThroughputMinutes.AsNoTracking()
                .Where(row => row.Minute == OldestMinute).Select(row => row.Articles).SingleAsync());
            Assert.False(await context.ThroughputMinutes.AnyAsync(row =>
                row.Minute < OldestMinute || row.Minute > TargetMinute));
            Assert.False(await context.ProviderMinutes.AnyAsync(row =>
                row.Minute < OldestMinute || row.Minute > TargetMinute));
            Assert.Equal(3, await context.SegmentFetches.CountAsync());
        });
    }

    [Fact]
    public async Task RollupTick_StartupReplay_IsIdempotentAndPreservesByteCounters()
    {
        await withMetricsDb(async context =>
        {
            var minute = ReplayHour - OneMinuteMs;
            context.ThroughputMinutes.Add(new ThroughputMinute
            {
                Minute = minute,
                BytesFetched = 1234,
            });
            context.ProviderMinutes.Add(new ProviderMinute
            {
                Minute = minute,
                Provider = "provider-a",
                BytesFetched = 1234,
            });
            context.ProviderHourly.Add(new ProviderHourly
            {
                Hour = ReplayHour - OneHourMs,
                Provider = "provider-a",
                BytesFetched = 1234,
            });
            await context.SaveChangesAsync();

            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO SegmentFetches
                    (At, Provider, ReadSessionId, QueueItemId, Workload,
                     Bytes, DurationMs, Status, Retries)
                VALUES ({0}, 'provider-a', NULL, NULL, 1, 0, 20, 0, 0);

                INSERT INTO ReadSessions
                    (Id, StartedAt, EndedAt, DurationMs, Path,
                     BytesServed, BytesFetched, FailoverSaves, EndReason)
                VALUES ({1}, {2}, {3}, 2000, '/synthetic/example.bin', 321, 0, 0, 0);
                """,
                minute + 1, Guid.NewGuid(), minute, minute + 2000);

            using (var writer = new MetricsWriter(
                       () => throw new InvalidOperationException("Test seeds metrics directly.")))
            using (var service = new MetricsRollupService(
                       new ProviderBytesTracker(), new ProviderLatencyTracker(), writer))
            {
                await service.RollupTickAsync(context, TickNow);
            }

            var throughputBefore = await context.ThroughputMinutes.AsNoTracking()
                .Where(row => row.Minute == minute)
                .Select(row => new
                {
                    row.Minute, row.BytesServed, row.BytesFetched, row.Articles,
                    row.ClientArticles, row.ClientArticlesFinalized, row.Misses,
                    row.Errors, row.ActiveReadsMax,
                }).SingleAsync();
            var providerBefore = await context.ProviderMinutes.AsNoTracking()
                .Where(row => row.Minute == minute && row.Provider == "provider-a")
                .Select(row => new
                {
                    row.Minute, row.Provider, row.Articles, row.ClientArticles,
                    row.ClientArticlesFinalized, row.BytesFetched, row.Misses,
                    row.Errors, row.Retries, row.FailoverSaves, row.SumDurationMs,
                    Hist = row.Hist == null ? null : Convert.ToHexString(row.Hist),
                }).SingleAsync();
            var hourlyBefore = await context.ProviderHourly.AsNoTracking()
                .Where(row => row.Hour == ReplayHour - OneHourMs && row.Provider == "provider-a")
                .Select(row => new
                {
                    row.Hour, row.Provider, row.Articles, row.ClientArticles,
                    row.BytesFetched, row.Misses, row.Errors, row.Retries,
                    row.FailoverSaves, row.SumDurationMs, row.P95DurationMs,
                }).SingleAsync();

            using (var writer = new MetricsWriter(
                       () => throw new InvalidOperationException("Test seeds metrics directly.")))
            using (var service = new MetricsRollupService(
                       new ProviderBytesTracker(), new ProviderLatencyTracker(), writer))
            {
                await service.RollupTickAsync(context, TickNow);
            }

            var throughputAfter = await context.ThroughputMinutes.AsNoTracking()
                .Where(row => row.Minute == minute)
                .Select(row => new
                {
                    row.Minute, row.BytesServed, row.BytesFetched, row.Articles,
                    row.ClientArticles, row.ClientArticlesFinalized, row.Misses,
                    row.Errors, row.ActiveReadsMax,
                }).SingleAsync();
            var providerAfter = await context.ProviderMinutes.AsNoTracking()
                .Where(row => row.Minute == minute && row.Provider == "provider-a")
                .Select(row => new
                {
                    row.Minute, row.Provider, row.Articles, row.ClientArticles,
                    row.ClientArticlesFinalized, row.BytesFetched, row.Misses,
                    row.Errors, row.Retries, row.FailoverSaves, row.SumDurationMs,
                    Hist = row.Hist == null ? null : Convert.ToHexString(row.Hist),
                }).SingleAsync();
            var hourlyAfter = await context.ProviderHourly.AsNoTracking()
                .Where(row => row.Hour == ReplayHour - OneHourMs && row.Provider == "provider-a")
                .Select(row => new
                {
                    row.Hour, row.Provider, row.Articles, row.ClientArticles,
                    row.BytesFetched, row.Misses, row.Errors, row.Retries,
                    row.FailoverSaves, row.SumDurationMs, row.P95DurationMs,
                }).SingleAsync();

            Assert.Equal(throughputBefore, throughputAfter);
            Assert.Equal(providerBefore, providerAfter);
            Assert.Equal(hourlyBefore, hourlyAfter);
            Assert.Equal(1234, throughputAfter.BytesFetched);
            Assert.Equal(1234, providerAfter.BytesFetched);
            Assert.Equal(1234, hourlyAfter.BytesFetched);
            Assert.Equal(1, await context.ThroughputMinutes.CountAsync(row => row.Minute == minute));
            Assert.Equal(1, await context.ProviderMinutes.CountAsync(row => row.Minute == minute));
            Assert.Equal(1, await context.ProviderHourly.CountAsync(row =>
                row.Hour == ReplayHour - OneHourMs));
        });
    }

    [Fact]
    public async Task RollupTick_StartupReplay_RebuildsProviderHourFromAllItsMinutes()
    {
        await withMetricsDb(async context =>
        {
            var oldMinute = ReplayHour - 50 * OneMinuteMs;
            var recoveredMinute = ReplayHour - OneMinuteMs;
            context.ProviderMinutes.AddRange(
                new ProviderMinute
                {
                    Minute = oldMinute,
                    Provider = "provider-a",
                    Articles = 7,
                    ClientArticles = 7,
                    BytesFetched = 700,
                },
                new ProviderMinute
                {
                    Minute = recoveredMinute,
                    Provider = "provider-a",
                    BytesFetched = 1234,
                });
            context.ProviderHourly.Add(new ProviderHourly
            {
                Hour = ReplayHour - OneHourMs,
                Provider = "provider-a",
                Articles = 7,
                ClientArticles = 7,
                BytesFetched = 700,
            });
            await context.SaveChangesAsync();

            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO SegmentFetches
                    (At, Provider, ReadSessionId, QueueItemId, Workload,
                     Bytes, DurationMs, Status, Retries)
                VALUES
                    ({0}, 'provider-a', NULL, NULL, 1, 0, 20, 0, 0),
                    ({1}, 'provider-a', NULL, NULL, 1, 0, 70, 1, 0);
                """,
                recoveredMinute + 1, recoveredMinute + 2);

            using var writer = new MetricsWriter(
                () => throw new InvalidOperationException("Test seeds metrics directly."));
            using var service = new MetricsRollupService(
                new ProviderBytesTracker(), new ProviderLatencyTracker(), writer);

            await service.RollupTickAsync(context, TickNow);

            var old = await context.ProviderMinutes.AsNoTracking()
                .SingleAsync(row => row.Minute == oldMinute && row.Provider == "provider-a");
            var recovered = await context.ProviderMinutes.AsNoTracking()
                .SingleAsync(row => row.Minute == recoveredMinute && row.Provider == "provider-a");
            var hourly = await context.ProviderHourly.AsNoTracking()
                .SingleAsync(row => row.Hour == ReplayHour - OneHourMs && row.Provider == "provider-a");

            Assert.Equal(7, old.Articles);
            Assert.Equal(7, old.ClientArticles);
            Assert.Equal(700, old.BytesFetched);
            Assert.Equal(2, recovered.Articles);
            Assert.Equal(2, recovered.ClientArticles);
            Assert.Equal(1, recovered.Misses);
            Assert.Equal(20, recovered.SumDurationMs);
            Assert.Equal(9, hourly.Articles);
            Assert.Equal(9, hourly.ClientArticles);
            Assert.Equal(1, hourly.Misses);
            Assert.Equal(20, hourly.SumDurationMs);
            Assert.Equal(1934, hourly.BytesFetched);
        });
    }

    [Fact]
    public async Task RollupTick_StartupReplay_PreservesHistoricalFailoverHourAfterRetention()
    {
        await withMetricsDb(async context =>
        {
            var hour = ReplayHour - OneHourMs;
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO FailoverMisses (At, FromProvider, ToProvider, Reason)
                VALUES
                    ({0}, 'primary', 'backup', 1),
                    ({1}, 'primary', 'backup', 1);

                INSERT INTO FailoverHourly (Hour, FromProvider, ToProvider, Reason, Count)
                VALUES ({2}, 'primary', 'backup', 1, 2);
                """,
                ReplayHour - 50 * OneMinuteMs,
                ReplayHour - 20 * OneMinuteMs,
                hour);

            await MetricsRetentionService.SweepAsync(
                context, TickNow - OneMinuteMs, TimeSpan.FromHours(1));
            Assert.Equal(1, await context.FailoverMisses.CountAsync());

            using var writer = new MetricsWriter(
                () => throw new InvalidOperationException("Test seeds metrics directly."));
            using var service = new MetricsRollupService(
                new ProviderBytesTracker(), new ProviderLatencyTracker(), writer);

            await service.RollupTickAsync(context, TickNow);

            var row = await context.FailoverHourly.AsNoTracking()
                .SingleAsync(item => item.Hour == hour);
            Assert.Equal(2, row.Count);
        });
    }

    [Fact]
    public async Task RollupTick_StartupReplay_CreatesMissingHistoricalFailoverHour()
    {
        await withMetricsDb(async context =>
        {
            var hour = ReplayHour - OneHourMs;
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO FailoverMisses (At, FromProvider, ToProvider, Reason)
                VALUES
                    ({0}, 'primary', 'backup', 1),
                    ({1}, 'primary', 'backup', 1);
                """,
                ReplayHour - 20 * OneMinuteMs,
                ReplayHour - 19 * OneMinuteMs);

            using (var writer = new MetricsWriter(
                       () => throw new InvalidOperationException("Test seeds metrics directly.")))
            using (var service = new MetricsRollupService(
                       new ProviderBytesTracker(), new ProviderLatencyTracker(), writer))
            {
                await service.RollupTickAsync(context, TickNow);
            }

            var row = await context.FailoverHourly.AsNoTracking()
                .SingleAsync(item => item.Hour == hour);
            Assert.Equal(2, row.Count);

            using (var writer = new MetricsWriter(
                       () => throw new InvalidOperationException("Test seeds metrics directly.")))
            using (var service = new MetricsRollupService(
                       new ProviderBytesTracker(), new ProviderLatencyTracker(), writer))
            {
                await service.RollupTickAsync(context, TickNow);
            }

            row = await context.FailoverHourly.AsNoTracking()
                .SingleAsync(item => item.Hour == hour);
            Assert.Equal(2, row.Count);
        });
    }

    [Fact]
    public async Task RollupTick_StartupReplay_SkipsFinalizedHistoricalMinutes()
    {
        await withMetricsDb(async context =>
        {
            context.ThroughputMinutes.Add(new ThroughputMinute
            {
                Minute = MissedMinute,
                Articles = 5,
                ClientArticles = 5,
                ClientArticlesFinalized = true,
                BytesServed = 321,
                Misses = 2,
                Errors = 1,
            });
            context.ProviderMinutes.Add(new ProviderMinute
            {
                Minute = MissedMinute,
                Provider = "provider-a",
                Articles = 5,
                ClientArticles = 5,
                ClientArticlesFinalized = true,
                Misses = 2,
                Errors = 1,
                SumDurationMs = 100,
            });
            await context.SaveChangesAsync();

            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO SegmentFetches
                    (At, Provider, ReadSessionId, QueueItemId, Workload,
                     Bytes, DurationMs, Status, Retries)
                VALUES ({0}, 'provider-a', NULL, NULL, 1, 0, 20, 0, 0);
                """,
                MissedMinute + 1);

            using var writer = new MetricsWriter(
                () => throw new InvalidOperationException("Test seeds metrics directly."));
            using var service = new MetricsRollupService(
                new ProviderBytesTracker(), new ProviderLatencyTracker(), writer);

            await service.RollupTickAsync(context, TickNow);

            var throughput = await context.ThroughputMinutes.AsNoTracking()
                .SingleAsync(row => row.Minute == MissedMinute);
            var provider = await context.ProviderMinutes.AsNoTracking()
                .SingleAsync(row => row.Minute == MissedMinute && row.Provider == "provider-a");
            Assert.Equal(5, throughput.Articles);
            Assert.Equal(5, throughput.ClientArticles);
            Assert.Equal(321, throughput.BytesServed);
            Assert.Equal(2, throughput.Misses);
            Assert.Equal(5, provider.Articles);
            Assert.Equal(5, provider.ClientArticles);
            Assert.Equal(2, provider.Misses);
            Assert.Equal(100, provider.SumDurationMs);

            Assert.True(await context.ThroughputMinutes.AsNoTracking()
                .AnyAsync(row => row.Minute == TargetMinute));
        });
    }

    [Fact]
    public async Task RollupTick_StartupReplay_ReplaysFinalizedMinuteWithMissingProviderRows()
    {
        await withMetricsDb(async context =>
        {
            context.ThroughputMinutes.Add(new ThroughputMinute
            {
                Minute = MissedMinute,
                Articles = 1,
                ClientArticles = 1,
                ClientArticlesFinalized = true,
            });
            await context.SaveChangesAsync();

            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO SegmentFetches
                    (At, Provider, ReadSessionId, QueueItemId, Workload,
                     Bytes, DurationMs, Status, Retries)
                VALUES ({0}, 'provider-a', NULL, NULL, 1, 0, 20, 0, 0);
                """,
                MissedMinute + 1);

            using var writer = new MetricsWriter(
                () => throw new InvalidOperationException("Test seeds metrics directly."));
            using var service = new MetricsRollupService(
                new ProviderBytesTracker(), new ProviderLatencyTracker(), writer);

            await service.RollupTickAsync(context, TickNow);

            var provider = await context.ProviderMinutes.AsNoTracking()
                .SingleAsync(row => row.Minute == MissedMinute && row.Provider == "provider-a");
            Assert.Equal(1, provider.Articles);
            Assert.Equal(1, provider.ClientArticles);
            Assert.True(provider.ClientArticlesFinalized);
        });
    }

    [Fact]
    public async Task RollupTick_StartupReplayFailureAtOldestMinute_RetriesOriginalWindow()
    {
        await withMetricsDb(async context =>
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO SegmentFetches
                    (At, Provider, ReadSessionId, QueueItemId, Workload,
                     Bytes, DurationMs, Status, Retries)
                VALUES
                    ({0}, 'provider-a', NULL, NULL, 1, 0, 20, 0, 0),
                    ({1}, 'provider-a', NULL, NULL, 1, 0, 20, 0, 0);
                """,
                OldestMinute + 1, TargetMinute + 1);

            using var writer = new MetricsWriter(
                () => throw new InvalidOperationException("Test seeds metrics directly."));
            using var service = new MetricsRollupService(
                new ProviderBytesTracker(), new ProviderLatencyTracker(), writer);

            await context.Database.OpenConnectionAsync();
            try
            {
                await context.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TEMP TABLE RollupFailureMinute (Minute INTEGER NOT NULL);
                    INSERT INTO RollupFailureMinute (Minute) VALUES ({0});

                    CREATE TEMP TRIGGER FailOldestProviderMinuteInsert
                    BEFORE INSERT ON ProviderMinutes
                    WHEN NEW.Minute = (SELECT Minute FROM RollupFailureMinute)
                    BEGIN
                        SELECT RAISE(ABORT, 'Synthetic rollup failure');
                    END;
                    """,
                    OldestMinute);

                await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
                    () => service.RollupTickAsync(context, TickNow));

                await context.Database.ExecuteSqlRawAsync(
                    """
                    DROP TRIGGER FailOldestProviderMinuteInsert;
                    DROP TABLE RollupFailureMinute;
                    """);
                await service.RollupTickAsync(context, TickNow + OneMinuteMs);

                var oldest = await context.ProviderMinutes.AsNoTracking()
                    .SingleAsync(row => row.Minute == OldestMinute && row.Provider == "provider-a");
                Assert.Equal(1, oldest.Articles);
                Assert.True(await context.ThroughputMinutes.AsNoTracking()
                    .AnyAsync(row => row.Minute == TargetMinute));
            }
            finally
            {
                await context.Database.CloseConnectionAsync();
            }
        });
    }

    [Fact]
    public async Task RollupTick_StartupReplayFailure_KeepsOriginalFailoverBoundary()
    {
        await withMetricsDb(async context =>
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO FailoverMisses (At, FromProvider, ToProvider, Reason)
                VALUES
                    ({0}, 'primary', 'backup', 1),
                    ({1}, 'primary', 'backup', 1);

                INSERT INTO FailoverHourly (Hour, FromProvider, ToProvider, Reason, Count)
                VALUES ({2}, 'primary', 'backup', 1, 2);

                INSERT INTO SegmentFetches
                    (At, Provider, ReadSessionId, QueueItemId, Workload,
                     Bytes, DurationMs, Status, Retries)
                VALUES
                    ({3}, 'provider-a', NULL, NULL, 1, 0, 20, 0, 0),
                    ({4}, 'provider-a', NULL, NULL, 1, 0, 20, 0, 0);
                """,
                ReplayHour - 50 * OneMinuteMs,
                ReplayHour - 20 * OneMinuteMs,
                ReplayHour - OneHourMs,
                ReplayHour - OneMinuteMs + 1,
                ReplayHour + 1);

            await MetricsRetentionService.SweepAsync(
                context, TickNow - OneMinuteMs, TimeSpan.FromHours(1));
            Assert.Equal(1, await context.FailoverMisses.CountAsync());

            using var writer = new MetricsWriter(
                () => throw new InvalidOperationException("Test seeds metrics directly."));
            using var service = new MetricsRollupService(
                new ProviderBytesTracker(), new ProviderLatencyTracker(), writer);

            await context.Database.OpenConnectionAsync();
            try
            {
                await context.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TEMP TABLE RollupFailureMinute (Minute INTEGER NOT NULL);
                    INSERT INTO RollupFailureMinute (Minute) VALUES ({0});

                    CREATE TEMP TRIGGER FailProviderMinuteInsert
                    BEFORE INSERT ON ProviderMinutes
                    WHEN NEW.Minute = (SELECT Minute FROM RollupFailureMinute)
                    BEGIN
                        SELECT RAISE(ABORT, 'Synthetic rollup failure');
                    END;
                    """,
                    ReplayHour);

                await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
                    () => service.RollupTickAsync(context, TickNow));
                Assert.True(await context.ProviderMinutes.AsNoTracking()
                    .AnyAsync(row => row.Minute == ReplayHour - OneMinuteMs));

                await context.Database.ExecuteSqlRawAsync(
                    """
                    DROP TRIGGER FailProviderMinuteInsert;
                    DROP TABLE RollupFailureMinute;
                    """);

                await service.RollupTickAsync(context, TickNow + OneMinuteMs);

                var boundaryMinute = await context.ProviderMinutes.AsNoTracking()
                    .SingleAsync(row => row.Minute == ReplayHour && row.Provider == "provider-a");
                var previousHour = await context.ProviderHourly.AsNoTracking()
                    .SingleAsync(row => row.Hour == ReplayHour - OneHourMs && row.Provider == "provider-a");
                var failoverHour = await context.FailoverHourly.AsNoTracking()
                    .SingleAsync(row => row.Hour == ReplayHour - OneHourMs);

                Assert.Equal(1, boundaryMinute.Articles);
                Assert.Equal(1, previousHour.Articles);
                Assert.Equal(20, previousHour.SumDurationMs);
                Assert.Equal(2, failoverHour.Count);
                Assert.True(await context.ThroughputMinutes.AsNoTracking()
                    .AnyAsync(row => row.Minute == CurrentMinute));
                Assert.Equal(61, await context.ThroughputMinutes.CountAsync());
            }
            finally
            {
                await context.Database.CloseConnectionAsync();
            }
        });
    }

    [Fact]
    public async Task RollupTick_SubsequentTick_ProcessesOnlyNewCompletedMinutes()
    {
        await withMetricsDb(async context =>
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO SegmentFetches
                    (At, Provider, ReadSessionId, QueueItemId, Workload,
                     Bytes, DurationMs, Status, Retries)
                VALUES ({0}, 'provider-a', NULL, NULL, 1, 0, 20, 0, 0);
                """,
                MissedMinute + 1);

            using var writer = new MetricsWriter(
                () => throw new InvalidOperationException("Test seeds metrics directly."));
            using var service = new MetricsRollupService(
                new ProviderBytesTracker(), new ProviderLatencyTracker(), writer);

            await service.RollupTickAsync(context, TickNow);
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO SegmentFetches
                    (At, Provider, ReadSessionId, QueueItemId, Workload,
                     Bytes, DurationMs, Status, Retries)
                VALUES
                    ({0}, 'provider-a', NULL, NULL, 1, 0, 30, 0, 0),
                    ({1}, 'provider-a', NULL, NULL, 1, 0, 30, 0, 0);
                """,
                MissedMinute + 2, CurrentMinute + 1);

            await service.RollupTickAsync(context, TickNow);
            Assert.Equal(60, await context.ThroughputMinutes.CountAsync());
            Assert.False(await context.ThroughputMinutes.AnyAsync(row => row.Minute == CurrentMinute));

            await service.RollupTickAsync(context, TickNow + OneMinuteMs);

            Assert.Equal(61, await context.ThroughputMinutes.CountAsync());
            Assert.Equal(1, await context.ThroughputMinutes.AsNoTracking()
                .Where(row => row.Minute == MissedMinute).Select(row => row.Articles).SingleAsync());
            Assert.Equal(1, await context.ThroughputMinutes.AsNoTracking()
                .Where(row => row.Minute == CurrentMinute).Select(row => row.Articles).SingleAsync());
        });
    }

    [Fact]
    public async Task RollupTick_LaterHourBoundary_StillRollsFailoverHour()
    {
        await withMetricsDb(async context =>
        {
            using var writer = new MetricsWriter(
                () => throw new InvalidOperationException("Test seeds metrics directly."));
            using var service = new MetricsRollupService(
                new ProviderBytesTracker(), new ProviderLatencyTracker(), writer);

            await service.RollupTickAsync(context, TickNow);
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO FailoverMisses (At, FromProvider, ToProvider, Reason)
                VALUES
                    ({0}, 'primary', 'backup', 1),
                    ({1}, 'primary', 'backup', 1);
                """,
                ReplayHour + 40 * OneMinuteMs,
                ReplayHour + 50 * OneMinuteMs);

            await service.RollupTickAsync(
                context, ReplayHour + OneHourMs + OneMinuteMs + 30_000);

            var row = await context.FailoverHourly.AsNoTracking()
                .SingleAsync(item => item.Hour == ReplayHour);
            Assert.Equal(2, row.Count);
        });
    }

    [Fact]
    public async Task RollupTick_FirstTargetOnHourBoundary_StillRollsFailoverHour()
    {
        await withMetricsDb(async context =>
        {
            var hour = ReplayHour - OneHourMs;
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO FailoverMisses (At, FromProvider, ToProvider, Reason)
                VALUES
                    ({0}, 'primary', 'backup', 1),
                    ({1}, 'primary', 'backup', 1);
                """,
                ReplayHour - 20 * OneMinuteMs,
                ReplayHour - 19 * OneMinuteMs);

            using var writer = new MetricsWriter(
                () => throw new InvalidOperationException("Test seeds metrics directly."));
            using var service = new MetricsRollupService(
                new ProviderBytesTracker(), new ProviderLatencyTracker(), writer);

            await service.RollupTickAsync(
                context, ReplayHour + OneMinuteMs + 30_000);

            var row = await context.FailoverHourly.AsNoTracking()
                .SingleAsync(item => item.Hour == hour);
            Assert.Equal(2, row.Count);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RollupTick_MinimumRetentionAndInitialDelay_PreserveOldestCompletedMinute(
        int configuredHours)
    {
        await withMetricsDb(async context =>
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO SegmentFetches
                    (At, Provider, ReadSessionId, QueueItemId, Workload,
                     Bytes, DurationMs, Status, Retries)
                VALUES
                    ({0}, 'provider-a', NULL, NULL, 1, 0, 20, 0, 0),
                    ({1}, 'provider-a', NULL, NULL, 1, 0, 20, 0, 0);
                """,
                OldestMinute - OneMinuteMs, OldestMinute + 1);

            var effectiveHours = Math.Max(
                configuredHours, MetricsRetentionService.MinFetchRetentionHours);
            await MetricsRetentionService.SweepAsync(
                context, TickNow - OneMinuteMs, TimeSpan.FromHours(effectiveHours));

            Assert.Equal(1, await context.SegmentFetches.CountAsync());
            Assert.True(await context.SegmentFetches.AnyAsync(row => row.At == OldestMinute + 1));

            using var writer = new MetricsWriter(
                () => throw new InvalidOperationException("Test seeds metrics directly."));
            using var service = new MetricsRollupService(
                new ProviderBytesTracker(), new ProviderLatencyTracker(), writer);

            await service.RollupTickAsync(context, TickNow);

            var oldest = await context.ThroughputMinutes.AsNoTracking()
                .SingleAsync(row => row.Minute == OldestMinute);
            Assert.Equal(1, oldest.Articles);

        });
    }

    [Fact]
    public async Task RollupMinute_SplitsQueueArticlesFromClientAndMaintenance()
    {
        await withMetricsDb(async context =>
        {
            // Workload: 1 = Streaming, 2 = Queue, 3 = Maintenance.
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO SegmentFetches
                    (At, Provider, ReadSessionId, QueueItemId, Workload, Bytes, DurationMs, Status, Retries)
                VALUES
                    ({0}, 'p', NULL, NULL, 1, 0, 20, 0, 0),
                    ({0}, 'p', NULL, NULL, 2, 0, 20, 0, 0),
                    ({0}, 'p', NULL, NULL, 2, 0, 20, 1, 0),
                    ({0}, 'p', NULL, NULL, 3, 0, 20, 0, 0);
                """,
                Minute + 1);

            await MetricsRollupService.RollupMinuteAsync(context, Minute);
            await MetricsRollupService.RollupHourAsync(context, Minute - (Minute % 3_600_000L));

            var throughput = await context.ThroughputMinutes.AsNoTracking().SingleAsync();
            var provider = await context.ProviderMinutes.AsNoTracking().SingleAsync();
            var hourly = await context.ProviderHourly.AsNoTracking().SingleAsync();
            Assert.Equal(4, throughput.Articles);
            Assert.Equal(1, throughput.ClientArticles);
            Assert.Equal(2, throughput.QueueArticles);
            Assert.Equal(2, provider.QueueArticles);
            Assert.Equal(2, hourly.QueueArticles);

            // Re-rolling after raw rows are pruned must keep the finalized queue count.
            await context.Database.ExecuteSqlRawAsync("DELETE FROM SegmentFetches");
            await MetricsRollupService.RollupMinuteAsync(context, Minute);

            throughput = await context.ThroughputMinutes.AsNoTracking().SingleAsync();
            provider = await context.ProviderMinutes.AsNoTracking().SingleAsync();
            Assert.Equal(4, throughput.Articles);
            Assert.Equal(4, provider.Articles);
            Assert.Equal(2, throughput.QueueArticles);
            Assert.Equal(2, provider.QueueArticles);
        });
    }

    private static async Task withMetricsDb(Func<MetricsDbContext, Task> body)
    {
        var databasePath = Path.Join(
            Path.GetTempPath(),
            $"nzbdav-metrics-rollup-{Guid.NewGuid():N}.sqlite");
        var options = new DbContextOptionsBuilder<MetricsDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .AddInterceptors(new SqliteMetricsPragmas())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;

        try
        {
            await using var context = new MetricsDbContext(options);
            await context.Database.MigrateAsync();
            await body(context);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }
}
