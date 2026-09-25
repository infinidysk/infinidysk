using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Api.Controllers.GetOverviewStats;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Services.Metrics;

namespace NzbWebDAV.Tests.Services.Metrics;

public sealed class MetricsRollupPeakRateTests
{
    private const long OneMinute = 60_000;
    private const long OneHour = 60 * OneMinute;
    private const long Hour0 = 1_700_000_000_000L - (1_700_000_000_000L % OneHour);

    [Fact]
    public async Task ProviderRates_ApiCombinesPersistedAndPendingSamplesExactlyOnce()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var tracker = new ProviderBytesTracker(() => 1);
        var providers = new List<GetOverviewStatsResponse.ProviderRow>();
        var labels = new Dictionary<string, string?> { ["primary"] = "Primary" };
        tracker.RestoreProviderRates([new(Hour0, "primary", 100_000_000, 100_000_000, 1)]);
        await MetricsRollupService.ApplyPendingPeakRatesAsync(harness.Context, tracker, Hour0 + OneMinute);
        tracker.RestoreProviderRates([new(Hour0 + OneMinute, "primary", 50_000_000, 100_000_000, 2)]);

        foreach (var useRollups in new[] { false, true })
        {
            await ProviderSampledRates.LoadAndApplyAsync(harness.Context, tracker, providers, labels,
                GetOverviewStatsRequest.OverviewWindow.Last1Hour, Hour0, Hour0 + OneHour + 1, useRollups);
            var row = Assert.Single(providers);
            Assert.Equal(100d, row.PeakMbPerSec);
            Assert.Equal(200d / 3, row.ActiveAverageMbPerSec!.Value, 8);
        }

        await MetricsRollupService.ApplyPendingPeakRatesAsync(harness.Context, tracker, Hour0 + OneHour);
        await ProviderSampledRates.LoadAndApplyAsync(harness.Context, tracker, providers, labels,
            GetOverviewStatsRequest.OverviewWindow.Last1Hour, Hour0, Hour0 + OneHour + 1, false);
        Assert.Equal(200d / 3, Assert.Single(providers).ActiveAverageMbPerSec!.Value, 8);
        var json = System.Text.Json.JsonSerializer.Serialize(providers,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"peakMbPerSec\":100", json);
        Assert.Contains("\"sampledSpeedSeries\":", json);
    }

    [Fact]
    public async Task ProviderRates_PersistRebuildAndRetainWeightedTotals()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var db = harness.Context;
        var tracker = new ProviderBytesTracker(() => 1);
        tracker.RestoreProviderRates([
            new(Hour0, "primary", 100_000_000, 100_000_000, 1),
            new(Hour0 + OneMinute, "primary", 50_000_000, 100_000_000, 2),
        ]);
        await MetricsRollupService.ApplyPendingPeakRatesAsync(db, tracker, Hour0 + OneHour);
        await MetricsRollupService.ApplyPendingPeakRatesAsync(db, tracker, Hour0 + OneHour);
        await MetricsRollupService.RollupHourAsync(db, Hour0);
        await MetricsRollupService.RollupHourAsync(db, Hour0);
        var hourly = await db.ProviderHourly.AsNoTracking().SingleAsync();
        Assert.Equal(100_000_000, hourly.PeakBytesPerSec);
        Assert.Equal(200_000_000, hourly.ActiveBytes);
        Assert.Equal(3d, hourly.ActiveSeconds);

        await MetricsRetentionService.SweepAsync(db, Hour0 + 400L * 24 * OneHour, TimeSpan.FromHours(24));
        var lifetime = await db.ProviderLifetimeTotals.AsNoTracking().SingleAsync();
        Assert.Equal(hourly.PeakBytesPerSec, lifetime.PeakBytesPerSec);
        Assert.Equal(hourly.ActiveBytes, lifetime.ActiveBytes);
        Assert.Equal(hourly.ActiveSeconds, lifetime.ActiveSeconds);
    }

    [Fact]
    public async Task ProviderRates_RollbackRestoresPendingSamples()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var db = harness.Context;
        var tracker = new ProviderBytesTracker(() => 1);
        tracker.RestoreProviderRates([new(Hour0, "primary", 100, 200, 2)]);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE ProviderHourly");
        await Assert.ThrowsAsync<SqliteException>(() =>
            MetricsRollupService.ApplyPendingPeakRatesAsync(db, tracker, Hour0 + OneHour));
        Assert.Equal(200, Assert.Single(tracker.PendingProviderRates(0)).ActiveBytes);
        Assert.Empty(await db.ProviderMinutes.ToListAsync());
    }

    [Fact]
    public async Task ApplyPeakRatesAsync_MaxMergesIntoMinuteAndHourRows()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var db = harness.Context;
        var minute0 = Hour0;
        var minute1 = Hour0 + OneMinute;
        var minute61 = Hour0 + OneHour + OneMinute;

        db.ThroughputMinutes.Add(new ThroughputMinute { Minute = minute0, BytesFetched = 123, Articles = 4 });
        await db.SaveChangesAsync();

        await MetricsRollupService.ApplyPeakRatesAsync(db, [(minute0, 500L), (minute1, 900L), (minute61, 700L)]);
        // Re-run with lower and higher values: lower must not win, higher must.
        await MetricsRollupService.ApplyPeakRatesAsync(db, [(minute0, 100L), (minute1, 950L)]);

        db.ChangeTracker.Clear();
        var minuteRows = await db.ThroughputMinutes.AsNoTracking().OrderBy(t => t.Minute).ToListAsync();
        Assert.Equal(3, minuteRows.Count);
        Assert.Equal(500, minuteRows[0].PeakFetchBytesPerSec);
        Assert.Equal(123, minuteRows[0].BytesFetched);
        Assert.Equal(4, minuteRows[0].Articles);
        Assert.Equal(950, minuteRows[1].PeakFetchBytesPerSec);
        Assert.Equal(700, minuteRows[2].PeakFetchBytesPerSec);

        var hourRows = await db.ThroughputHourly.AsNoTracking().OrderBy(h => h.Hour).ToListAsync();
        Assert.Equal(2, hourRows.Count);
        Assert.Equal(Hour0, hourRows[0].Hour);
        Assert.Equal(950, hourRows[0].PeakFetchBytesPerSec);
        Assert.Equal(Hour0 + OneHour, hourRows[1].Hour);
        Assert.Equal(700, hourRows[1].PeakFetchBytesPerSec);
    }

    [Fact]
    public async Task RollupMinuteAsync_DoesNotOverwritePersistedPeak()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var db = harness.Context;

        await MetricsRollupService.ApplyPeakRatesAsync(db, [(Hour0, 800L)]);
        await MetricsRollupService.RollupMinuteAsync(db, Hour0);

        var row = await db.ThroughputMinutes.AsNoTracking().SingleAsync(t => t.Minute == Hour0);
        Assert.Equal(800, row.PeakFetchBytesPerSec);
    }

    [Fact]
    public async Task ApplyPendingPeakRatesAsync_RestoresDrainedPeaksWhenPersistenceFails()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var db = harness.Context;
        var tracker = new ProviderBytesTracker(() => 1);
        tracker.RestorePeaks([(Hour0, 800L)]);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE ThroughputHourly");

        await Assert.ThrowsAsync<SqliteException>(
            () => MetricsRollupService.ApplyPendingPeakRatesAsync(db, tracker, Hour0 + OneHour));

        Assert.Equal(800, tracker.PendingPeakSince(Hour0));
        Assert.Empty(await db.ThroughputMinutes.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ApplyPendingPeakRatesAsync_WaitsUntilFullStatsResetCompletes()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var db = harness.Context;
        var tracker = new ProviderBytesTracker(() => 1);
        tracker.RestorePeaks([(Hour0, 800L)]);
        await tracker.PeakPersistenceGate.WaitAsync();

        try
        {
            var apply = MetricsRollupService.ApplyPendingPeakRatesAsync(db, tracker, Hour0 + OneHour);
            tracker.ResetCounters();
            Assert.False(apply.IsCompleted);
            tracker.PeakPersistenceGate.Release();
            await apply;
        }
        finally
        {
            if (tracker.PeakPersistenceGate.CurrentCount == 0)
                tracker.PeakPersistenceGate.Release();
        }

        Assert.Empty(await db.ThroughputMinutes.ToListAsync());
        Assert.Empty(await db.ThroughputHourly.ToListAsync());
    }

    [Fact]
    public async Task SweepAsync_KeepsThroughputHourlyBeyondHourlyTtl()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var db = harness.Context;
        var nowMs = 400L * 24 * OneHour;

        db.ThroughputHourly.Add(new ThroughputHourly { Hour = 0, PeakFetchBytesPerSec = 42 });
        await db.SaveChangesAsync();

        await MetricsRetentionService.SweepAsync(db, nowMs, TimeSpan.FromHours(24));

        Assert.Equal(1, await db.ThroughputHourly.CountAsync());
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
            var dir = Path.Join(Path.GetTempPath(), $"nzbdav-metrics-peak-{Guid.NewGuid():N}");
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
            catch (DirectoryNotFoundException)
            {
                // Best effort cleanup.
            }
            catch (IOException)
            {
                // Best effort cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort cleanup.
            }
        }
    }
}
