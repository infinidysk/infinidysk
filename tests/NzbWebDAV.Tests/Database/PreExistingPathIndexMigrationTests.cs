using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Database;

/// <summary>
/// Reproduces the 0.7.11 Docker crash loop where databases already have a
/// non-unique <c>IX_DavItems_Path</c> (mrghxst fork migration or the manual
/// #237 workaround) and the unique-index migration must replace it.
/// </summary>
public sealed class PreExistingPathIndexMigrationTests
{
    private const string PriorMigration = "20260712000000_Fix-Empty-Categories";

    [Fact]
    public async Task PreExistingNonUniquePathIndex_IsReplacedByUniqueIndex()
    {
        await using var harness = await MigrationHarness.CreateAsync();
        var ctx = harness.Context;

        await SeedSmallTreeAsync(ctx);

        // Simulate the fork / manual CREATE INDEX workaround for #237.
        await ctx.Database.ExecuteSqlRawAsync(
            """CREATE INDEX "IX_DavItems_Path" ON "DavItems" ("Path");""");
        Assert.True(await IndexExistsAsync(ctx, "IX_DavItems_Path"));
        Assert.False(await IndexIsUniqueAsync(ctx, "IX_DavItems_Path"));

        await ctx.Database.MigrateAsync();
        ctx.ChangeTracker.Clear();

        Assert.True(await IndexExistsAsync(ctx, "IX_DavItems_Path"));
        Assert.True(await IndexIsUniqueAsync(ctx, "IX_DavItems_Path"));
    }

    private static async Task SeedSmallTreeAsync(DavDatabaseContext ctx)
    {
        var category = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            "movies",
            fileSize: null,
            type: DavItem.ItemType.Directory,
            subType: DavItem.ItemSubType.Directory,
            releaseDate: null,
            lastHealthCheck: null,
            historyItemId: null,
            fileBlobId: null);
        var release = DavItem.New(
            Guid.NewGuid(),
            category,
            "Some.Movie.2024",
            fileSize: null,
            type: DavItem.ItemType.Directory,
            subType: DavItem.ItemSubType.Directory,
            releaseDate: null,
            lastHealthCheck: null,
            historyItemId: null,
            fileBlobId: null);
        var file = DavItem.New(
            Guid.NewGuid(),
            release,
            "movie.mkv",
            fileSize: 1_024,
            type: DavItem.ItemType.UsenetFile,
            subType: DavItem.ItemSubType.NzbFile,
            releaseDate: null,
            lastHealthCheck: null,
            historyItemId: null,
            fileBlobId: null);

        await HistoricalDavItemSeeder.SeedAsync(ctx, [category, release, file]);
        ctx.ChangeTracker.Clear();
    }

    private static async Task<bool> IndexExistsAsync(DavDatabaseContext ctx, string indexName)
    {
        await using var command = ctx.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync();

        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = $name LIMIT 1;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = indexName;
        command.Parameters.Add(parameter);

        var result = await command.ExecuteScalarAsync();
        return result is not null && result is not DBNull;
    }

    private static async Task<bool> IndexIsUniqueAsync(DavDatabaseContext ctx, string indexName)
    {
        await using var command = ctx.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync();

        command.CommandText =
            "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = $name LIMIT 1;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = indexName;
        command.Parameters.Add(parameter);

        var result = await command.ExecuteScalarAsync();
        return result is string sql
               && sql.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase);
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
            var databasePath = Path.Join(Path.GetTempPath(), $"nzbdav-preexisting-path-index-{Guid.NewGuid():N}.sqlite");
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
