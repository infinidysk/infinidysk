using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Database;

public sealed class FixEmptyCategoriesMigrationTests
{
    private const string PriorMigration = "20260604120000_Add-UpdatedAtUnix-Index-To-WantedItems";

    [Fact]
    public async Task FixEmptyCategories_RepairsLegacyRowsAndRebuildsPaths()
    {
        await using var harness = await MigrationHarness.CreateAsync();
        var ctx = harness.Context;

        var emptyFolderId = Guid.NewGuid();
        var mountId = Guid.NewGuid();
        var collisionMountId = Guid.NewGuid();
        var existingUncategorizedMountId = Guid.NewGuid();
        var uncategorizedFolderId = Guid.NewGuid();
        var emptyQueueId = Guid.NewGuid();
        var collidingQueueId = Guid.NewGuid();
        var existingQueueId = Guid.NewGuid();
        var emptyHistoryId = Guid.NewGuid();
        var whitespaceHistoryId = Guid.NewGuid();

        ctx.ConfigItems.Add(new ConfigItem
        {
            ConfigName = "api.manual-category",
            ConfigValue = "uncategorized",
        });

        var uncategorizedFolder = NewDirectory(uncategorizedFolderId, DavItem.ContentFolder, "uncategorized");
        var emptyFolder = NewDirectory(emptyFolderId, DavItem.ContentFolder, "");
        // Bypass DavItem.New path join for the empty-named folder; Path.Join drops empty segments.
        emptyFolder.Path = "/content/";
        var mount = NewDirectory(mountId, emptyFolder, "Release One");
        mount.Path = "/content//Release One";
        var collisionMount = NewDirectory(collisionMountId, emptyFolder, "Shared Release");
        collisionMount.Path = "/content//Shared Release";
        var existingMount = NewDirectory(existingUncategorizedMountId, uncategorizedFolder, "Shared Release");

        ctx.Items.AddRange(uncategorizedFolder, emptyFolder, mount, collisionMount, existingMount);

        ctx.QueueItems.AddRange(
            new QueueItem
            {
                Id = emptyQueueId,
                CreatedAt = DateTime.UtcNow,
                FileName = "empty.nzb",
                JobName = "empty",
                NzbFileSize = 10,
                TotalSegmentBytes = 20,
                Category = "",
                Priority = QueueItem.PriorityOption.Normal,
                PostProcessing = QueueItem.PostProcessingOption.None,
            },
            new QueueItem
            {
                Id = collidingQueueId,
                CreatedAt = DateTime.UtcNow,
                FileName = "shared.nzb",
                JobName = "shared-empty",
                NzbFileSize = 10,
                TotalSegmentBytes = 20,
                Category = "",
                Priority = QueueItem.PriorityOption.Normal,
                PostProcessing = QueueItem.PostProcessingOption.None,
            },
            new QueueItem
            {
                Id = existingQueueId,
                CreatedAt = DateTime.UtcNow,
                FileName = "shared.nzb",
                JobName = "shared-existing",
                NzbFileSize = 10,
                TotalSegmentBytes = 20,
                Category = "uncategorized",
                Priority = QueueItem.PriorityOption.Normal,
                PostProcessing = QueueItem.PostProcessingOption.None,
            });

        ctx.HistoryItems.AddRange(
            new HistoryItem
            {
                Id = emptyHistoryId,
                CreatedAt = DateTime.UtcNow,
                FileName = "hist-empty.nzb",
                JobName = "hist-empty",
                Category = "",
                DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
                TotalSegmentBytes = 20,
                DownloadTimeSeconds = 1,
            },
            new HistoryItem
            {
                Id = whitespaceHistoryId,
                CreatedAt = DateTime.UtcNow,
                FileName = "hist-ws.nzb",
                JobName = "hist-ws",
                Category = "   ",
                DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
                TotalSegmentBytes = 20,
                DownloadTimeSeconds = 1,
            });

        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        await ctx.Database.MigrateAsync();
        ctx.ChangeTracker.Clear();

        Assert.False(await ctx.Items.AsNoTracking().AnyAsync(x => x.Id == emptyFolderId));

        var repairedMount = await ctx.Items.AsNoTracking()
            .SingleAsync(x => x.Id == mountId);
        Assert.Equal(uncategorizedFolderId, repairedMount.ParentId);
        Assert.Equal("Release One", repairedMount.Name);
        Assert.Equal("/content/uncategorized/Release One", repairedMount.Path);

        var renamedMount = await ctx.Items.AsNoTracking()
            .SingleAsync(x => x.Id == collisionMountId);
        Assert.Equal(uncategorizedFolderId, renamedMount.ParentId);
        Assert.Equal($"Shared Release ({collisionMountId.ToString().ToUpperInvariant()[..5]})", renamedMount.Name);
        Assert.Equal($"/content/uncategorized/{renamedMount.Name}", renamedMount.Path);

        var existing = await ctx.Items.AsNoTracking()
            .SingleAsync(x => x.Id == existingUncategorizedMountId);
        Assert.Equal("Shared Release", existing.Name);
        Assert.Equal("/content/uncategorized/Shared Release", existing.Path);

        var emptyQueue = await ctx.QueueItems.AsNoTracking()
            .SingleAsync(x => x.Id == emptyQueueId);
        Assert.Equal("uncategorized", emptyQueue.Category);
        Assert.Equal("empty.nzb", emptyQueue.FileName);

        var collidingQueue = await ctx.QueueItems.AsNoTracking()
            .SingleAsync(x => x.Id == collidingQueueId);
        Assert.Equal("uncategorized", collidingQueue.Category);
        Assert.Equal($"shared.nzb ({collidingQueueId.ToString().ToUpperInvariant()[..5]})", collidingQueue.FileName);

        var existingQueue = await ctx.QueueItems.AsNoTracking()
            .SingleAsync(x => x.Id == existingQueueId);
        Assert.Equal("uncategorized", existingQueue.Category);
        Assert.Equal("shared.nzb", existingQueue.FileName);

        var histories = await ctx.HistoryItems.AsNoTracking()
            .Where(x => x.Id == emptyHistoryId || x.Id == whitespaceHistoryId)
            .ToListAsync();
        Assert.All(histories, h => Assert.Equal("uncategorized", h.Category));

        // Deleting the empty directory enqueues its Id for cleanup; with zero children
        // that cleanup is a no-op. No other DavCleanupItems should appear.
        var cleanupIds = await ctx.DavCleanupItems.AsNoTracking()
            .Select(x => x.Id)
            .ToListAsync();
        Assert.Equal([emptyFolderId], cleanupIds);
    }

    [Fact]
    public async Task FixEmptyCategories_CreatesTargetCategoryFolderWhenMissing()
    {
        await using var harness = await MigrationHarness.CreateAsync();
        var ctx = harness.Context;

        var emptyFolderId = Guid.NewGuid();
        var mountId = Guid.NewGuid();

        var emptyFolder = NewDirectory(emptyFolderId, DavItem.ContentFolder, "");
        emptyFolder.Path = "/content/";
        var mount = NewDirectory(mountId, emptyFolder, "Orphan Release");
        mount.Path = "/content//Orphan Release";

        ctx.Items.AddRange(emptyFolder, mount);
        ctx.HistoryItems.Add(new HistoryItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            FileName = "orphan.nzb",
            JobName = "orphan",
            Category = "",
            DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
            TotalSegmentBytes = 20,
            DownloadTimeSeconds = 1,
        });
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        await ctx.Database.MigrateAsync();
        ctx.ChangeTracker.Clear();

        var categoryFolder = await ctx.Items.AsNoTracking()
            .SingleAsync(x => x.ParentId == DavItem.ContentFolder.Id && x.Name == "uncategorized");
        var repairedMount = await ctx.Items.AsNoTracking()
            .SingleAsync(x => x.Id == mountId);

        Assert.Equal(categoryFolder.Id, repairedMount.ParentId);
        Assert.Equal("/content/uncategorized/Orphan Release", repairedMount.Path);
        Assert.False(await ctx.Items.AsNoTracking().AnyAsync(x => x.Id == emptyFolderId));
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
            var databasePath = Path.Join(Path.GetTempPath(), $"nzbdav-empty-cat-{Guid.NewGuid():N}.sqlite");
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
