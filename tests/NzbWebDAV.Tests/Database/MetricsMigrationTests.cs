using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;

namespace NzbWebDAV.Tests.Database;

public sealed class MetricsMigrationTests
{
    private const string PriorMigration = "20260601104313_AddFailoverEdges";

    [Fact]
    public async Task AddMissesCounters_MovesExistingErrorsWithoutScanningRawFetches()
    {
        var databasePath = Path.Join(
            Path.GetTempPath(),
            $"nzbdav-metrics-migration-{Guid.NewGuid():N}.sqlite");
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
            await context.Database.MigrateAsync(PriorMigration);

            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO ThroughputMinutes
                    (Minute, BytesServed, BytesFetched, Articles, Errors, ActiveReadsMax)
                VALUES (60000, 10, 20, 3, 7, 1);

                INSERT INTO ProviderMinutes
                    (Minute, Provider, Articles, BytesFetched, Errors, Retries, FailoverSaves, SumDurationMs, Hist)
                VALUES (60000, 'provider-a', 3, 20, 5, 1, 1, 30, NULL);

                INSERT INTO ProviderHourly
                    (Hour, Provider, Articles, BytesFetched, Errors, Retries, FailoverSaves, SumDurationMs, P95DurationMs)
                VALUES (0, 'provider-a', 3, 20, 9, 1, 1, 30, 10);

                INSERT INTO SegmentFetches
                    (At, Provider, ReadSessionId, QueueItemId, Bytes, DurationMs, Status, Retries)
                VALUES
                    (60001, 'provider-a', NULL, NULL, 10, 1, 1, 0),
                    (60002, 'provider-a', NULL, NULL, 10, 1, 2, 0);
                """);

            await context.Database.MigrateAsync();
            context.ChangeTracker.Clear();

            var throughput = await context.ThroughputMinutes.AsNoTracking().SingleAsync();
            Assert.Equal(7, throughput.Misses);
            Assert.Equal(0, throughput.Errors);
            Assert.Equal(0, throughput.PeakFetchBytesPerSec);

            var providerMinute = await context.ProviderMinutes.AsNoTracking().SingleAsync();
            Assert.Equal(5, providerMinute.Misses);
            Assert.Equal(0, providerMinute.Errors);
            Assert.Null(providerMinute.PeakBytesPerSec);
            Assert.Null(providerMinute.ActiveBytes);
            Assert.Null(providerMinute.ActiveSeconds);

            var providerHour = await context.ProviderHourly.AsNoTracking().SingleAsync();
            Assert.Equal(9, providerHour.Misses);
            Assert.Equal(0, providerHour.Errors);
            Assert.Null(providerHour.PeakBytesPerSec);
            Assert.Null(providerHour.ActiveBytes);
            Assert.Null(providerHour.ActiveSeconds);

            Assert.Empty(await context.ProviderQuotaUsage.AsNoTracking().ToListAsync());
            Assert.Empty(await context.ThroughputHourly.AsNoTracking().ToListAsync());

            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddReadSessionsEndedAtIndex_ToleratesManuallyCreatedIndex(bool createManually)
    {
        var databasePath = Path.Join(
            Path.GetTempPath(),
            $"nzbdav-metrics-migration-{Guid.NewGuid():N}.sqlite");
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
            await context.Database.MigrateAsync("20260923160000_AddProviderSampledRates");
            if (createManually)
            {
                await context.Database.ExecuteSqlRawAsync(
                    "CREATE INDEX IX_ReadSessions_EndedAt ON ReadSessions(EndedAt);");
            }

            await context.Database.MigrateAsync();

            var indexes = await context.Database
                .SqlQueryRaw<string>(
                    "SELECT name AS Value FROM sqlite_master WHERE type = 'index' AND tbl_name = 'ReadSessions'")
                .ToListAsync();
            Assert.Single(indexes, name => name == "IX_ReadSessions_EndedAt");
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            File.Delete(databasePath);
        }
    }
}
