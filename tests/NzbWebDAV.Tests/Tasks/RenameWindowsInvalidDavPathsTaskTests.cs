using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Tasks;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Tasks;

[Collection(nameof(BaseTaskCollection))]
public class RenameWindowsInvalidDavPathsTaskTests
{
    public RenameWindowsInvalidDavPathsTaskTests()
    {
        PathSanitizer.SetWindowsSafePathsEnabled(true);
    }

    [Fact]
    public void BuildRenamePlan_RenamesInvalidComponent()
    {
        var parent = DavItem.ContentFolder;
        var invalid = DavItem.New(
            Guid.NewGuid(),
            parent,
            "Show: Title?",
            null,
            DavItem.ItemType.Directory,
            DavItem.ItemSubType.Directory,
            null,
            null,
            null,
            null);

        var plan = RenameWindowsInvalidDavPathsTask.BuildRenamePlan([parent, invalid]);

        Assert.Single(plan.Renames);
        Assert.Equal("Show: Title?", plan.Renames[0].OldName);
        Assert.Equal("Show_ Title_", plan.Renames[0].NewName);
        Assert.Equal("/content/Show: Title?", plan.Renames[0].OldPath);
        Assert.Equal("/content/Show_ Title_", plan.Renames[0].NewPath);
    }

    [Fact]
    public void BuildRenamePlan_UpdatesSubtreePaths()
    {
        var show = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            "Show: Title",
            null,
            DavItem.ItemType.Directory,
            DavItem.ItemSubType.Directory,
            null,
            null,
            null,
            null);
        var season = DavItem.New(
            Guid.NewGuid(),
            show,
            "Season 01.",
            null,
            DavItem.ItemType.Directory,
            DavItem.ItemSubType.Directory,
            null,
            null,
            null,
            null);
        var episode = DavItem.New(
            Guid.NewGuid(),
            season,
            "ep.mkv",
            10,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            null,
            null,
            null,
            null);

        var plan = RenameWindowsInvalidDavPathsTask.BuildRenamePlan(
            [DavItem.ContentFolder, show, season, episode]);

        Assert.Equal(2, plan.Renames.Count);
        var showRename = plan.Renames.Single(r => r.Id == show.Id);
        var seasonRename = plan.Renames.Single(r => r.Id == season.Id);
        Assert.Equal("/content/Show_ Title", showRename.NewPath);
        Assert.Equal("Season 01", seasonRename.NewName);
        Assert.Equal("/content/Show_ Title/Season 01", seasonRename.NewPath);

        var episodeUpdate = plan.PathUpdates.Single(u => u.Id == episode.Id);
        Assert.Equal("/content/Show_ Title/Season 01/ep.mkv", episodeUpdate.NewPath);
    }

    [Fact]
    public void BuildRenamePlan_SuffixesCollidingSanitizedNames()
    {
        var a = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            "A:B",
            10,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            null,
            null,
            null,
            null);
        var b = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            "A?B",
            10,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            null,
            null,
            null,
            null);
        // Deterministic order: lower path depth same, then Path ordinal, then Id.
        // Force path order by ensuring A:B sorts before A?B (':' < '?').
        var plan = RenameWindowsInvalidDavPathsTask.BuildRenamePlan(
            [DavItem.ContentFolder, a, b]);

        Assert.Equal(2, plan.Renames.Count);
        var first = plan.Renames.Single(r => r.Id == a.Id);
        var second = plan.Renames.Single(r => r.Id == b.Id);
        Assert.Equal("A_B", first.NewName);
        Assert.Equal("A_B_2", second.NewName);
        Assert.Equal("/content/A_B", first.NewPath);
        Assert.Equal("/content/A_B_2", second.NewPath);
    }

    [Fact]
    public void BuildRenamePlan_SuffixesWhenSanitizedNameAlreadyExists()
    {
        var existing = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            "Show_ Title_",
            10,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            null,
            null,
            null,
            null);
        var invalid = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            "Show: Title?",
            10,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            null,
            null,
            null,
            null);

        var plan = RenameWindowsInvalidDavPathsTask.BuildRenamePlan(
            [DavItem.ContentFolder, existing, invalid]);

        Assert.Single(plan.Renames);
        Assert.Equal("Show_ Title__2", plan.Renames[0].NewName);
        Assert.Equal("/content/Show_ Title__2", plan.Renames[0].NewPath);
    }

    [Fact]
    public void ResolveUniqueName_PreservesExtensionWhenSuffixing()
    {
        var id = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var names = new Dictionary<Guid, string>
        {
            [id] = "file:name.mkv",
            [otherId] = "file_name.mkv",
        };
        var paths = new Dictionary<Guid, string>
        {
            [id] = "/content/file:name.mkv",
            [otherId] = "/content/file_name.mkv",
        };
        var parents = new Dictionary<Guid, Guid?>
        {
            [id] = DavItem.ContentFolder.Id,
            [otherId] = DavItem.ContentFolder.Id,
        };

        var result = RenameWindowsInvalidDavPathsTask.ResolveUniqueName(
            "file_name.mkv",
            id,
            DavItem.ContentFolder.Id,
            "/content",
            names,
            paths,
            parents);

        Assert.Equal("file_name_2.mkv", result);
    }

    [Fact]
    public async Task Execute_DryRun_DoesNotWriteAndReportsDone()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        await using var harness = await TempDb.CreateAsync();
        try
        {
            var ctx = harness.Context;
            await SeedRootsAsync(ctx);
            var invalid = DavItem.New(
                Guid.NewGuid(),
                DavItem.ContentFolder,
                "Bad:Name?",
                10,
                DavItem.ItemType.UsenetFile,
                DavItem.ItemSubType.NzbFile,
                null,
                null,
                null,
                null);
            ctx.Items.Add(invalid);
            await ctx.SaveChangesAsync();

            var config = new ConfigManager();
            config.UpdateValues(
            [
                new ConfigItem { ConfigName = ConfigKeys.WebdavWindowsSafePaths, ConfigValue = "true" },
            ]);
            var websocket = new WebsocketManager();
            var task = new RenameWindowsInvalidDavPathsTask(
                config,
                websocket,
                isDryRun: true,
                createContext: () => harness.CreateContext());

            Assert.True(await task.Execute());

            await using var verify = harness.CreateContext();
            var stillBad = await verify.Items.SingleAsync(x => x.Id == invalid.Id);
            Assert.Equal("Bad:Name?", stillBad.Name);

            var progress = websocket.PeekLastMessage(WebsocketTopic.RenameWindowsInvalidPathsProgress);
            Assert.NotNull(progress);
            Assert.StartsWith("Dry Run - Done.", progress);
            Assert.Contains("Identified 1 rename", progress);

            var audit = RenameWindowsInvalidDavPathsTask.GetAuditReport();
            Assert.Contains("Bad:Name?", audit);
            Assert.Contains("Bad_Name_", audit);
        }
        finally
        {
            await BaseTask.ResetRunningTaskForTestsAsync();
            RenameWindowsInvalidDavPathsTask.ClearAuditForTests();
        }
    }

    [Fact]
    public async Task Execute_Apply_PersistsRenamesAndSubtreePaths()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        await using var harness = await TempDb.CreateAsync();
        try
        {
            var ctx = harness.Context;
            await SeedRootsAsync(ctx);
            var show = DavItem.New(
                Guid.NewGuid(),
                DavItem.ContentFolder,
                "Show: Title",
                null,
                DavItem.ItemType.Directory,
                DavItem.ItemSubType.Directory,
                null,
                null,
                null,
                null);
            var episode = DavItem.New(
                Guid.NewGuid(),
                show,
                "ep.mkv",
                10,
                DavItem.ItemType.UsenetFile,
                DavItem.ItemSubType.NzbFile,
                null,
                null,
                null,
                null);
            ctx.Items.AddRange(show, episode);
            await ctx.SaveChangesAsync();

            var config = new ConfigManager();
            config.UpdateValues(
            [
                new ConfigItem { ConfigName = ConfigKeys.WebdavWindowsSafePaths, ConfigValue = "true" },
            ]);
            var websocket = new WebsocketManager();
            var task = new RenameWindowsInvalidDavPathsTask(
                config,
                websocket,
                isDryRun: false,
                createContext: () => harness.CreateContext());

            Assert.True(await task.Execute());

            await using var verify = harness.CreateContext();
            var renamedShow = await verify.Items.SingleAsync(x => x.Id == show.Id);
            var renamedEpisode = await verify.Items.SingleAsync(x => x.Id == episode.Id);
            Assert.Equal("Show_ Title", renamedShow.Name);
            Assert.Equal("/content/Show_ Title", renamedShow.Path);
            Assert.Equal("ep.mkv", renamedEpisode.Name);
            Assert.Equal("/content/Show_ Title/ep.mkv", renamedEpisode.Path);
        }
        finally
        {
            await BaseTask.ResetRunningTaskForTestsAsync();
            RenameWindowsInvalidDavPathsTask.ClearAuditForTests();
        }
    }

    [Fact]
    public async Task Execute_AbortsWhenWindowsSafePathsDisabled()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        await using var harness = await TempDb.CreateAsync();
        try
        {
            var config = new ConfigManager();
            config.UpdateValues(
            [
                new ConfigItem { ConfigName = ConfigKeys.WebdavWindowsSafePaths, ConfigValue = "false" },
            ]);
            var websocket = new WebsocketManager();
            var task = new RenameWindowsInvalidDavPathsTask(
                config,
                websocket,
                isDryRun: true,
                createContext: () => harness.CreateContext());

            Assert.True(await task.Execute());
            var progress = websocket.PeekLastMessage(WebsocketTopic.RenameWindowsInvalidPathsProgress);
            Assert.NotNull(progress);
            Assert.Contains("Aborted:", progress);
            Assert.Contains("windows-safe-paths", progress);
        }
        finally
        {
            await BaseTask.ResetRunningTaskForTestsAsync();
            PathSanitizer.SetWindowsSafePathsEnabled(true);
            RenameWindowsInvalidDavPathsTask.ClearAuditForTests();
        }
    }

    private static async Task SeedRootsAsync(DavDatabaseContext ctx)
    {
        if (!await ctx.Items.AnyAsync(x => x.Id == DavItem.Root.Id))
            ctx.Items.Add(DavItem.Root);
        if (!await ctx.Items.AnyAsync(x => x.Id == DavItem.ContentFolder.Id))
            ctx.Items.Add(DavItem.ContentFolder);
        await ctx.SaveChangesAsync();
    }

    private sealed class TempDb : IAsyncDisposable
    {
        private readonly string _path;
        private TempDb(string path, DavDatabaseContext context)
        {
            _path = path;
            Context = context;
        }

        public DavDatabaseContext Context { get; }

        public DavDatabaseContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<DavDatabaseContext>()
                .UseSqlite($"Data Source={_path}")
                .AddInterceptors(new SqliteMainDbPragmas())
                .ReplaceService<IMigrationsSqlGenerator, SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
                .Options;
            return new DavDatabaseContext(options);
        }

        public static async Task<TempDb> CreateAsync()
        {
            var path = Path.Join(Path.GetTempPath(), $"nzbdav-winrename-{Guid.NewGuid():N}.sqlite");
            var options = new DbContextOptionsBuilder<DavDatabaseContext>()
                .UseSqlite($"Data Source={path}")
                .AddInterceptors(new SqliteMainDbPragmas())
                .ReplaceService<IMigrationsSqlGenerator, SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
                .Options;
            var context = new DavDatabaseContext(options);
            await context.Database.MigrateAsync();
            return new TempDb(path, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            try { File.Delete(_path); } catch (IOException) { /* best effort */ }
            try { File.Delete(_path + "-wal"); } catch (IOException) { /* best effort */ }
            try { File.Delete(_path + "-shm"); } catch (IOException) { /* best effort */ }
        }
    }
}
