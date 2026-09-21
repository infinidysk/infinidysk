using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    public async Task RunAsync_AppliesPendingMigrationsWithoutScanningMetrics()
    {
        var mainPath = TempDatabasePath("main");
        var metricsPath = TempDatabasePath("metrics");
        try
        {
            var recorder = new MetricsIntegrityCommandRecorder();
            await using var mainContext = CreateMainContext(mainPath);
            await mainContext.Database.MigrateAsync(PriorMainMigration);
            await using var metricsContext = CreateMetricsContext(metricsPath, recorder);
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
            Assert.Equal(0, recorder.ScanCount);
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

    [Fact]
    public async Task RunAsync_CurrentSchemasDoNotScanMetricsOrStartStatusServer()
    {
        var mainPath = TempDatabasePath("main");
        var metricsPath = TempDatabasePath("metrics");
        try
        {
            var recorder = new MetricsIntegrityCommandRecorder();
            await using var mainContext = CreateMainContext(mainPath);
            await mainContext.Database.MigrateAsync();
            await using var metricsContext = CreateMetricsContext(metricsPath, recorder);
            await metricsContext.Database.MigrateAsync();

            var statusServerStarted = false;
            await StartupDatabaseMigrator.RunAsync(
                    mainContext,
                    metricsContext,
                    (_, _) =>
                    {
                        statusServerStarted = true;
                        return Task.FromResult<IAsyncDisposable?>(null);
                    },
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(0, recorder.ScanCount);
            Assert.False(statusServerStarted);
            Assert.Empty(await mainContext.Database.GetPendingMigrationsAsync());
            Assert.Empty(await metricsContext.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            DeleteDatabaseFiles(mainPath);
            DeleteDatabaseFiles(metricsPath);
        }
    }

    private static MetricsDbContext CreateMetricsContext(
        string path,
        params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<MetricsDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False")
            .AddInterceptors(new SqliteMetricsPragmas())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>();

        if (interceptors.Length > 0)
            builder.AddInterceptors(interceptors);

        return new MetricsDbContext(builder.Options);
    }

    private sealed class MetricsIntegrityCommandRecorder : DbCommandInterceptor
    {
        private int _scanCount;

        public int ScanCount => Volatile.Read(ref _scanCount);

        private void Record(DbCommand command)
        {
            if (command.CommandText.Contains("quick_check", StringComparison.OrdinalIgnoreCase)
                || command.CommandText.Contains("integrity_check", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _scanCount);
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }
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
