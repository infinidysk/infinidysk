using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Database;

public class SqliteMainDbPragmasTests
{
    [Theory]
    [InlineData("Data Source=db.sqlite;Mode=ReadOnly", true)]
    [InlineData("Data Source=db.sqlite;mode=readonly", true)]
    [InlineData("Data Source=db.sqlite;mode=read-only", true)]
    [InlineData("Data Source=db.sqlite", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsExplicitlyReadOnly_DetectsReadOnlyModes(string? connectionString, bool expected)
    {
        Assert.Equal(expected, SqliteMainDbPragmas.IsExplicitlyReadOnly(connectionString));
    }

    [Fact]
    public async Task ConnectionOpened_AppliesMemoryAndJournalPragmas()
    {
        var databasePath = Path.Join(Path.GetTempPath(), $"nzbdav-main-pragmas-{Guid.NewGuid():N}.sqlite");
        try
        {
            var options = new DbContextOptionsBuilder<DavDatabaseContext>()
                .UseSqlite($"Data Source={databasePath}")
                .AddInterceptors(new SqliteMainDbPragmas())
                .Options;

            await using var ctx = new DavDatabaseContext(options);
            await ctx.Database.OpenConnectionAsync();

            Assert.Equal(268435456L, await ReadPragmaInt64Async(ctx, "mmap_size"));
            Assert.Equal(-65536L, await ReadPragmaInt64Async(ctx, "cache_size"));
            Assert.Equal(67108864L, await ReadPragmaInt64Async(ctx, "journal_size_limit"));
            Assert.Equal(5000L, await ReadPragmaInt64Async(ctx, "busy_timeout"));
        }
        finally
        {
            try { File.Delete(databasePath); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public async Task MaintenanceSweep_PopulatesSqliteStat1()
    {
        var configPath = Path.Join(Path.GetTempPath(), $"nzbdav-maint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configPath);
        var mainPath = Path.Join(configPath, "db.sqlite");
        var metricsPath = Path.Join(configPath, "metrics.sqlite");

        try
        {
            var mainOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
                .UseSqlite($"Data Source={mainPath}")
                .AddInterceptors(new SqliteMainDbPragmas())
                .ReplaceService<
                    IMigrationsSqlGenerator,
                    SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
                .Options;
            await using var main = new DavDatabaseContext(mainOptions);
            await main.Database.MigrateAsync();

            var metricsOptions = new DbContextOptionsBuilder<MetricsDbContext>()
                .UseSqlite($"Data Source={metricsPath}")
                .AddInterceptors(new SqliteMetricsPragmas())
                .ReplaceService<
                    IMigrationsSqlGenerator,
                    SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
                .Options;
            await using var metrics = new MetricsDbContext(metricsOptions);
            await metrics.Database.MigrateAsync();

            // Ensure there is something for ANALYZE to sample.
            await main.Database.ExecuteSqlRawAsync(
                "INSERT OR IGNORE INTO ConfigItems (ConfigName, ConfigValue) VALUES ('test.pragma', '1');");

            await SqliteMaintenanceService.SweepAsync(main, metrics);

            var statCount = await ReadScalarInt64Async(main, "SELECT count(*) FROM sqlite_stat1;");
            Assert.True(statCount > 0, "Expected PRAGMA optimize to populate sqlite_stat1");
        }
        finally
        {
            try { Directory.Delete(configPath, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    private static async Task<long> ReadPragmaInt64Async(DavDatabaseContext ctx, string pragma)
    {
        await using var command = ctx.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA {pragma};";
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }

    private static async Task<long> ReadScalarInt64Async(DavDatabaseContext ctx, string sql)
    {
        await using var command = ctx.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }
}

public class SqliteMetricsPragmasTests
{
    [Fact]
    public async Task ConnectionOpened_AppliesBusyTimeout()
    {
        var databasePath = Path.Join(Path.GetTempPath(), $"nzbdav-metrics-pragmas-{Guid.NewGuid():N}.sqlite");
        try
        {
            var options = new DbContextOptionsBuilder<MetricsDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .AddInterceptors(new SqliteMetricsPragmas())
                .Options;

            await using var ctx = new MetricsDbContext(options);
            await ctx.Database.OpenConnectionAsync();

            await using var command = ctx.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA busy_timeout;";
            var result = await command.ExecuteScalarAsync();
            Assert.Equal(5000L, Convert.ToInt64(result));
        }
        finally
        {
            try { File.Delete(databasePath); } catch (IOException) { /* best effort */ }
        }
    }
}
