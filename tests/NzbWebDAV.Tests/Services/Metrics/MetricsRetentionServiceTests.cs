using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Services.Metrics;

namespace NzbWebDAV.Tests.Services.Metrics;

public sealed class MetricsRetentionServiceTests
{
    private const long OneDayMs = 24 * 60 * 60 * 1000L;
    private const long OneHourMs = 60 * 60 * 1000L;

    [Fact]
    public async Task SweepAsync_FoldsPrunedProviderHourlyIntoLifetimeTotals()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var db = harness.Context;
        var nowMs = 400L * OneDayMs;
        var cutoff = MetricsRetentionService.Cutoff(nowMs, TimeSpan.FromDays(365));

        db.ProviderHourly.AddRange(
            new ProviderHourly
            {
                Hour = cutoff - OneDayMs,
                Provider = "provider-a",
                Articles = 10,
                BytesFetched = 100,
                Misses = 1,
                Errors = 2,
                Retries = 3,
                SumDurationMs = 40,
                FailoverSaves = 4,
            },
            new ProviderHourly
            {
                Hour = cutoff - 2 * OneDayMs,
                Provider = "provider-a",
                Articles = 5,
                BytesFetched = 50,
                FailoverSaves = 1,
            },
            new ProviderHourly
            {
                Hour = cutoff + OneDayMs,
                Provider = "provider-a",
                Articles = 7,
                BytesFetched = 70,
            },
            new ProviderHourly
            {
                Hour = cutoff - OneDayMs,
                Provider = "provider-b",
                Articles = 3,
                BytesFetched = 30,
                FailoverSaves = 2,
            });
        await db.SaveChangesAsync();

        await MetricsRetentionService.SweepAsync(db, nowMs, TimeSpan.FromHours(24));

        var lifetimeA = await db.ProviderLifetimeTotals.SingleAsync(x => x.Provider == "provider-a");
        Assert.Equal(15, lifetimeA.Articles);
        Assert.Equal(150, lifetimeA.BytesFetched);
        Assert.Equal(cutoff - 2 * OneDayMs, lifetimeA.FirstHour);
        Assert.Equal(1, await db.ProviderHourly.CountAsync());

        await MetricsRetentionService.SweepAsync(db, nowMs, TimeSpan.FromHours(24));
        var lifetimeAAgain = await db.ProviderLifetimeTotals.SingleAsync(x => x.Provider == "provider-a");
        Assert.Equal(lifetimeA.Articles, lifetimeAAgain.Articles);
        Assert.Equal(lifetimeA.BytesFetched, lifetimeAAgain.BytesFetched);
        Assert.Equal(lifetimeA.FirstHour, lifetimeAAgain.FirstHour);
    }

    [Fact]
    public async Task SweepAsync_PrunesSegmentFetchesOlderThanFetchTtl()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var db = harness.Context;
        var nowMs = 100L * OneHourMs;
        var fetchTtl = TimeSpan.FromHours(6);
        var cutoff = MetricsRetentionService.Cutoff(nowMs, fetchTtl);

        db.SegmentFetches.AddRange(
            new SegmentFetch { At = cutoff - OneHourMs, Provider = "old" },
            new SegmentFetch { At = cutoff + OneHourMs, Provider = "fresh" });
        await db.SaveChangesAsync();

        await MetricsRetentionService.SweepAsync(db, nowMs, fetchTtl);

        Assert.Equal(1, await db.SegmentFetches.CountAsync());
        Assert.Equal("fresh", await db.SegmentFetches.Select(x => x.Provider).SingleAsync());
    }

    [Fact]
    public async Task SweepAsync_PrunesFailoverMissesWithSameFetchTtl()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var db = harness.Context;
        var nowMs = 100L * OneHourMs;
        var fetchTtl = TimeSpan.FromHours(3);
        var cutoff = MetricsRetentionService.Cutoff(nowMs, fetchTtl);

        db.FailoverMisses.AddRange(
            new FailoverMiss { At = cutoff - OneHourMs, FromProvider = "old", ToProvider = "backup" },
            new FailoverMiss { At = cutoff + OneHourMs, FromProvider = "fresh", ToProvider = "backup" });
        await db.SaveChangesAsync();

        await MetricsRetentionService.SweepAsync(db, nowMs, fetchTtl);

        Assert.Equal(1, await db.FailoverMisses.CountAsync());
        Assert.Equal("fresh", await db.FailoverMisses.Select(x => x.FromProvider).SingleAsync());
    }

    [Fact]
    public async Task SweepAsync_PrunesArrImportEventsOlderThanNinetyDays()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var db = harness.Context;
        var nowMs = 200L * OneDayMs;
        var cutoff = MetricsRetentionService.Cutoff(nowMs, TimeSpan.FromDays(90));

        db.ArrImportEvents.AddRange(
            new ArrImportEvent
            {
                InstanceKey = "sonarr|http://sonarr:8989",
                ArrRecordId = 1,
                DownloadId = Guid.NewGuid(),
                ImportedAtMs = cutoff - OneDayMs,
                Title = "old",
            },
            new ArrImportEvent
            {
                InstanceKey = "sonarr|http://sonarr:8989",
                ArrRecordId = 2,
                DownloadId = Guid.NewGuid(),
                ImportedAtMs = cutoff + OneDayMs,
                Title = "fresh",
            });
        await db.SaveChangesAsync();

        await MetricsRetentionService.SweepAsync(db, nowMs, TimeSpan.FromHours(24));

        Assert.Equal(1, await db.ArrImportEvents.CountAsync());
        Assert.Equal("fresh", await db.ArrImportEvents.Select(x => x.Title).SingleAsync());
    }

    [Fact]
    public async Task SweepAsync_UsesOneHourFloorWhenConfiguredRetentionIsZero()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var db = harness.Context;
        var nowMs = 10L * OneHourMs;
        var floorTtl = TimeSpan.FromHours(MetricsRetentionService.MinFetchRetentionHours);
        var cutoff = MetricsRetentionService.Cutoff(nowMs, floorTtl);

        db.SegmentFetches.AddRange(
            new SegmentFetch { At = cutoff - OneHourMs, Provider = "old" },
            new SegmentFetch { At = cutoff + OneHourMs, Provider = "fresh" });
        await db.SaveChangesAsync();

        var configuredHours = 0;
        var effectiveHours = Math.Max(configuredHours, MetricsRetentionService.MinFetchRetentionHours);
        await MetricsRetentionService.SweepAsync(db, nowMs, TimeSpan.FromHours(effectiveHours));

        Assert.Equal(1, await db.SegmentFetches.CountAsync());
        Assert.Equal("fresh", await db.SegmentFetches.Select(x => x.Provider).SingleAsync());
    }

    private sealed class MetricsHarness : IAsyncDisposable
    {
        private readonly string _dir;

        private MetricsHarness(string dir, MetricsDbContext context)
        {
            _dir = dir;
            Context = context;
        }

        public MetricsDbContext Context { get; }

        public static async Task<MetricsHarness> CreateAsync()
        {
            var dir = Path.Join(Path.GetTempPath(), $"nzbdav-metrics-retention-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            var path = Path.Join(dir, "metrics.sqlite");
            var options = new DbContextOptionsBuilder<MetricsDbContext>()
                .UseSqlite($"Data Source={path}")
                .AddInterceptors(new SqliteMetricsPragmas())
                .ReplaceService<
                    IMigrationsSqlGenerator,
                    SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
                .Options;
            var context = new MetricsDbContext(options);
            await context.Database.MigrateAsync();
            return new MetricsHarness(dir, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }
}
