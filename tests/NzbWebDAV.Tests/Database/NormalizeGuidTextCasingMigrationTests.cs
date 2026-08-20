using System.Data;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Tests.Database;

public sealed class NormalizeGuidTextCasingMigrationTests
{
    private const string PriorMigration = "20260818220000_Add-Par2-Repair-Jobs";

    [Fact]
    public void Inventory_CoversTwentySevenGuidColumnsAcrossEighteenTables()
    {
        Assert.Equal(18, GuidTextCasingSql.GuidColumns.Length);
        Assert.Equal(27, GuidTextCasingSql.GuidColumns.Sum(entry => entry.Columns.Length));
    }

    [Fact]
    public void NormalizeGuidTextCasing_IsSqliteOnly()
    {
        var postgresIds = typeof(PostgresDavDatabaseContext).Assembly.GetTypes()
            .Where(type => type.Namespace == "NzbWebDAV.Database.PostgresMigrations")
            .Select(type => type.GetCustomAttribute<MigrationAttribute>()?.Id)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(GuidTextCasingSql.MigrationId, postgresIds);
        Assert.Equal(
            GuidTextCasingSql.MigrationId,
            typeof(NzbWebDAV.Database.Migrations.NormalizeGuidTextCasing)
                .GetCustomAttribute<MigrationAttribute>()
                ?.Id);
    }

    [Fact]
    public async Task NormalizeGuidTextCasing_UppercasesLowercaseRowsAndRestoresEfLookups()
    {
        await using var harness = await MigrationHarness.CreateAsync();
        var ctx = harness.Context;

        var dirId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0101");
        var nzbId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0102");
        var rarId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0103");
        var multiId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0104");
        var historyId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0105");
        var queueId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0106");
        var blobId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0107");
        var nzbBlobId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0108");
        var healthId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0109");
        var listId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0110");
        var wantedId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0111");
        var groupId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0112");
        var par2Id = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0113");
        var clickId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0114");
        var davCleanupId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0115");

        var dir = NewDirectory(dirId, DavItem.ContentFolder, "guid-case");
        var nzbItem = DavItem.New(
            nzbId, dir, "file.nzb", 10,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
            releaseDate: null, lastHealthCheck: null,
            historyItemId: historyId, fileBlobId: blobId, nzbBlobId: nzbBlobId);
        var rarItem = DavItem.New(
            rarId, dir, "file.rar", 10,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.RarFile,
            releaseDate: null, lastHealthCheck: null,
            historyItemId: null, fileBlobId: null);
        var multiItem = DavItem.New(
            multiId, dir, "file.mkv", 10,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.MultipartFile,
            releaseDate: null, lastHealthCheck: null,
            historyItemId: null, fileBlobId: null);

        ctx.Items.AddRange(dir, nzbItem, rarItem, multiItem);
        ctx.NzbFiles.Add(new DavNzbFile { Id = nzbId, SegmentIds = ["seg"] });
        ctx.RarFiles.Add(new DavRarFile { Id = rarId, RarParts = [] });
        ctx.MultipartFiles.Add(new DavMultipartFile { Id = multiId, Metadata = new DavMultipartFile.Meta() });
        ctx.HistoryItems.Add(new HistoryItem
        {
            Id = historyId,
            CreatedAt = DateTime.UtcNow,
            FileName = "hist.nzb",
            JobName = "hist",
            Category = "tv",
            DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
            TotalSegmentBytes = 20,
            DownloadTimeSeconds = 1,
            DownloadDirId = dirId,
            NzbBlobId = nzbBlobId,
        });
        ctx.QueueItems.Add(new QueueItem
        {
            Id = queueId,
            CreatedAt = DateTime.UtcNow,
            SortOrder = QueueItem.SortOrderStride,
            FileName = "queue.nzb",
            JobName = "queue",
            NzbFileSize = 10,
            TotalSegmentBytes = 20,
            Category = "tv",
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None,
        });
        ctx.QueueNzbContents.Add(new QueueNzbContents { Id = queueId, NzbContents = "<nzb />" });
        ctx.HealthCheckResults.Add(new HealthCheckResult
        {
            Id = healthId,
            CreatedAt = DateTimeOffset.UtcNow,
            DavItemId = nzbId,
            Path = nzbItem.Path,
            Result = HealthCheckResult.HealthResult.Healthy,
            RepairStatus = HealthCheckResult.RepairAction.None,
        });
        ctx.BlobCleanupItems.Add(new BlobCleanupItem { Id = blobId });
        ctx.DavCleanupItems.Add(new DavCleanupItem { Id = davCleanupId });
        ctx.HistoryCleanupItems.Add(new HistoryCleanupItem { Id = historyId, DeleteMountedFiles = false });
        ctx.NzbBlobCleanupItems.Add(new NzbBlobCleanupItem { Id = nzbBlobId });
        ctx.NzbNames.Add(new NzbName { Id = nzbBlobId, FileName = "hist.nzb" });
        ctx.ListSources.Add(new ListSource
        {
            Id = listId,
            Kind = ListSource.KindManual,
            Name = "lists",
            Enabled = true,
            Cap = 10,
            CreatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        ctx.WantedItems.Add(new WantedItem
        {
            Id = wantedId,
            Key = "movie:tt1",
            Type = "movie",
            ContentId = "tt1",
            Title = "Title",
            State = WantedItem.StateScouting,
            CreatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            UpdatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        ctx.NzbResolutionGroups.Add(new NzbResolutionGroup
        {
            Id = groupId,
            Type = "movie",
            ProfileToken = "p",
            SearchId = "s",
            CandidatesJson = "[]",
            TokensJson = "[]",
            CreatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        ctx.Par2RepairJobs.Add(new Par2RepairJob
        {
            Id = par2Id,
            DavItemId = nzbId,
            Path = nzbItem.Path,
            State = Par2RepairJob.RepairJobState.Queued,
            MissingSegmentIds = ["seg"],
            CreatedAt = DateTimeOffset.UtcNow,
        });
        ctx.WatchdogEntries.Add(new WatchdogEntry
        {
            ClickId = clickId,
            AttemptedAt = DateTimeOffset.UtcNow,
            ContentType = "movie",
            RequestedTitle = "Title",
            CandidateTitle = "Title",
            IndexerName = "ix",
            Result = WatchdogEntry.Outcome.QueueCompleted,
            QueueItemId = queueId,
        });

        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        await LowercaseAllGuidColumnsAsync(ctx);

        Assert.False(await ctx.Items.AsNoTracking().AnyAsync(x => x.Id == dirId));
        Assert.Equal(Lower(dirId), await ReadTextAsync(ctx, $"SELECT Id FROM DavItems WHERE lower(Id) = '{Lower(dirId)}'"));

        await ctx.Database.MigrateAsync();
        ctx.ChangeTracker.Clear();

        Assert.True(await ctx.Items.AsNoTracking().AnyAsync(x => x.Id == dirId));
        Assert.True(await ctx.NzbFiles.AsNoTracking().AnyAsync(x => x.Id == nzbId));
        Assert.True(await ctx.RarFiles.AsNoTracking().AnyAsync(x => x.Id == rarId));
        Assert.True(await ctx.MultipartFiles.AsNoTracking().AnyAsync(x => x.Id == multiId));
        Assert.True(await ctx.QueueItems.AsNoTracking().AnyAsync(x => x.Id == queueId));
        Assert.True(await ctx.QueueNzbContents.AsNoTracking().AnyAsync(x => x.Id == queueId));
        Assert.True(await ctx.HistoryItems.AsNoTracking().AnyAsync(x => x.Id == historyId));
        Assert.True(await ctx.HealthCheckResults.AsNoTracking().AnyAsync(x => x.Id == healthId));
        Assert.True(await ctx.Par2RepairJobs.AsNoTracking().AnyAsync(x => x.Id == par2Id));
        Assert.True(await ctx.WantedItems.AsNoTracking().AnyAsync(x => x.Id == wantedId));
        Assert.True(await ctx.ListSources.AsNoTracking().AnyAsync(x => x.Id == listId));
        Assert.True(await ctx.NzbNames.AsNoTracking().AnyAsync(x => x.Id == nzbBlobId));
        Assert.True(await ctx.NzbResolutionGroups.AsNoTracking().AnyAsync(x => x.Id == groupId));
        Assert.True(await ctx.WatchdogEntries.AsNoTracking().AnyAsync(x => x.ClickId == clickId && x.QueueItemId == queueId));

        var repaired = await ctx.Items.AsNoTracking().SingleAsync(x => x.Id == nzbId);
        Assert.Equal(dirId, repaired.ParentId);
        Assert.Equal(historyId, repaired.HistoryItemId);
        Assert.Equal(blobId, repaired.FileBlobId);
        Assert.Equal(nzbBlobId, repaired.NzbBlobId);
        Assert.Equal(nzbId.GetFiveLengthPrefix(), repaired.IdPrefix);
        Assert.Equal(Upper(nzbId), await ReadTextAsync(ctx, $"SELECT Id FROM DavItems WHERE Id = '{Upper(nzbId)}'"));
        Assert.Equal(Upper(queueId), await ReadTextAsync(ctx, $"SELECT Id FROM QueueItems WHERE Id = '{Upper(queueId)}'"));
        Assert.Equal(Upper(clickId), await ReadTextAsync(ctx, $"SELECT ClickId FROM WatchdogEntries WHERE ClickId = '{Upper(clickId)}'"));
        Assert.Empty(await ctx.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task NormalizeGuidTextCasing_RenamesParentIdNameCollisionsBeforeRewrite()
    {
        await using var harness = await MigrationHarness.CreateAsync();
        var ctx = harness.Context;

        var parentId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0201");
        var keepId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0202");
        var renameId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0203");
        var contentId = Upper(DavItem.ContentFolder.Id);

        await ExecAsync(ctx, $"""
            INSERT INTO DavItems (Id, IdPrefix, CreatedAt, ParentId, Name, Type, SubType, Path)
            VALUES
              ('{Upper(parentId)}', '{parentId.GetFiveLengthPrefix()}', datetime('now'), '{contentId}', 'case-parent', 1, 101, '/content/case-parent'),
              ('{Upper(keepId)}', '{keepId.GetFiveLengthPrefix()}', datetime('now'), '{Upper(parentId)}', 'Shared', 1, 101, '/content/case-parent/Shared'),
              ('{Lower(renameId)}', '{renameId.GetFiveLengthPrefix()}', datetime('now'), '{Lower(parentId)}', 'Shared', 1, 101, '/content/case-parent/Shared-lower');
            """);
        ctx.ChangeTracker.Clear();

        await ctx.Database.MigrateAsync();
        ctx.ChangeTracker.Clear();

        var kept = await ctx.Items.AsNoTracking().SingleAsync(x => x.Id == keepId);
        var renamed = await ctx.Items.AsNoTracking().SingleAsync(x => x.Id == renameId);
        Assert.Equal("Shared", kept.Name);
        Assert.Equal($"Shared ({Upper(renameId)[..5]})", renamed.Name);
        Assert.Equal(parentId, kept.ParentId);
        Assert.Equal(parentId, renamed.ParentId);
        Assert.Equal("/content/case-parent/Shared", kept.Path);
        Assert.Equal($"/content/case-parent/{renamed.Name}", renamed.Path);
    }

    [Fact]
    public async Task NormalizeGuidTextCasing_AbortsWhenIdsDifferOnlyByCase()
    {
        await using var harness = await MigrationHarness.CreateAsync();
        var ctx = harness.Context;
        var contentId = Upper(DavItem.ContentFolder.Id);
        var lowerId = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee03aa";
        var upperId = "AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEE03AA";

        await ExecAsync(ctx, $"""
            INSERT INTO DavItems (Id, IdPrefix, CreatedAt, ParentId, Name, Type, SubType, Path)
            VALUES
              ('{lowerId}', 'aaaaa', datetime('now'), '{contentId}', 'dup-lower', 1, 101, '/content/dup-lower'),
              ('{upperId}', 'aaaaa', datetime('now'), '{contentId}', 'dup-upper', 1, 101, '/content/dup-upper');
            """);
        ctx.ChangeTracker.Clear();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ctx.Database.MigrateAsync());
        Assert.Contains("duplicate DavItems.Id values differ only by case", ex.Message, StringComparison.Ordinal);

        Assert.Contains(
            GuidTextCasingSql.MigrationId,
            await ctx.Database.GetPendingMigrationsAsync());
        Assert.Equal(lowerId, await ReadTextAsync(ctx, $"SELECT Id FROM DavItems WHERE Name = 'dup-lower'"));
        Assert.Equal(upperId, await ReadTextAsync(ctx, $"SELECT Id FROM DavItems WHERE Name = 'dup-upper'"));
    }

    [Fact]
    public async Task NormalizeGuidTextCasing_DoesNotEnqueueBlobCleanupWhenRewritingFileBlobId()
    {
        await using var harness = await MigrationHarness.CreateAsync();
        var ctx = harness.Context;

        var fileId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0401");
        var blobId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0402");
        var file = DavItem.New(
            fileId, DavItem.ContentFolder, "blob.nzb", 10,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
            releaseDate: null, lastHealthCheck: null,
            historyItemId: null, fileBlobId: blobId);
        ctx.Items.Add(file);
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        await LowercaseAllGuidColumnsAsync(ctx);
        // Lowercasing FileBlobId fires TR_DavItems_Update_AddBlobCleanup; clear that
        // test artifact so this assertion only covers the migration rewrite.
        await ExecAsync(ctx, "DELETE FROM BlobCleanupItems;");
        await ctx.Database.MigrateAsync();
        ctx.ChangeTracker.Clear();

        Assert.False(await ctx.BlobCleanupItems.AsNoTracking().AnyAsync(x => x.Id == blobId));

        var replacement = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0403");
        await ExecAsync(ctx, $"""
            UPDATE DavItems SET FileBlobId = '{Upper(replacement)}' WHERE Id = '{Upper(fileId)}';
            """);

        var enqueued = await ctx.BlobCleanupItems.AsNoTracking().SingleAsync();
        Assert.Equal(blobId, enqueued.Id);
        Assert.Equal(Upper(blobId), await ReadTextAsync(ctx, "SELECT Id FROM BlobCleanupItems LIMIT 1"));
    }

    [Fact]
    public async Task NormalizeGuidTextCasing_DeleteDirectoryTriggerCopiesUppercaseId()
    {
        await using var harness = await MigrationHarness.CreateAsync();
        var ctx = harness.Context;

        var dirId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0501");
        ctx.Items.Add(NewDirectory(dirId, DavItem.ContentFolder, "to-delete"));
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        await LowercaseAllGuidColumnsAsync(ctx);
        await ctx.Database.MigrateAsync();
        ctx.ChangeTracker.Clear();

        await ExecAsync(ctx, $"DELETE FROM DavItems WHERE Id = '{Upper(dirId)}';");

        var cleanupId = await ReadTextAsync(ctx, "SELECT Id FROM DavCleanupItems WHERE Id = '" + Upper(dirId) + "'");
        Assert.Equal(Upper(dirId), cleanupId);
    }

    [Fact]
    public async Task NormalizeGuidTextCasing_IsIdempotentOnAlreadyUppercaseRows()
    {
        await using var harness = await MigrationHarness.CreateAsync();
        var ctx = harness.Context;

        var dirId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeee0601");
        ctx.Items.Add(NewDirectory(dirId, DavItem.ContentFolder, "already-upper"));
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        await ctx.Database.MigrateAsync();
        ctx.ChangeTracker.Clear();

        Assert.Equal(Upper(dirId), await ReadTextAsync(ctx, $"SELECT Id FROM DavItems WHERE Id = '{Upper(dirId)}'"));
        var item = await ctx.Items.AsNoTracking().SingleAsync(x => x.Id == dirId);
        Assert.Equal(dirId.GetFiveLengthPrefix(), item.IdPrefix);
        Assert.Equal("/content/already-upper", item.Path);
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

    private static string Lower(Guid id) => id.ToString("D").ToLowerInvariant();

    private static string Upper(Guid id) => id.ToString("D").ToUpperInvariant();

    private static async Task ExecAsync(DavDatabaseContext ctx, string sql)
    {
        var connection = (SqliteConnection)ctx.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task LowercaseAllGuidColumnsAsync(DavDatabaseContext ctx)
    {
        var connection = (SqliteConnection)ctx.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = OFF;";
        await command.ExecuteNonQueryAsync();
        command.CommandText = GuidTextCasingSql.LowercaseAllSql;
        await command.ExecuteNonQueryAsync();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadTextAsync(DavDatabaseContext ctx, string sql)
    {
        var connection = (SqliteConnection)ctx.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return Assert.IsType<string>(value);
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
            var databasePath = Path.Join(Path.GetTempPath(), $"nzbdav-guid-case-{Guid.NewGuid():N}.sqlite");
            var options = new DbContextOptionsBuilder<DavDatabaseContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .AddInterceptors(new SqliteMainDbPragmas())
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
            try { File.Delete(_databasePath + "-wal"); } catch (IOException) { }
            try { File.Delete(_databasePath + "-shm"); } catch (IOException) { }
        }
    }
}
