using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Services.Metrics;

namespace NzbWebDAV.Tests.Services.Metrics;

/// <summary>
/// Backup rescues (ProviderMinutes.FailoverSaves) come from FailoverMisses edges,
/// not from SegmentFetch.Retries. Same-provider self-retries keep Retries visible
/// on the scoreboard without inflating Overview Backup rescues.
/// </summary>
public sealed class MetricsRollupFailoverSavesTests
{
    private const long Minute = 1_700_000_060_000L;

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
    public async Task RollupMinute_MultipleEdgeRowsSameAt_CountAsOneSave()
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
                """,
                Minute + 1, Minute + 5);

            await MetricsRollupService.RollupMinuteAsync(context, Minute);

            var row = await context.ProviderMinutes.AsNoTracking().SingleAsync();
            Assert.Equal(2, row.Retries);
            Assert.Equal(1, row.FailoverSaves);
        });
    }

    private static async Task withMetricsDb(Func<MetricsDbContext, Task> body)
    {
        var databasePath = Path.Combine(
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
