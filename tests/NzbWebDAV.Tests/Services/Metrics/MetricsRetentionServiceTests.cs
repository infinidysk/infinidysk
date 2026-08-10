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

        await MetricsRetentionService.SweepAsync(db, nowMs);

        var lifetimeA = await db.ProviderLifetimeTotals.SingleAsync(x => x.Provider == "provider-a");
        Assert.Equal(15, lifetimeA.Articles);
        Assert.Equal(150, lifetimeA.BytesFetched);
        Assert.Equal(cutoff - 2 * OneDayMs, lifetimeA.FirstHour);
        Assert.Equal(1, await db.ProviderHourly.CountAsync());

        await MetricsRetentionService.SweepAsync(db, nowMs);
        var lifetimeAAgain = await db.ProviderLifetimeTotals.SingleAsync(x => x.Provider == "provider-a");
        Assert.Equal(lifetimeA.Articles, lifetimeAAgain.Articles);
        Assert.Equal(lifetimeA.BytesFetched, lifetimeAAgain.BytesFetched);
        Assert.Equal(lifetimeA.FirstHour, lifetimeAAgain.FirstHour);
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
