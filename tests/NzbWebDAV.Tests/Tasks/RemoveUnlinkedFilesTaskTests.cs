using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Tasks;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Tasks;

[Collection(nameof(BaseTaskCollection))]
public class RemoveUnlinkedFilesTaskTests
{
    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Utc)]
    public void ToPostgresWallClock_NormalizesDateTimeKind(DateTimeKind kind)
    {
        var value = new DateTime(2026, 8, 23, 12, 34, 56, kind);

        var result = DavDatabaseContext.ToPostgresWallClock(value);

        Assert.Equal(DateTimeKind.Unspecified, result.Kind);
        Assert.Equal(
            kind == DateTimeKind.Utc ? value.ToLocalTime().Ticks : value.Ticks,
            result.Ticks);
    }

    [Fact]
    public void CreateWallClockParameter_UsesPostgresTimestamp()
    {
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseNpgsql("Host=localhost;Database=nzbdav")
            .Options;
        using var context = new DavDatabaseContext(options);
        var value = new DateTime(2026, 8, 23, 12, 34, 56, DateTimeKind.Utc);

        var parameter = Assert.IsType<NpgsqlParameter>(
            RemoveUnlinkedFilesTask.CreateWallClockParameter(context, value));

        Assert.Equal(NpgsqlDbType.Timestamp, parameter.NpgsqlDbType);
        var boundValue = Assert.IsType<DateTime>(parameter.Value);
        Assert.Equal(DateTimeKind.Unspecified, boundValue.Kind);
        Assert.Equal(value.ToLocalTime().Ticks, boundValue.Ticks);
    }

    [Fact]
    public async Task RemoveEmptyDirectoriesAsync_RemovesNestedEmptyDirsAndTerminates()
    {
        await using var harness = await TempDb.CreateAsync();
        var ctx = harness.Context;
        var createdBefore = DateTime.Now.AddMinutes(1);

        // Category folder under /content is protected; nested empties beneath it are removed.
        var category = NewDir(Guid.NewGuid(), DavItem.ContentFolder, "movies");
        var parent = NewDir(Guid.NewGuid(), category, "show");
        var child = NewDir(Guid.NewGuid(), parent, "season");
        var grandchild = NewDir(Guid.NewGuid(), child, "empty");
        var keptFile = DavItem.New(
            Guid.NewGuid(),
            category,
            "keep.mkv",
            10,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            null,
            null,
            null,
            null);
        await SeedRootsAsync(ctx);
        ctx.Items.AddRange(category, parent, child, grandchild, keptFile);
        await ctx.SaveChangesAsync();

        var removed = await RemoveUnlinkedFilesTask.RemoveEmptyDirectoriesAsync(
            ctx,
            createdBefore);

        Assert.True(removed >= 2);
        Assert.False(await ctx.Items.AnyAsync(x => x.Id == grandchild.Id));
        Assert.False(await ctx.Items.AnyAsync(x => x.Id == child.Id));
        Assert.False(await ctx.Items.AnyAsync(x => x.Id == parent.Id));
        Assert.True(await ctx.Items.AnyAsync(x => x.Id == category.Id));
        Assert.True(await ctx.Items.AnyAsync(x => x.Id == keptFile.Id));
    }

    [Fact]
    public async Task RemoveEmptyDirectoriesAsync_PreservesEmptyCategoryFolderUnderContent()
    {
        await using var harness = await TempDb.CreateAsync();
        var ctx = harness.Context;
        await SeedRootsAsync(ctx);

        var category = NewDir(Guid.NewGuid(), DavItem.ContentFolder, "tv");
        ctx.Items.Add(category);
        await ctx.SaveChangesAsync();

        var createdBefore = DateTime.Now.AddMinutes(1);
        await RemoveUnlinkedFilesTask.RemoveEmptyDirectoriesAsync(ctx, createdBefore);

        Assert.True(await ctx.Items.AnyAsync(x => x.Id == category.Id));
    }

    [Fact]
    public async Task RemoveEmptyDirectoriesAsync_PreservesEmptyDirWithHistoryItemId()
    {
        await using var harness = await TempDb.CreateAsync();
        var ctx = harness.Context;
        await SeedRootsAsync(ctx);

        var category = NewDir(Guid.NewGuid(), DavItem.ContentFolder, "movies");
        var mountFolder = DavItem.New(
            Guid.NewGuid(),
            category,
            "Some.Release",
            null,
            DavItem.ItemType.Directory,
            DavItem.ItemSubType.Directory,
            null,
            null,
            historyItemId: Guid.NewGuid(),
            fileBlobId: null);
        ctx.Items.AddRange(category, mountFolder);
        await ctx.SaveChangesAsync();

        var createdBefore = DateTime.Now.AddMinutes(1);
        var removed = await RemoveUnlinkedFilesTask.RemoveEmptyDirectoriesAsync(ctx, createdBefore);

        Assert.Equal(0, removed);
        Assert.True(await ctx.Items.AnyAsync(x => x.Id == mountFolder.Id));
    }

    [Fact]
    public async Task DeleteEmptyDirectoriesByIdTextAsync_SkipsDirThatGainedChild()
    {
        await using var harness = await TempDb.CreateAsync();
        var ctx = harness.Context;
        await SeedRootsAsync(ctx);

        var category = NewDir(Guid.NewGuid(), DavItem.ContentFolder, "movies");
        var emptyDir = NewDir(Guid.NewGuid(), category, "release");
        ctx.Items.AddRange(category, emptyDir);
        await ctx.SaveChangesAsync();

        var candidates = new[]
        {
            new RemoveUnlinkedFilesTask.UnlinkedItemInfo(
                emptyDir.Id.ToString().ToUpperInvariant(),
                (int)emptyDir.Type,
                emptyDir.Path),
        };

        // Simulate a concurrent queue insert between SELECT and DELETE.
        var child = DavItem.New(
            Guid.NewGuid(),
            emptyDir,
            "video.mkv",
            10,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            null,
            null,
            null,
            null);
        ctx.Items.Add(child);
        await ctx.SaveChangesAsync();

        var deleted = await RemoveUnlinkedFilesTask.DeleteEmptyDirectoriesByIdTextAsync(
            ctx, candidates);

        Assert.Equal(0, deleted);
        Assert.True(await ctx.Items.AnyAsync(x => x.Id == emptyDir.Id));
        Assert.True(await ctx.Items.AnyAsync(x => x.Id == child.Id));
    }

    [Fact]
    public async Task RemoveEmptyDirectoriesAsync_ReturnsZero_WhenNoEmptyDirectories()
    {
        await using var harness = await TempDb.CreateAsync();
        var ctx = harness.Context;
        await SeedRootsAsync(ctx);
        var file = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            "only.mkv",
            10,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            null,
            null,
            null,
            null);
        ctx.Items.Add(file);
        await ctx.SaveChangesAsync();

        // Category folders under /content are protected; migration-seeded empties
        // (e.g. /content/uncategorized) remain and must not block a clean second pass.
        var createdBefore = DateTime.Now.AddMinutes(1);
        await RemoveUnlinkedFilesTask.RemoveEmptyDirectoriesAsync(ctx, createdBefore);

        var removed = await RemoveUnlinkedFilesTask.RemoveEmptyDirectoriesAsync(
            ctx,
            createdBefore);

        Assert.Equal(0, removed);
        Assert.True(await ctx.Items.AnyAsync(x => x.Id == file.Id));
    }

    [Fact]
    public async Task DryRun_DoesNotTreatLowercaseLinkedIdAsUnlinked()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        var libraryDir = Path.Join(Path.GetTempPath(), $"nzbdav-lib-{Guid.NewGuid():N}");
        Directory.CreateDirectory(libraryDir);
        await using var harness = await TempDb.CreateAsync();
        try
        {
            var ctx = harness.Context;
            await SeedRootsAsync(ctx);

            var linkedIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
            foreach (var id in linkedIds)
            {
                ctx.Items.Add(DavItem.New(
                    id,
                    DavItem.ContentFolder,
                    $"{id:N}.mkv",
                    10,
                    DavItem.ItemType.UsenetFile,
                    DavItem.ItemSubType.NzbFile,
                    null,
                    null,
                    null,
                    null));
            }

            await ctx.SaveChangesAsync();

            // Seed a lowercase-Id UsenetFile the way Fix-Empty-Categories seeds folders.
            var lowercaseId = Guid.NewGuid();
            var lowercaseIdText = lowercaseId.ToString().ToLowerInvariant();
            await ctx.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO DavItems (Id, IdPrefix, CreatedAt, ParentId, Name, FileSize, Type, SubType, Path)
                VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8})
                """,
                lowercaseIdText,
                lowercaseIdText[..5],
                DateTime.Now.AddMinutes(-1),
                DavItem.ContentFolder.Id.ToString(),
                $"{lowercaseId:N}.mkv",
                10L,
                (int)DavItem.ItemType.UsenetFile,
                (int)DavItem.ItemSubType.NzbFile,
                $"/content/{lowercaseId:N}.mkv");

            foreach (var id in linkedIds.Append(lowercaseId))
            {
                await File.WriteAllTextAsync(
                    Path.Join(libraryDir, $"{id:N}.strm"),
                    $"http://localhost/view/.ids/{id}.mkv");
            }

            var config = new ConfigManager();
            config.UpdateValues(
            [
                new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = libraryDir },
            ]);

            var websocket = new WebsocketManager();
            var task = new RemoveUnlinkedFilesTask(
                config,
                websocket,
                isDryRun: true,
                createContext: () => harness.CreateContext());

            Assert.True(await task.Execute());

            var progress = websocket.PeekLastMessage(WebsocketTopic.CleanupTaskProgress);
            Assert.NotNull(progress);
            Assert.StartsWith("Dry Run - Done.", progress);
            Assert.Contains("Identified 0 unlinked files", progress);
        }
        finally
        {
            await BaseTask.ResetRunningTaskForTestsAsync();
            RemoveUnlinkedFilesTask.ClearAuditPathsForTests();
            try { Directory.Delete(libraryDir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public async Task Execute_ReturnsFalse_WhenAnotherTaskIsRunning()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        try
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var longRunning = new BlockingTask(gate.Task);
            var run = longRunning.Execute();

            // Give the long-running task time to claim the single-flight slot.
            await Task.Delay(50);
            var second = new NoOpTask();
            Assert.False(await second.Execute());

            gate.SetResult();
            Assert.True(await run);
        }
        finally
        {
            await BaseTask.ResetRunningTaskForTestsAsync();
        }
    }

    [Fact]
    public async Task DryRun_ReportsDone_WithTerminalProgress()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        var libraryDir = Path.Join(Path.GetTempPath(), $"nzbdav-lib-{Guid.NewGuid():N}");
        Directory.CreateDirectory(libraryDir);
        await using var harness = await TempDb.CreateAsync();
        try
        {
            var ctx = harness.Context;
            await SeedRootsAsync(ctx);

            var linkedIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
            var orphanId = Guid.NewGuid();
            foreach (var id in linkedIds.Append(orphanId))
            {
                ctx.Items.Add(DavItem.New(
                    id,
                    DavItem.ContentFolder,
                    $"{id:N}.mkv",
                    10,
                    DavItem.ItemType.UsenetFile,
                    DavItem.ItemSubType.NzbFile,
                    null,
                    null,
                    null,
                    null));
            }

            await ctx.SaveChangesAsync();

            foreach (var id in linkedIds)
            {
                await File.WriteAllTextAsync(
                    Path.Join(libraryDir, $"{id:N}.strm"),
                    $"http://localhost/view/.ids/{id}.mkv");
            }

            var config = new ConfigManager();
            config.UpdateValues(
            [
                new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = libraryDir },
            ]);

            var websocket = new WebsocketManager();
            var messages = new ConcurrentQueue<string>();
            var task = new RemoveUnlinkedFilesTask(
                config,
                websocket,
                isDryRun: true,
                createContext: () => harness.CreateContext(),
                progressObserver: messages.Enqueue);

            Assert.True(await task.Execute());

            var progress = websocket.PeekLastMessage(WebsocketTopic.CleanupTaskProgress);
            Assert.NotNull(progress);
            Assert.StartsWith("Dry Run - Done.", progress);
            Assert.Contains("Identified 1 unlinked files", progress);
            AssertMessagesAppearInOrder(
                messages,
                "Scanning all linked files",
                "Indexing 5 linked files",
                "Searching for unlinked webdav items",
                "Identifying unlinked files",
                "Done. Identified 1 unlinked files");
        }
        finally
        {
            await BaseTask.ResetRunningTaskForTestsAsync();
            RemoveUnlinkedFilesTask.ClearAuditPathsForTests();
            try { Directory.Delete(libraryDir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public async Task DryRun_Completes_WhenProgressObserverThrowsOnce()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        var libraryDir = Path.Join(Path.GetTempPath(), $"nzbdav-lib-{Guid.NewGuid():N}");
        Directory.CreateDirectory(libraryDir);
        await using var harness = await TempDb.CreateAsync();
        try
        {
            var ctx = harness.Context;
            await SeedRootsAsync(ctx);

            var linkedIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
            var orphanId = Guid.NewGuid();
            foreach (var id in linkedIds.Append(orphanId))
            {
                ctx.Items.Add(DavItem.New(
                    id,
                    DavItem.ContentFolder,
                    $"{id:N}.mkv",
                    10,
                    DavItem.ItemType.UsenetFile,
                    DavItem.ItemSubType.NzbFile,
                    null,
                    null,
                    null,
                    null));
            }

            await ctx.SaveChangesAsync();

            foreach (var id in linkedIds)
            {
                await File.WriteAllTextAsync(
                    Path.Join(libraryDir, $"{id:N}.strm"),
                    $"http://localhost/view/.ids/{id}.mkv");
            }

            var config = new ConfigManager();
            config.UpdateValues(
            [
                new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = libraryDir },
            ]);

            var websocket = new WebsocketManager();
            var invocation = 0;
            var messages = new ConcurrentQueue<string>();
            void Observer(string message)
            {
                if (Interlocked.Increment(ref invocation) == 1)
                    throw new InvalidOperationException("progress observer failure");
                messages.Enqueue(message);
            }

            var task = new RemoveUnlinkedFilesTask(
                config,
                websocket,
                isDryRun: true,
                createContext: () => harness.CreateContext(),
                progressObserver: Observer);

            Assert.True(await task.Execute());

            Assert.True(await ctx.Items.AnyAsync(x => x.Id == orphanId));
            foreach (var id in linkedIds)
                Assert.True(await ctx.Items.AnyAsync(x => x.Id == id));

            var progress = websocket.PeekLastMessage(WebsocketTopic.CleanupTaskProgress);
            Assert.Equal("Dry Run - Done. Identified 1 unlinked files.", progress);
            Assert.Contains(
                messages,
                message => message == "Dry Run - Done. Identified 1 unlinked files.");
            Assert.DoesNotContain(
                messages,
                message => message.StartsWith("Dry Run - Failed:", StringComparison.Ordinal)
                    || message.StartsWith("Failed:", StringComparison.Ordinal));
        }
        finally
        {
            await BaseTask.ResetRunningTaskForTestsAsync();
            RemoveUnlinkedFilesTask.ClearAuditPathsForTests();
            try { Directory.Delete(libraryDir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public async Task DryRun_Succeeds_WhenPreviousRunLeftUniqueTempTableBehind()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        var libraryDir = Path.Join(Path.GetTempPath(), $"nzbdav-lib-{Guid.NewGuid():N}");
        Directory.CreateDirectory(libraryDir);
        await using var harness = await TempDb.CreateAsync();
        try
        {
            var ctx = harness.Context;
            await SeedRootsAsync(ctx);

            var linkedIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
            var orphanId = Guid.NewGuid();
            foreach (var id in linkedIds.Append(orphanId))
            {
                ctx.Items.Add(DavItem.New(
                    id,
                    DavItem.ContentFolder,
                    $"{id:N}.mkv",
                    10,
                    DavItem.ItemType.UsenetFile,
                    DavItem.ItemSubType.NzbFile,
                    null,
                    null,
                    null,
                    null));
            }

            await ctx.SaveChangesAsync();

            foreach (var id in linkedIds)
            {
                await File.WriteAllTextAsync(
                    Path.Join(libraryDir, $"{id:N}.strm"),
                    $"http://localhost/view/.ids/{id}.mkv");
            }

            // Strand the unique temp table the way a run interrupted between its CREATE and
            // RENAME does. Every later run used to fail on "table already exists".
            await ctx.Database.ExecuteSqlRawAsync(
                "CREATE TABLE TMP_LINKED_FILES_UNIQUE (Id TEXT NOT NULL COLLATE NOCASE PRIMARY KEY);");

            var config = new ConfigManager();
            config.UpdateValues(
            [
                new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = libraryDir },
            ]);

            var websocket = new WebsocketManager();
            var task = new RemoveUnlinkedFilesTask(
                config,
                websocket,
                isDryRun: true,
                createContext: () => harness.CreateContext());

            Assert.True(await task.Execute());

            // Assert on progress, not the return value: ExecuteInternal catches the failure and
            // reports "Failed: ...", so Execute() returns true either way.
            var progress = websocket.PeekLastMessage(WebsocketTopic.CleanupTaskProgress);
            Assert.NotNull(progress);
            Assert.DoesNotContain("already exists", progress);
            Assert.StartsWith("Dry Run - Done.", progress);
            Assert.Contains("Identified 1 unlinked files", progress);
        }
        finally
        {
            await BaseTask.ResetRunningTaskForTestsAsync();
            RemoveUnlinkedFilesTask.ClearAuditPathsForTests();
            try { Directory.Delete(libraryDir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Theory]
    [InlineData("/mnt/debrid/nzbdav/completed-symlinks", "/mnt/debrid/nzbdav", true)]
    [InlineData("/mnt/debrid/nzbdav", "/mnt/debrid/nzbdav", true)]
    [InlineData("/mnt/debrid/nzbdav/", "/mnt/debrid/nzbdav", true)]
    [InlineData("/mnt/debrid/nzbdav/content", "/mnt/debrid/nzbdav", true)]
    [InlineData("/mnt/debrid/nzbdav-symlinks", "/mnt/debrid/nzbdav", false)]
    [InlineData("/mnt/debrid/combined_symlinks", "/mnt/debrid/nzbdav", false)]
    [InlineData("/mnt/media", "/mnt/debrid/nzbdav", false)]
    [InlineData("", "/mnt/debrid/nzbdav", false)]
    [InlineData(null, "/mnt/debrid/nzbdav", false)]
    public void IsLibraryDirInsideRcloneMount_DetectsVirtualMountPaths(
        string? libraryDir,
        string mountDir,
        bool expected)
    {
        Assert.Equal(
            expected,
            RemoveUnlinkedFilesTask.IsLibraryDirInsideRcloneMount(
                libraryDir, mountDir, out _, out _));
    }

    [Fact]
    public void IsLibraryDirInsideRcloneMount_UsesOsAwareCasing()
    {
        var inside = RemoveUnlinkedFilesTask.IsLibraryDirInsideRcloneMount(
            "/mnt/debrid/NZBDAV/completed-symlinks",
            "/mnt/debrid/nzbdav",
            out _,
            out _);

        Assert.Equal(OperatingSystem.IsWindows(), inside);
    }

    [Fact]
    public async Task DryRun_Aborts_WhenLibraryDirIsInsideRcloneMount()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        var mountDir = Path.Join(Path.GetTempPath(), $"nzbdav-mount-{Guid.NewGuid():N}");
        var libraryDir = Path.Join(mountDir, "completed-symlinks");
        Directory.CreateDirectory(libraryDir);
        await using var harness = await TempDb.CreateAsync();
        try
        {
            var config = new ConfigManager();
            config.UpdateValues(
            [
                new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = libraryDir },
                new ConfigItem { ConfigName = ConfigKeys.RcloneMountDir, ConfigValue = mountDir },
            ]);

            var websocket = new WebsocketManager();
            var task = new RemoveUnlinkedFilesTask(
                config,
                websocket,
                isDryRun: true,
                createContext: () => harness.CreateContext());

            Assert.True(await task.Execute());

            var progress = websocket.PeekLastMessage(WebsocketTopic.CleanupTaskProgress);
            Assert.NotNull(progress);
            Assert.Contains("Aborted:", progress, StringComparison.Ordinal);
            Assert.Contains("inside the rclone mount", progress, StringComparison.Ordinal);
            Assert.Contains(libraryDir, progress, StringComparison.Ordinal);
            Assert.Contains(mountDir, progress, StringComparison.Ordinal);
        }
        finally
        {
            await BaseTask.ResetRunningTaskForTestsAsync();
            RemoveUnlinkedFilesTask.ClearAuditPathsForTests();
            try { Directory.Delete(mountDir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public async Task DryRun_DoesNotAbort_WhenLibraryDirIsOutsideRcloneMount()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        var mountDir = Path.Join(Path.GetTempPath(), $"nzbdav-mount-{Guid.NewGuid():N}");
        var libraryDir = Path.Join(Path.GetTempPath(), $"nzbdav-lib-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mountDir);
        Directory.CreateDirectory(libraryDir);
        await using var harness = await TempDb.CreateAsync();
        try
        {
            var ctx = harness.Context;
            await SeedRootsAsync(ctx);

            var linkedIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
            foreach (var id in linkedIds)
            {
                ctx.Items.Add(DavItem.New(
                    id,
                    DavItem.ContentFolder,
                    $"{id:N}.mkv",
                    10,
                    DavItem.ItemType.UsenetFile,
                    DavItem.ItemSubType.NzbFile,
                    null,
                    null,
                    null,
                    null));
                await File.WriteAllTextAsync(
                    Path.Join(libraryDir, $"{id:N}.strm"),
                    $"http://localhost/view/.ids/{id}.mkv");
            }

            await ctx.SaveChangesAsync();

            var config = new ConfigManager();
            config.UpdateValues(
            [
                new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = libraryDir },
                new ConfigItem { ConfigName = ConfigKeys.RcloneMountDir, ConfigValue = mountDir },
            ]);

            var websocket = new WebsocketManager();
            var task = new RemoveUnlinkedFilesTask(
                config,
                websocket,
                isDryRun: true,
                createContext: () => harness.CreateContext());

            Assert.True(await task.Execute());

            var progress = websocket.PeekLastMessage(WebsocketTopic.CleanupTaskProgress);
            Assert.NotNull(progress);
            Assert.DoesNotContain("inside the rclone mount", progress, StringComparison.Ordinal);
            Assert.StartsWith("Dry Run - Done.", progress);
            Assert.Contains("Identified 0 unlinked files", progress);
        }
        finally
        {
            await BaseTask.ResetRunningTaskForTestsAsync();
            RemoveUnlinkedFilesTask.ClearAuditPathsForTests();
            try { Directory.Delete(libraryDir, recursive: true); } catch (IOException) { /* best effort */ }
            try { Directory.Delete(mountDir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public async Task InsertLinkedIdBatchAsync_InsertsAllIds()
    {
        await using var harness = await TempDb.CreateAsync();
        var ctx = harness.Context;
        await ctx.Database.ExecuteSqlRawAsync(
            """
            DROP TABLE IF EXISTS TMP_LINKED_FILES;
            CREATE TABLE TMP_LINKED_FILES (Id TEXT NOT NULL);
            """);

        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        await RemoveUnlinkedFilesTask.InsertLinkedIdBatchAsync(ctx, ids);

        var count = await ctx.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM TMP_LINKED_FILES")
            .FirstAsync();
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task LinkedFilesLookup_UsesPrimaryKeySeek_NotFullScan()
    {
        // Regression for #408: predicate COLLATE NOCASE made the BINARY PK ineligible,
        // turning each NOT EXISTS into SCAN t. Column NOCASE + t.Id = DavItems.Id must SEEK.
        await using var harness = await TempDb.CreateAsync();
        var ctx = harness.Context;

        await ctx.Database.ExecuteSqlRawAsync(
            """
            DROP TABLE IF EXISTS TMP_LINKED_FILES;
            CREATE TABLE TMP_LINKED_FILES (Id TEXT NOT NULL COLLATE NOCASE PRIMARY KEY);
            INSERT INTO TMP_LINKED_FILES (Id) VALUES ('AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE');
            """);

        var connection = ctx.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync();
        command.CommandText =
            """
            EXPLAIN QUERY PLAN
            SELECT Id FROM DavItems
            WHERE NOT EXISTS (
                SELECT 1 FROM TMP_LINKED_FILES t
                WHERE t.Id = DavItems.Id
            )
            """;

        var details = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            var detailOrdinal = reader.GetOrdinal("detail");
            while (await reader.ReadAsync())
                details.Add(reader.GetString(detailOrdinal));
        }

        var plan = string.Join('\n', details);
        Assert.Contains("SEARCH t", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("SCAN t", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_RemovesGeneratedSidecarsWithOrphanedItem()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        var rootDir = Path.Join(Path.GetTempPath(), $"nzbdav-orphan-{Guid.NewGuid():N}");
        var libraryDir = Path.Join(rootDir, "library");
        var completedDir = Path.Join(rootDir, "completed-downloads");
        Directory.CreateDirectory(libraryDir);
        await using var harness = await TempDb.CreateAsync();
        try
        {
            var ctx = harness.Context;
            await SeedRootsAsync(ctx);
            await SeedLinkedItemsAsync(ctx, libraryDir, 5);

            var orphanId = Guid.NewGuid();
            var orphan = DavItem.New(
                orphanId,
                DavItem.ContentFolder,
                "orphan.mkv",
                10,
                DavItem.ItemType.UsenetFile,
                DavItem.ItemSubType.NzbFile,
                null,
                null,
                null,
                null);

            var strmPath = Path.Join(completedDir, "movies", "Some.Release", "orphan.mkv.strm");
            var strmTarget = $"http://localhost/view/.ids/{orphanId}.mkv";
            orphan.GeneratedStrmOutputRoot = Path.GetFullPath(completedDir);
            orphan.GeneratedStrmPath = strmPath;
            orphan.GeneratedStrmTarget = strmTarget;

            ctx.Items.Add(orphan);
            await ctx.SaveChangesAsync();

            Directory.CreateDirectory(Path.GetDirectoryName(strmPath)!);
            await File.WriteAllTextAsync(strmPath, strmTarget);

            var config = new ConfigManager();
            config.UpdateValues(
            [
                new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = libraryDir },
                new ConfigItem { ConfigName = ConfigKeys.ApiImportStrategy, ConfigValue = "strm" },
                new ConfigItem { ConfigName = ConfigKeys.ApiCompletedDownloadsDir, ConfigValue = completedDir },
            ]);

            var websocket = new WebsocketManager();
            var task = new RemoveUnlinkedFilesTask(
                config,
                websocket,
                isDryRun: false,
                createContext: () => harness.CreateContext());

            Assert.True(await task.Execute());

            var progress = websocket.PeekLastMessage(WebsocketTopic.CleanupTaskProgress);
            Assert.NotNull(progress);
            Assert.StartsWith("Done. Removed 1 unlinked files.", progress);
            Assert.False(await ctx.Items.AnyAsync(x => x.Id == orphanId));
            Assert.False(File.Exists(strmPath));

            // empty sidecar directories are pruned up to (but not including) the output root
            Assert.False(Directory.Exists(Path.Join(completedDir, "movies")));
            Assert.True(Directory.Exists(completedDir));

            var report = RemoveUnlinkedFilesTask.GetAuditReport();
            Assert.Contains($"(strm sidecar of {orphan.Path})", report);
        }
        finally
        {
            await BaseTask.ResetRunningTaskForTestsAsync();
            RemoveUnlinkedFilesTask.ClearAuditPathsForTests();
            try { Directory.Delete(rootDir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public async Task Execute_PreservesSidecarWhoseOnDiskTargetChanged()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        var rootDir = Path.Join(Path.GetTempPath(), $"nzbdav-orphan-{Guid.NewGuid():N}");
        var libraryDir = Path.Join(rootDir, "library");
        var completedDir = Path.Join(rootDir, "completed-downloads");
        Directory.CreateDirectory(libraryDir);
        await using var harness = await TempDb.CreateAsync();
        try
        {
            var ctx = harness.Context;
            await SeedRootsAsync(ctx);
            await SeedLinkedItemsAsync(ctx, libraryDir, 5);

            var orphanId = Guid.NewGuid();
            var orphan = DavItem.New(
                orphanId,
                DavItem.ContentFolder,
                "orphan.mkv",
                10,
                DavItem.ItemType.UsenetFile,
                DavItem.ItemSubType.NzbFile,
                null,
                null,
                null,
                null);

            var strmPath = Path.Join(completedDir, "movies", "Some.Release", "orphan.mkv.strm");
            orphan.GeneratedStrmOutputRoot = Path.GetFullPath(completedDir);
            orphan.GeneratedStrmPath = strmPath;
            orphan.GeneratedStrmTarget = $"http://localhost/view/.ids/{orphanId}.mkv";

            ctx.Items.Add(orphan);
            await ctx.SaveChangesAsync();

            // The file at the recorded path now belongs to something else (e.g. an Arr
            // import replaced it). Ownership no longer verified -> must be preserved.
            Directory.CreateDirectory(Path.GetDirectoryName(strmPath)!);
            await File.WriteAllTextAsync(strmPath, $"http://localhost/view/.ids/{Guid.NewGuid()}.mkv");

            var config = new ConfigManager();
            config.UpdateValues(
            [
                new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = libraryDir },
                new ConfigItem { ConfigName = ConfigKeys.ApiImportStrategy, ConfigValue = "strm" },
                new ConfigItem { ConfigName = ConfigKeys.ApiCompletedDownloadsDir, ConfigValue = completedDir },
            ]);

            var websocket = new WebsocketManager();
            var task = new RemoveUnlinkedFilesTask(
                config,
                websocket,
                isDryRun: false,
                createContext: () => harness.CreateContext());

            Assert.True(await task.Execute());

            var progress = websocket.PeekLastMessage(WebsocketTopic.CleanupTaskProgress);
            Assert.NotNull(progress);
            Assert.StartsWith("Done. Removed 1 unlinked files.", progress);
            Assert.False(await ctx.Items.AnyAsync(x => x.Id == orphanId));
            Assert.True(File.Exists(strmPath));
        }
        finally
        {
            await BaseTask.ResetRunningTaskForTestsAsync();
            RemoveUnlinkedFilesTask.ClearAuditPathsForTests();
            try { Directory.Delete(rootDir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public async Task DryRun_ReportsGeneratedSidecarsWithoutDeleting()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        var rootDir = Path.Join(Path.GetTempPath(), $"nzbdav-orphan-{Guid.NewGuid():N}");
        var libraryDir = Path.Join(rootDir, "library");
        var completedDir = Path.Join(rootDir, "completed-downloads");
        Directory.CreateDirectory(libraryDir);
        await using var harness = await TempDb.CreateAsync();
        try
        {
            var ctx = harness.Context;
            await SeedRootsAsync(ctx);
            await SeedLinkedItemsAsync(ctx, libraryDir, 5);

            var orphanId = Guid.NewGuid();
            var orphan = DavItem.New(
                orphanId,
                DavItem.ContentFolder,
                "orphan.mkv",
                10,
                DavItem.ItemType.UsenetFile,
                DavItem.ItemSubType.NzbFile,
                null,
                null,
                null,
                null);

            var strmPath = Path.Join(completedDir, "movies", "Some.Release", "orphan.mkv.strm");
            var strmTarget = $"http://localhost/view/.ids/{orphanId}.mkv";
            orphan.GeneratedStrmOutputRoot = Path.GetFullPath(completedDir);
            orphan.GeneratedStrmPath = strmPath;
            orphan.GeneratedStrmTarget = strmTarget;

            ctx.Items.Add(orphan);
            await ctx.SaveChangesAsync();

            Directory.CreateDirectory(Path.GetDirectoryName(strmPath)!);
            await File.WriteAllTextAsync(strmPath, strmTarget);

            var config = new ConfigManager();
            config.UpdateValues(
            [
                new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = libraryDir },
                new ConfigItem { ConfigName = ConfigKeys.ApiImportStrategy, ConfigValue = "strm" },
                new ConfigItem { ConfigName = ConfigKeys.ApiCompletedDownloadsDir, ConfigValue = completedDir },
            ]);

            var websocket = new WebsocketManager();
            var task = new RemoveUnlinkedFilesTask(
                config,
                websocket,
                isDryRun: true,
                createContext: () => harness.CreateContext());

            Assert.True(await task.Execute());

            var progress = websocket.PeekLastMessage(WebsocketTopic.CleanupTaskProgress);
            Assert.NotNull(progress);
            Assert.StartsWith("Dry Run - Done. Identified 1 unlinked files.", progress);
            Assert.True(await ctx.Items.AnyAsync(x => x.Id == orphanId));
            Assert.True(File.Exists(strmPath));

            var report = RemoveUnlinkedFilesTask.GetAuditReport();
            Assert.Contains(orphan.Path, report);
            Assert.Contains($"(strm sidecar of {orphan.Path})", report);
        }
        finally
        {
            await BaseTask.ResetRunningTaskForTestsAsync();
            RemoveUnlinkedFilesTask.ClearAuditPathsForTests();
            try { Directory.Delete(rootDir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public async Task DryRun_IgnoresGeneratedSidecarsInsideLibraryDir()
    {
        // A completed-downloads dir nested inside the Library Directory must not let
        // generated strm sidecars mark their own dav-items as "linked"; otherwise nothing
        // using the STRM import strategy could ever be orphaned.
        await BaseTask.ResetRunningTaskForTestsAsync();
        var rootDir = Path.Join(Path.GetTempPath(), $"nzbdav-orphan-{Guid.NewGuid():N}");
        var libraryDir = Path.Join(rootDir, "data");
        var completedDir = Path.Join(libraryDir, "completed-downloads");
        Directory.CreateDirectory(completedDir);
        await using var harness = await TempDb.CreateAsync();
        try
        {
            var ctx = harness.Context;
            await SeedRootsAsync(ctx);
            await SeedLinkedItemsAsync(ctx, completedDir, 5);

            var config = new ConfigManager();
            config.UpdateValues(
            [
                new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = libraryDir },
                new ConfigItem { ConfigName = ConfigKeys.ApiImportStrategy, ConfigValue = "strm" },
                new ConfigItem { ConfigName = ConfigKeys.ApiCompletedDownloadsDir, ConfigValue = completedDir },
            ]);

            var websocket = new WebsocketManager();
            var task = new RemoveUnlinkedFilesTask(
                config,
                websocket,
                isDryRun: true,
                createContext: () => harness.CreateContext());

            Assert.True(await task.Execute());

            var progress = websocket.PeekLastMessage(WebsocketTopic.CleanupTaskProgress);
            Assert.NotNull(progress);
            Assert.Contains("Aborted:", progress, StringComparison.Ordinal);
            Assert.Contains("less than five linked files", progress, StringComparison.Ordinal);
        }
        finally
        {
            await BaseTask.ResetRunningTaskForTestsAsync();
            RemoveUnlinkedFilesTask.ClearAuditPathsForTests();
            try { Directory.Delete(rootDir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public async Task DryRun_StillCountsLibraryLinksOutsideGeneratedOutputDirs()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        var rootDir = Path.Join(Path.GetTempPath(), $"nzbdav-orphan-{Guid.NewGuid():N}");
        var libraryDir = Path.Join(rootDir, "data");
        var completedDir = Path.Join(libraryDir, "completed-downloads");
        Directory.CreateDirectory(completedDir);
        await using var harness = await TempDb.CreateAsync();
        try
        {
            var ctx = harness.Context;
            await SeedRootsAsync(ctx);
            await SeedLinkedItemsAsync(ctx, libraryDir, 5);

            // An orphan whose only "link" is its own generated strm sidecar inside the
            // completed-downloads dir nested within the Library Directory.
            var orphanId = Guid.NewGuid();
            ctx.Items.Add(DavItem.New(
                orphanId,
                DavItem.ContentFolder,
                $"{orphanId:N}.mkv",
                10,
                DavItem.ItemType.UsenetFile,
                DavItem.ItemSubType.NzbFile,
                null,
                null,
                null,
                null));
            await ctx.SaveChangesAsync();
            await File.WriteAllTextAsync(
                Path.Join(completedDir, $"{orphanId:N}.mkv.strm"),
                $"http://localhost/view/.ids/{orphanId}.mkv");

            var config = new ConfigManager();
            config.UpdateValues(
            [
                new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = libraryDir },
                new ConfigItem { ConfigName = ConfigKeys.ApiImportStrategy, ConfigValue = "strm" },
                new ConfigItem { ConfigName = ConfigKeys.ApiCompletedDownloadsDir, ConfigValue = completedDir },
            ]);

            var websocket = new WebsocketManager();
            var task = new RemoveUnlinkedFilesTask(
                config,
                websocket,
                isDryRun: true,
                createContext: () => harness.CreateContext());

            Assert.True(await task.Execute());

            var progress = websocket.PeekLastMessage(WebsocketTopic.CleanupTaskProgress);
            Assert.NotNull(progress);
            Assert.StartsWith("Dry Run - Done.", progress);
            Assert.Contains("Identified 1 unlinked files", progress);
        }
        finally
        {
            await BaseTask.ResetRunningTaskForTestsAsync();
            RemoveUnlinkedFilesTask.ClearAuditPathsForTests();
            try { Directory.Delete(rootDir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public async Task DryRun_CountsLibraryLinksUnderCompletedDownloadsDirInSymlinkMode()
    {
        // In symlink mode, completed-downloads-dir is not InfiniDysk's generated output.
        // Arr-imported links that happen to sit under that path must still count.
        await BaseTask.ResetRunningTaskForTestsAsync();
        var rootDir = Path.Join(Path.GetTempPath(), $"nzbdav-orphan-{Guid.NewGuid():N}");
        var libraryDir = Path.Join(rootDir, "data");
        var completedDir = Path.Join(libraryDir, "completed-downloads");
        Directory.CreateDirectory(completedDir);
        await using var harness = await TempDb.CreateAsync();
        try
        {
            var ctx = harness.Context;
            await SeedRootsAsync(ctx);
            await SeedLinkedItemsAsync(ctx, completedDir, 5);

            var config = new ConfigManager();
            config.UpdateValues(
            [
                new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = libraryDir },
                new ConfigItem { ConfigName = ConfigKeys.ApiImportStrategy, ConfigValue = "symlinks" },
                new ConfigItem { ConfigName = ConfigKeys.ApiCompletedDownloadsDir, ConfigValue = completedDir },
            ]);

            var websocket = new WebsocketManager();
            var task = new RemoveUnlinkedFilesTask(
                config,
                websocket,
                isDryRun: true,
                createContext: () => harness.CreateContext());

            Assert.True(await task.Execute());

            var progress = websocket.PeekLastMessage(WebsocketTopic.CleanupTaskProgress);
            Assert.NotNull(progress);
            Assert.DoesNotContain("Aborted:", progress, StringComparison.Ordinal);
            Assert.StartsWith("Dry Run - Done.", progress);
        }
        finally
        {
            await BaseTask.ResetRunningTaskForTestsAsync();
            RemoveUnlinkedFilesTask.ClearAuditPathsForTests();
            try { Directory.Delete(rootDir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    private static async Task SeedLinkedItemsAsync(DavDatabaseContext ctx, string libraryDir, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            ctx.Items.Add(DavItem.New(
                id,
                DavItem.ContentFolder,
                $"{id:N}.mkv",
                10,
                DavItem.ItemType.UsenetFile,
                DavItem.ItemSubType.NzbFile,
                null,
                null,
                null,
                null));
            await File.WriteAllTextAsync(
                Path.Join(libraryDir, $"{id:N}.strm"),
                $"http://localhost/view/.ids/{id}.mkv");
        }

        await ctx.SaveChangesAsync();
    }

    private static DavItem NewDir(Guid id, DavItem parent, string name) =>
        DavItem.New(id, parent, name, null, DavItem.ItemType.Directory, DavItem.ItemSubType.Directory,
            null, null, null, null);

    private static void AssertMessagesAppearInOrder(
        IEnumerable<string> messages,
        params string[] expectedFragments)
    {
        var allMessages = messages.ToList();
        var searchFrom = 0;
        foreach (var expected in expectedFragments)
        {
            var index = allMessages.FindIndex(
                searchFrom,
                message => message.Contains(expected, StringComparison.Ordinal));
            Assert.True(
                index >= 0,
                $"Expected progress containing '{expected}' after index {searchFrom - 1}. " +
                $"Messages: {string.Join(" | ", allMessages)}");
            searchFrom = index + 1;
        }
    }

    private static async Task SeedRootsAsync(DavDatabaseContext ctx)
    {
        // Migrations already insert roots; ensure local references match persisted rows.
        if (!await ctx.Items.AnyAsync(x => x.Id == DavItem.Root.Id))
            ctx.Items.Add(DavItem.Root);
        if (!await ctx.Items.AnyAsync(x => x.Id == DavItem.ContentFolder.Id))
            ctx.Items.Add(DavItem.ContentFolder);
        await ctx.SaveChangesAsync();
    }

    private sealed class BlockingTask(Task gate) : BaseTask
    {
        protected override Task ExecuteInternal() => gate;
    }

    private sealed class NoOpTask : BaseTask
    {
        protected override Task ExecuteInternal() => Task.CompletedTask;
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
            var path = Path.Join(Path.GetTempPath(), $"nzbdav-unlinked-{Guid.NewGuid():N}.sqlite");
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

[CollectionDefinition(nameof(BaseTaskCollection))]
public class BaseTaskCollection : ICollectionFixture<object>;
