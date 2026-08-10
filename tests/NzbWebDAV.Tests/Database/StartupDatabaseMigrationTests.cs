using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;

namespace NzbWebDAV.Tests.Database;

public sealed class StartupDatabaseMigrationTests
{
    private const string PriorMainMigration = "20260713120000_Add-Path-Index-To-DavItems";
    private const string PriorMetricsMigration = "20260601104313_AddFailoverEdges";

    [Fact]
    public async Task RunAsync_AppliesPendingMainAndMetricsMigrations()
    {
        var mainPath = TempDatabasePath("main");
        var metricsPath = TempDatabasePath("metrics");
        try
        {
            await using var mainContext = CreateMainContext(mainPath);
            await mainContext.Database.MigrateAsync(PriorMainMigration);
            await using var metricsContext = CreateMetricsContext(metricsPath);
            await metricsContext.Database.MigrateAsync(PriorMetricsMigration);
            await metricsContext.Database.ExecuteSqlRawAsync(
                """
                INSERT OR REPLACE INTO "__EFMigrationsLock" ("Id", "Timestamp")
                VALUES (1, '2026-07-23 01:40:05+00:00')
                """);

            await StartupDatabaseMigrator
                .RunAsync(
                    mainContext,
                    metricsContext,
                    static (_, _) => Task.FromResult<IAsyncDisposable?>(null),
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Empty(await mainContext.Database.GetPendingMigrationsAsync());
            Assert.Empty(await metricsContext.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            DeleteDatabaseFiles(mainPath);
            DeleteDatabaseFiles(metricsPath);
        }
    }

    private static DavDatabaseContext CreateMainContext(string path)
    {
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={path};Pooling=False")
            .AddInterceptors(new SqliteMainDbPragmas())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        return new DavDatabaseContext(options);
    }

    private static MetricsDbContext CreateMetricsContext(string path)
    {
        var options = new DbContextOptionsBuilder<MetricsDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False")
            .AddInterceptors(new SqliteMetricsPragmas())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        return new MetricsDbContext(options);
    }

    private static string TempDatabasePath(string name) =>
        Path.Join(Path.GetTempPath(), $"nzbdav-startup-{name}-{Guid.NewGuid():N}.sqlite");

    private static void DeleteDatabaseFiles(string path)
    {
        TryDelete(path);
        TryDelete(path + "-wal");
        TryDelete(path + "-shm");
        TryDelete(path + ".maintenance.lock");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { /* best effort */ }
    }
}
