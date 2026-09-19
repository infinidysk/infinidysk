using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;

namespace NzbWebDAV.Tests.Database;

public sealed class UrgentRepairFailuresMigrationTests
{
    private const string PriorMigration =
        "20260902000809_Add-Setup-Wizard-State";

    [Fact]
    public async Task ExistingRowsReceiveNullAndDownDropsColumn()
    {
        await using var harness = await MigrationHarness.CreateAsync();
        var context = harness.Context;
        var id = Guid.NewGuid();

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "DavItems" ("Id", "IdPrefix", "CreatedAt", "Name", "Type", "SubType", "Path")
            VALUES ({id}, {id.ToString("N")[..5]}, {DateTime.UtcNow}, {"video.mkv"}, {2}, {203}, {$"/content/{id:N}/video.mkv"});
            """);

        await context.Database.MigrateAsync();
        Assert.True(await ColumnExistsAsync(context, "UrgentRepairFailures"));
        Assert.Null(await ScalarAsync(context,
            "SELECT \"UrgentRepairFailures\" FROM \"DavItems\" WHERE \"Id\" = $id;",
            id));

        await context.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"DavItems\" DROP COLUMN \"UrgentRepairFailures\";");
        Assert.False(await ColumnExistsAsync(context, "UrgentRepairFailures"));
    }

    private static async Task<bool> ColumnExistsAsync(DavDatabaseContext context, string columnName)
    {
        return await ScalarAsync(
            context,
            "SELECT 1 FROM pragma_table_info('DavItems') WHERE name = $name LIMIT 1;",
            columnName) is not null and not DBNull;
    }

    private static async Task<object?> ScalarAsync(
        DavDatabaseContext context,
        string sql,
        object value)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = sql.Contains("$name", StringComparison.Ordinal) ? "$name" : "$id";
        parameter.Value = value;
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync();
        return result is DBNull ? null : result;
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
                Path.GetTempPath(), $"infinidysk-urgent-repair-mig-{Guid.NewGuid():N}.sqlite");
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
