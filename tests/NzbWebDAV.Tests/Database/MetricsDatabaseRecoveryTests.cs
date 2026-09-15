using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;

namespace NzbWebDAV.Tests.Database;

public sealed class MetricsDatabaseRecoveryTests
{
    private static string TempDatabasePath() => Path.Join(
        Path.GetTempPath(),
        $"nzbdav-metrics-recovery-{Guid.NewGuid():N}",
        "metrics.sqlite");

    private static DbContextOptions<MetricsDbContext> OptionsFor(string databasePath) =>
        new DbContextOptionsBuilder<MetricsDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .AddInterceptors(new SqliteMetricsPragmas())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;

    private static string[] QuarantinedFiles(string databasePath) =>
        Directory.GetFiles(Path.GetDirectoryName(databasePath)!, "metrics.sqlite.corrupt-*")
            .Where(f => !f.EndsWith("-wal") && !f.EndsWith("-shm") && !f.EndsWith("-journal"))
            .ToArray();

    [Fact]
    public async Task MissingFile_IsNotQuarantined()
    {
        var databasePath = TempDatabasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        try
        {
            await using var context = new MetricsDbContext(OptionsFor(databasePath));
            Assert.False(await MetricsDatabaseRecovery.QuarantineIfCorruptAsync(context));
            Assert.Empty(QuarantinedFiles(databasePath));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
        }
    }

    [Fact]
    public async Task HealthyDatabase_IsLeftAlone()
    {
        var databasePath = TempDatabasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        try
        {
            await using var context = new MetricsDbContext(OptionsFor(databasePath));
            await context.Database.MigrateAsync();

            Assert.False(await MetricsDatabaseRecovery.QuarantineIfCorruptAsync(context));
            Assert.True(File.Exists(databasePath));
            Assert.Empty(QuarantinedFiles(databasePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
        }
    }

    [Fact]
    public async Task NotADatabase_IsQuarantinedAndRecreated()
    {
        var databasePath = TempDatabasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        try
        {
            // Garbage where SQLite expects a header: SQLITE_NOTADB (26) on first use.
            await File.WriteAllBytesAsync(databasePath, Enumerable.Repeat((byte)0x5A, 8192).ToArray());

            await using var context = new MetricsDbContext(OptionsFor(databasePath));
            Assert.True(await MetricsDatabaseRecovery.QuarantineIfCorruptAsync(context));

            Assert.False(File.Exists(databasePath));
            Assert.Single(QuarantinedFiles(databasePath));

            // The same context creates a fresh file with the full schema.
            await context.Database.MigrateAsync();
            Assert.True(File.Exists(databasePath));
            Assert.Equal(0, await context.ThroughputMinutes.CountAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
        }
    }

    [Fact]
    public async Task MalformedPage_IsQuarantined()
    {
        var databasePath = TempDatabasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        try
        {
            await using (var seed = new MetricsDbContext(OptionsFor(databasePath)))
            {
                await seed.Database.MigrateAsync();
                // Enough rows to span several pages so a wrecked interior page is
                // reachable from the b-tree quick_check walks.
                for (var i = 0; i < 3000; i++)
                {
                    seed.ThroughputMinutes.Add(new NzbWebDAV.Database.Models.Metrics.ThroughputMinute
                    {
                        Minute = 60_000L * i,
                        BytesServed = i,
                        BytesFetched = i,
                        Articles = 1,
                        Errors = 0,
                        ActiveReadsMax = 1,
                    });
                }
                await seed.SaveChangesAsync();
                // Fold the WAL into the main file so the damage below lands in it.
                await seed.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);");
            }
            SqliteConnection.ClearAllPools();

            // Overwrite the third 4 KiB page (header page untouched).
            await using (var stream = new FileStream(databasePath, FileMode.Open, FileAccess.ReadWrite))
            {
                stream.Position = 4096 * 2;
                await stream.WriteAsync(Enumerable.Repeat((byte)0xFF, 4096).ToArray());
            }

            await using var context = new MetricsDbContext(OptionsFor(databasePath));
            Assert.True(await MetricsDatabaseRecovery.QuarantineIfCorruptAsync(context));
            Assert.False(File.Exists(databasePath));
            Assert.Single(QuarantinedFiles(databasePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
        }
    }

    [Fact]
    public void IsUnusableDatabase_CoversCorruptAndNotADatabase()
    {
        Assert.True(MetricsDatabaseRecovery.IsUnusableDatabase(new SqliteException("malformed", 11)));
        Assert.True(MetricsDatabaseRecovery.IsUnusableDatabase(new SqliteException("not a database", 26)));
        Assert.True(MetricsDatabaseRecovery.IsUnusableDatabase(
            new InvalidOperationException("wrapped", new SqliteException("malformed", 11))));
        Assert.False(MetricsDatabaseRecovery.IsUnusableDatabase(new SqliteException("busy", 5)));
        Assert.False(MetricsDatabaseRecovery.IsUnusableDatabase(new IOException("disk")));
    }
}
