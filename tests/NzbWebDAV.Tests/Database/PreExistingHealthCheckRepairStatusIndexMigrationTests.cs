using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;

namespace NzbWebDAV.Tests.Database;

/// <summary>
/// Reproduces #1104, where a pre-existing health-check repair index prevented
/// the Nzb identity migration from completing during an upgrade.
/// </summary>
public sealed class PreExistingHealthCheckRepairStatusIndexMigrationTests
{
    private const string PriorMigration = "20260731171110_Add-SingleAdmin-UniqueIndex";
    private const string NzbIdentityMigration = "20260817185420_Add-NzbIdentity-To-HealthCheckResults";
    private const string RepairStatusIndex = "IX_HealthCheckResults_RepairStatus_CreatedAt";

    [Fact]
    public async Task PreExistingRepairStatusIndex_IsReplacedByCanonicalFilteredIndex()
    {
        await using var harness = await MigrationHarness.CreateAsync();
        var context = harness.Context;

        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE INDEX "IX_HealthCheckResults_RepairStatus_CreatedAt"
            ON "HealthCheckResults" ("RepairStatus", "CreatedAt")
            WHERE "RepairStatus" IN (1, 2);
            """);
        Assert.True(await IndexExistsAsync(context, RepairStatusIndex));
        Assert.Contains(
            NzbIdentityMigration,
            await context.Database.GetPendingMigrationsAsync());

        await context.Database.MigrateAsync();

        Assert.Contains(
            NzbIdentityMigration,
            await context.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.True(await ColumnExistsAsync(context, "JobName"));
        Assert.True(await ColumnExistsAsync(context, "NzbFileName"));
        Assert.Equal(
            """CREATE INDEX "IX_HealthCheckResults_RepairStatus_CreatedAt" ON "HealthCheckResults" ("RepairStatus", "CreatedAt") WHERE "RepairStatus" IN (1, 2)""",
            await IndexSqlAsync(context, RepairStatusIndex));
    }

    private static async Task<bool> IndexExistsAsync(DavDatabaseContext context, string indexName)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync();

        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = $name LIMIT 1;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = indexName;
        command.Parameters.Add(parameter);

        return await command.ExecuteScalarAsync() is not null and not DBNull;
    }

    private static async Task<string?> IndexSqlAsync(DavDatabaseContext context, string indexName)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync();

        command.CommandText =
            "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = $name LIMIT 1;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = indexName;
        command.Parameters.Add(parameter);

        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<bool> ColumnExistsAsync(DavDatabaseContext context, string columnName)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync();

        command.CommandText = "SELECT 1 FROM pragma_table_info('HealthCheckResults') WHERE name = $name LIMIT 1;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = columnName;
        command.Parameters.Add(parameter);

        return await command.ExecuteScalarAsync() is not null and not DBNull;
    }

    private sealed class MigrationHarness : IAsyncDisposable
    {
        private readonly string _databasePath;

        private MigrationHarness(string databasePath, DavDatabaseContext context)
        {
            _databasePath = databasePath;
            Context = context;
        }

        public DavDatabaseContext Context { get; }

        public static async Task<MigrationHarness> CreateAsync()
        {
            var databasePath = Path.Join(
                Path.GetTempPath(),
                $"nzbdav-preexisting-health-check-index-{Guid.NewGuid():N}.sqlite");
            var options = new DbContextOptionsBuilder<DavDatabaseContext>()
                .UseSqlite($"Data Source={databasePath}")
                .AddInterceptors(new SqliteForeignKeyEnabler())
                .ReplaceService<
                    IMigrationsSqlGenerator,
                    SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
                .Options;
            var context = new DavDatabaseContext(options);
            await context.Database.MigrateAsync(PriorMigration);
            return new MigrationHarness(databasePath, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            File.Delete(_databasePath);
        }
    }
}
