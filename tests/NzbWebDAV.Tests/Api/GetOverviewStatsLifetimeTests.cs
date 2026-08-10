using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Api.Controllers.GetOverviewStats;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models.Metrics;

namespace NzbWebDAV.Tests.Api;

public sealed class GetOverviewStatsLifetimeTests
{
    [Fact]
    public async Task BuildLifetimeAsync_IncludesFoldedTotalsAndEarliestFirstSeenAt()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var db = harness.Context;

        db.ProviderHourly.Add(new ProviderHourly
        {
            Hour = 2_000,
            Provider = "provider-a",
            Articles = 4,
            BytesFetched = 400,
        });
        db.ProviderLifetimeTotals.Add(new ProviderLifetimeTotal
        {
            Provider = "provider-a",
            Articles = 10,
            BytesFetched = 1_000,
            FirstHour = 1_000,
        });
        db.ProviderLifetimeTotals.Add(new ProviderLifetimeTotal
        {
            Provider = "provider-b",
            Articles = 2,
            BytesFetched = 200,
            FirstHour = 500,
        });
        await db.SaveChangesAsync();

        var lifetime = await GetOverviewStatsController.BuildLifetimeAsync(db);

        Assert.Equal(16, lifetime.Articles);
        Assert.Equal(1_600, lifetime.BytesFetched);
        Assert.Equal(500, lifetime.FirstSeenAt);
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
            var dir = Path.Join(Path.GetTempPath(), $"nzbdav-overview-lifetime-{Guid.NewGuid():N}");
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
