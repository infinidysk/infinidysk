using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Database;

public sealed class ArrDownloadProvenanceMigrationTests
{
    private const string PriorMigration = "20260824160000_Add-Health-Repair-Pending";
    private const string ProvenanceMigration = "20260830120000_Add-Arr-Download-Provenance";

    [Fact]
    public async Task Upgrade_AddsNullableColumnsWithoutBackfillOrIndexes()
    {
        await using var harness = await MigrationHarness.CreateAsync();
        var ctx = harness.Context;

        var queueId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0001");
        var historyId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0002");
        var davId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0003");
        await InsertLegacyRowsAsync(ctx, queueId, historyId, davId);

        Assert.Contains(ProvenanceMigration, await ctx.Database.GetPendingMigrationsAsync());
        await ctx.Database.MigrateAsync();
        Assert.Empty(await ctx.Database.GetPendingMigrationsAsync());

        Assert.True(await ColumnIsNullableAsync(ctx, "QueueItems"));
        Assert.True(await ColumnIsNullableAsync(ctx, "HistoryItems"));
        Assert.True(await ColumnIsNullableAsync(ctx, "DavItems"));
        Assert.False(await IndexExistsAsync(ctx, "ArrDownloadId"));

        Assert.Equal(queueId, (await ctx.QueueItems.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(historyId, (await ctx.HistoryItems.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(davId, (await ctx.Items.AsNoTracking().SingleAsync(x => x.Id == davId)).Id);
        Assert.Null((await ctx.QueueItems.AsNoTracking().SingleAsync()).ArrDownloadId);
        Assert.Null((await ctx.HistoryItems.AsNoTracking().SingleAsync()).ArrDownloadId);
        Assert.Null((await ctx.Items.AsNoTracking().SingleAsync(x => x.Id == davId)).ArrDownloadId);

        var mixed = Guid.Parse("AbCdEf01-2345-4aBc-8DeF-0123456789Ab");
        var written = await ctx.QueueItems.SingleAsync();
        written.ArrDownloadId = mixed;
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var reread = await ctx.QueueItems.AsNoTracking().SingleAsync();
        Assert.Equal(mixed, reread.ArrDownloadId);
        Assert.Equal(mixed.ToString("D").ToUpperInvariant(), await ReadTextAsync(ctx, queueId));
    }

    private static async Task InsertLegacyRowsAsync(
        DavDatabaseContext ctx, Guid queueId, Guid historyId, Guid davId)
    {
        await ctx.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO QueueItems (Id, CreatedAt, SortOrder, FileName, JobName, NzbFileSize, TotalSegmentBytes, Category, Priority, PostProcessing)
            VALUES ({0}, {1}, 0, 'legacy.nzb', 'legacy', 1, 1, 'tv', 0, 0);
            """,
            queueId.ToString("D").ToUpperInvariant(),
            DateTime.UtcNow);

        await ctx.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO HistoryItems (Id, CreatedAt, FileName, JobName, Category, DownloadStatus, TotalSegmentBytes, DownloadTimeSeconds, NzbBlobId)
            VALUES ({0}, {1}, 'legacy.nzb', 'legacy', 'tv', 1, 1, 1, {2});
            """,
            historyId.ToString("D").ToUpperInvariant(),
            DateTime.UtcNow,
            historyId.ToString("D").ToUpperInvariant());

        await ctx.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO DavItems (Id, IdPrefix, CreatedAt, ParentId, Name, FileSize, Type, SubType, Path, HistoryItemId, NzbBlobId, HealthRepairPending)
            VALUES ({0}, {1}, {2}, {3}, 'legacy.mkv', 1, 2, 201, '/content/legacy.mkv', {4}, {5}, 0);
            """,
            davId.ToString("D").ToUpperInvariant(),
            davId.ToString("N")[..DavItem.IdPrefixLength],
            DateTime.UtcNow,
            DavItem.ContentFolder.Id.ToString("D").ToUpperInvariant(),
            historyId.ToString("D").ToUpperInvariant(),
            historyId.ToString("D").ToUpperInvariant());
    }

    private static async Task<bool> ColumnIsNullableAsync(DavDatabaseContext ctx, string table)
    {
        var connection = (SqliteConnection)ctx.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"notnull\" FROM pragma_table_info('{table}') WHERE name = 'ArrDownloadId';";
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt32(value) == 0;
    }

    private static async Task<bool> IndexExistsAsync(DavDatabaseContext ctx, string columnName)
    {
        var connection = (SqliteConnection)ctx.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1 FROM sqlite_master
            WHERE type = 'index' AND sql LIKE '%' || $column || '%'
            LIMIT 1;
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$column";
        parameter.Value = columnName;
        command.Parameters.Add(parameter);
        return await command.ExecuteScalarAsync() is not null and not DBNull;
    }

    private static async Task<string> ReadTextAsync(DavDatabaseContext ctx, Guid queueId)
    {
        var connection = (SqliteConnection)ctx.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ArrDownloadId FROM QueueItems WHERE Id = $id;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$id";
        parameter.Value = queueId.ToString("D").ToUpperInvariant();
        command.Parameters.Add(parameter);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
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
                $"nzbdav-arr-download-id-{Guid.NewGuid():N}.sqlite");
            var options = new DbContextOptionsBuilder<DavDatabaseContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
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
