using Microsoft.EntityFrameworkCore;
using Npgsql;
using NzbWebDAV.Database;

namespace NzbWebDAV.Tests.Database;

public sealed class PostgresMigrationTests
{
    [SkippableFact]
    public async Task MigrateAsync_AppliesFreshPostgresSchema()
    {
        Skip.IfNot(
            DatabaseProviderConfig.IsPostgres,
            "PostgreSQL migration tests require DATABASE_PROVIDER=postgres.");

        var schema = $"nzbdav_test_{Guid.NewGuid():N}";
        var connectionString = DatabaseProviderConfig.PostgresConnectionString;

        await using var adminConnection = new NpgsqlConnection(connectionString);
        await adminConnection.OpenAsync();
        await ExecuteAsync(adminConnection, $"CREATE SCHEMA \"{schema}\"");

        try
        {
            var scopedConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
            {
                SearchPath = schema
            }.ConnectionString;
            var options = new DbContextOptionsBuilder<PostgresDavDatabaseContext>()
                .UseNpgsql(scopedConnectionString)
                .Options;

            await using var context = new PostgresDavDatabaseContext(options);
            await context.Database.MigrateAsync().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            Assert.True(await DatabaseStartupGuards.ConfigItemsTableExistsAsync(context));
            Assert.Equal(5, await context.Items.CountAsync());
        }
        finally
        {
            await ExecuteAsync(adminConnection, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string commandText)
    {
        await using var command = new NpgsqlCommand(commandText, connection);
        await command.ExecuteNonQueryAsync();
    }
}
