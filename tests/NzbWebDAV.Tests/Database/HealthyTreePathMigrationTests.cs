using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Database;

/// <summary>
/// Ensures the Fix-Empty-Categories + Path-Index migrations leave a healthy multi-level
/// tree's Paths unchanged and create the unique Path index. Exercises the incremental
/// BuildFullPath rewrite on ~5–10k already-correct rows.
/// </summary>
public sealed class HealthyTreePathMigrationTests
{
    private const string PriorMigration = "20260604120000_Add-UpdatedAtUnix-Index-To-WantedItems";
    private const int CategoryCount = 5;
    private const int ReleasesPerCategory = 20;
    private const int FilesPerRelease = 50;

    [Fact]
    public async Task HealthyTree_MigratesWithoutChangingPaths_AndCreatesUniquePathIndex()
    {
        await using var harness = await MigrationHarness.CreateAsync();
        var ctx = harness.Context;

        var expectedPaths = await SeedHealthyTreeAsync(ctx);
        Assert.True(expectedPaths.Count >= 5_000, $"Expected >= 5000 seeded rows, got {expectedPaths.Count}");

        await ctx.Database.MigrateAsync();
        ctx.ChangeTracker.Clear();

        var actual = await ctx.Items.AsNoTracking()
            .Select(x => new { x.Id, x.Path })
            .ToListAsync();

        foreach (var (id, path) in expectedPaths)
        {
            var item = Assert.Single(actual, x => x.Id == id);
            Assert.Equal(path, item.Path);
        }

        var pathIndexExists = await IndexExistsAsync(ctx, "IX_DavItems_Path");
        Assert.True(pathIndexExists);
    }

    private static async Task<Dictionary<Guid, string>> SeedHealthyTreeAsync(DavDatabaseContext ctx)
    {
        var expected = new Dictionary<Guid, string>();
        var batch = new List<DavItem>();

        for (var c = 0; c < CategoryCount; c++)
        {
            var category = NewDirectory(Guid.NewGuid(), DavItem.ContentFolder, $"cat-{c:D2}");
            batch.Add(category);
            expected[category.Id] = category.Path;

            for (var r = 0; r < ReleasesPerCategory; r++)
            {
                var release = NewDirectory(Guid.NewGuid(), category, $"release-{r:D2}");
                batch.Add(release);
                expected[release.Id] = release.Path;

                for (var f = 0; f < FilesPerRelease; f++)
                {
                    var file = DavItem.New(
                        Guid.NewGuid(),
                        release,
                        $"file-{f:D3}.mkv",
                        fileSize: 1_024,
                        type: DavItem.ItemType.UsenetFile,
                        subType: DavItem.ItemSubType.NzbFile,
                        releaseDate: null,
                        lastHealthCheck: null,
                        historyItemId: null,
                        fileBlobId: null);
                    batch.Add(file);
                    expected[file.Id] = file.Path;
                }
            }
        }

        // 5 categories * 20 releases * 50 files = 5000 files + 100 dirs + 5 cats = 5105
        await HistoricalDavItemSeeder.SeedAsync(ctx, batch);
        ctx.ChangeTracker.Clear();
        return expected;
    }

    private static DavItem NewDirectory(Guid id, DavItem parent, string name) =>
        DavItem.New(
            id,
            parent,
            name,
            fileSize: null,
            type: DavItem.ItemType.Directory,
            subType: DavItem.ItemSubType.Directory,
            releaseDate: null,
            lastHealthCheck: null,
            historyItemId: null,
            fileBlobId: null);

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
            var databasePath = Path.Join(Path.GetTempPath(), $"nzbdav-healthy-tree-{Guid.NewGuid():N}.sqlite");
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
