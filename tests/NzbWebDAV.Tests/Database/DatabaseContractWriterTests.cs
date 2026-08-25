using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;

namespace NzbWebDAV.Tests.Database;

public sealed class DatabaseContractWriterTests : IAsyncLifetime
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"nzbdav-contract-{Guid.NewGuid():N}");
    private readonly string _mainPath;
    private readonly string _metricsPath;
    private readonly string _ledgerPath;
    private readonly string _contractPath;

    private DbContextOptions<DavDatabaseContext> _mainOptions = null!;
    private DbContextOptions<MetricsDbContext> _metricsOptions = null!;
    private DbContextOptions<UsenetMigrationDbContext> _ledgerOptions = null!;

    private Func<DavDatabaseContext> _previousMainFactory = null!;
    private Func<MetricsDbContext> _previousMetricsFactory = null!;
    private Func<UsenetMigrationDbContext> _previousLedgerFactory = null!;
    private Func<string> _previousLedgerFilePath = null!;
    private Func<string> _previousContractFilePath = null!;

    public DatabaseContractWriterTests()
    {
        _mainPath = Path.Join(_root, "db.sqlite");
        _metricsPath = Path.Join(_root, "metrics.sqlite");
        _ledgerPath = Path.Join(_root, "usenet-migration.db");
        _contractPath = Path.Join(_root, "db-contract.json");
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        _mainOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={_mainPath};Pooling=False")
            .AddInterceptors(new SqliteMainDbPragmas())
            .ReplaceService<IMigrationsSqlGenerator, SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        _metricsOptions = new DbContextOptionsBuilder<MetricsDbContext>()
            .UseSqlite($"Data Source={_metricsPath};Pooling=False")
            .AddInterceptors(new SqliteMetricsPragmas())
            .ReplaceService<IMigrationsSqlGenerator, SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        _ledgerOptions = new DbContextOptionsBuilder<UsenetMigrationDbContext>()
            .UseSqlite($"Data Source={_ledgerPath};Pooling=False")
            .AddInterceptors(new SqliteUsenetMigrationPragmas())
            .Options;

        _previousMainFactory = DatabaseContractWriter.MainContextFactory;
        _previousMetricsFactory = DatabaseContractWriter.MetricsContextFactory;
        _previousLedgerFactory = DatabaseContractWriter.UsenetMigrationContextFactory;
        _previousLedgerFilePath = DatabaseContractWriter.UsenetMigrationDatabaseFilePath;
        _previousContractFilePath = DatabaseContractWriter.ContractFilePath;

        DatabaseContractWriter.MainContextFactory = () => new DavDatabaseContext(_mainOptions);
        DatabaseContractWriter.MetricsContextFactory = () => new MetricsDbContext(_metricsOptions);
        DatabaseContractWriter.UsenetMigrationContextFactory = () => new UsenetMigrationDbContext(_ledgerOptions);
        DatabaseContractWriter.UsenetMigrationDatabaseFilePath = () => _ledgerPath;
        DatabaseContractWriter.ContractFilePath = () => _contractPath;

        await using (var main = new DavDatabaseContext(_mainOptions))
            await main.Database.MigrateAsync();
        await using (var metrics = new MetricsDbContext(_metricsOptions))
            await metrics.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        DatabaseContractWriter.MainContextFactory = _previousMainFactory;
        DatabaseContractWriter.MetricsContextFactory = _previousMetricsFactory;
        DatabaseContractWriter.UsenetMigrationContextFactory = _previousLedgerFactory;
        DatabaseContractWriter.UsenetMigrationDatabaseFilePath = _previousLedgerFilePath;
        DatabaseContractWriter.ContractFilePath = _previousContractFilePath;

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best effort — temp files.
        }

        await Task.CompletedTask;
    }

    [Fact]
    public async Task WriteAsync_WritesContractMatchingAppliedHistory()
    {
        var mainApplied = await GetAppliedMigrationsAsync(new DavDatabaseContext(_mainOptions));
        var metricsApplied = await GetAppliedMigrationsAsync(new MetricsDbContext(_metricsOptions));

        await DatabaseContractWriter.WriteAsync();

        using var doc = await ReadContractAsync();
        var root = doc.RootElement;

        Assert.Equal("infinidysk-db-v1", root.GetProperty("contract").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("appVersion").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("generatedAtUtc").GetString()));
        Assert.Equal("sqlite", root.GetProperty("provider").GetString());
        Assert.Equal(mainApplied.Last(), root.GetProperty("terminalMigration").GetString());
        Assert.Equal(mainApplied.Count, root.GetProperty("migrationCount").GetInt32());
        Assert.Equal(ExpectedHash(mainApplied), root.GetProperty("migrationHistoryHash").GetString());
        Assert.Equal(
            new[] { "TMP_LINKED_FILES", "TMP_LINKED_FILES_UNIQUE" },
            root.GetProperty("transientObjects").Deserialize<string[]>());

        var databases = root.GetProperty("databases");

        var main = databases.GetProperty("main");
        Assert.Equal("sqlite", main.GetProperty("provider").GetString());
        Assert.Equal(mainApplied.Last(), main.GetProperty("terminalMigration").GetString());
        Assert.Equal(mainApplied.Count, main.GetProperty("migrationCount").GetInt32());
        Assert.Equal(ExpectedHash(mainApplied), main.GetProperty("migrationHistoryHash").GetString());
        Assert.Equal(2, main.GetProperty("transientObjects").GetArrayLength());

        var metrics = databases.GetProperty("metrics");
        Assert.Equal("sqlite", metrics.GetProperty("provider").GetString());
        Assert.Equal(metricsApplied.Last(), metrics.GetProperty("terminalMigration").GetString());
        Assert.Equal(metricsApplied.Count, metrics.GetProperty("migrationCount").GetInt32());
        Assert.Equal(ExpectedHash(metricsApplied), metrics.GetProperty("migrationHistoryHash").GetString());
        Assert.Equal(0, metrics.GetProperty("transientObjects").GetArrayLength());

        var ledger = databases.GetProperty("usenetMigration");
        Assert.Equal("sqlite", ledger.GetProperty("provider").GetString());
        Assert.Equal(JsonValueKind.Null, ledger.GetProperty("terminalMigration").ValueKind);
        Assert.Equal(0, ledger.GetProperty("migrationCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, ledger.GetProperty("migrationHistoryHash").ValueKind);
        Assert.Equal(0, ledger.GetProperty("transientObjects").GetArrayLength());

        // The writer must not create the lazily-initialized ledger as a side effect.
        Assert.False(File.Exists(_ledgerPath));
    }

    [Fact]
    public async Task WriteAsync_ProducesDeterministicHistoryHash()
    {
        await DatabaseContractWriter.WriteAsync();
        string? first;
        using (var doc = await ReadContractAsync())
            first = doc.RootElement.GetProperty("migrationHistoryHash").GetString();

        await DatabaseContractWriter.WriteAsync();
        string? second;
        using (var doc = await ReadContractAsync())
            second = doc.RootElement.GetProperty("migrationHistoryHash").GetString();

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task WriteAsync_WritesWorldReadableFile_AndOverwritesReadOnlyLeftover()
    {
        if (OperatingSystem.IsWindows()) return;

        const UnixFileMode expected =
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead;

        await DatabaseContractWriter.WriteAsync();
        Assert.Equal(expected, File.GetUnixFileMode(_contractPath));

        // A restrictive leftover from a previous install (possibly owned by another
        // user) must not block the rewrite: replace-by-rename only needs directory
        // write permission, and the new file is 0644 again afterwards.
        File.SetUnixFileMode(_contractPath,
            UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        await DatabaseContractWriter.WriteAsync();

        Assert.Equal(expected, File.GetUnixFileMode(_contractPath));
        using var doc = await ReadContractAsync();
        Assert.Equal("infinidysk-db-v1", doc.RootElement.GetProperty("contract").GetString());
    }

    [Fact]
    public async Task WriteAsync_CleansUpStaleTempFiles()
    {
        var stale = Path.Join(_root, "db-contract.deadbeef.tmp");
        await File.WriteAllTextAsync(stale, "junk");

        await DatabaseContractWriter.WriteAsync();

        Assert.False(File.Exists(stale));
        Assert.Empty(Directory.GetFiles(_root, "db-contract.*.tmp"));
        Assert.True(File.Exists(_contractPath));
    }

    [Fact]
    public async Task WriteAsync_PopulatesUsenetMigrationSection_WhenLedgerExists()
    {
        List<string> ledgerApplied;
        await using (var context = new UsenetMigrationDbContext(_ledgerOptions))
        {
            await context.Database.MigrateAsync();
            ledgerApplied = await GetAppliedMigrationsAsync(context);
        }

        await DatabaseContractWriter.WriteAsync();

        using var doc = await ReadContractAsync();
        var ledger = doc.RootElement.GetProperty("databases").GetProperty("usenetMigration");
        Assert.Equal("sqlite", ledger.GetProperty("provider").GetString());
        Assert.Equal(ledgerApplied.Last(), ledger.GetProperty("terminalMigration").GetString());
        Assert.Equal(ledgerApplied.Count, ledger.GetProperty("migrationCount").GetInt32());
        Assert.Equal(ExpectedHash(ledgerApplied), ledger.GetProperty("migrationHistoryHash").GetString());
        Assert.Equal(0, ledger.GetProperty("transientObjects").GetArrayLength());
    }

    [Fact]
    public async Task WriteAsync_NeverThrows_WhenContractCannotBeWritten()
    {
        var unreachable = Path.Join(_root, "missing-dir", "db-contract.json");
        DatabaseContractWriter.ContractFilePath = () => unreachable;

        await DatabaseContractWriter.WriteAsync();

        Assert.False(File.Exists(unreachable));
    }

    private async Task<JsonDocument> ReadContractAsync() =>
        JsonDocument.Parse(await File.ReadAllTextAsync(_contractPath));

    private static async Task<List<string>> GetAppliedMigrationsAsync(DbContext context)
    {
        await using (context)
        {
            return (await context.Database.GetAppliedMigrationsAsync())
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
        }
    }

    private static string ExpectedHash(IEnumerable<string> appliedMigrationIds)
    {
        var payload = string.Join('\n', appliedMigrationIds.OrderBy(id => id, StringComparer.Ordinal));
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
