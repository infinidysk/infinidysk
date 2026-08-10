using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database.Models.UsenetMigration;
using NzbWebDAV.UsenetMigration.Symlinks;

namespace NzbWebDAV.Tests.UsenetMigration;

public class SymlinkRestoreServiceTests
{
    private sealed class FakeSymlinkOps : ISymlinkOps
    {
        public readonly Dictionary<string, string> Links = new(StringComparer.Ordinal);

        public string? ReadLink(string libraryRoot, string path) => Links.GetValueOrDefault(path);

        public void ReplaceSymlink(string libraryRoot, string path, string expectedOldTarget, string newTarget)
        {
            if (!Links.TryGetValue(path, out var current)
                || !string.Equals(current, expectedOldTarget, StringComparison.Ordinal))
                throw new IOException($"Refusing to replace '{path}' because its symlink target changed during replacement.");
            Links[path] = newTarget;
        }

        public void DeleteSymlink(string libraryRoot, string path, string expectedTarget)
        {
            if (!Links.TryGetValue(path, out var current)
                || !string.Equals(current, expectedTarget, StringComparison.Ordinal))
                throw new IOException($"Refusing to delete '{path}' because its symlink target changed during removal.");
            Links.Remove(path);
        }

        public void CreateSymlink(string libraryRoot, string path, string target)
        {
            if (Links.ContainsKey(path))
                throw new IOException($"Refusing to create symlink over existing path at '{path}'.");
            Links[path] = target;
        }
    }

    [Fact]
    public async Task Restore_LeavesDriftedAndOutOfRootLinksUntouched()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        var root = Path.Join(Path.GetTempPath(), $"altmig-library-{Guid.NewGuid():N}");
        var backupDir = Path.Join(Path.GetTempPath(), $"altmig-backups-{Guid.NewGuid():N}");
        var inRoot = Path.Join(root, "movie.mkv");
        var outsideRoot = Path.Join(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.mkv");
        var archiveName = "altmount-symlink-backup-20260720-120000.tar.gz";
        var archivePath = Path.Join(backupDir, archiveName);
        await h.Store.UpdateSessionAsync(s =>
        {
            s.Status = "linked";
            s.SymlinkLibraryRoot = root;
            s.SymlinkBackupDir = backupDir;
        });
        await SymlinkBackup.WriteAsync(archivePath,
        [
            new(inRoot, "/alt/original.mkv", "/nzbdav/expected.mkv"),
            new(outsideRoot, "/alt/outside.mkv", "/nzbdav/outside.mkv"),
        ]);
        var ops = new FakeSymlinkOps
        {
            Links =
            {
                [inRoot] = "/nzbdav/changed-after-rewrite.mkv",
                [outsideRoot] = "/nzbdav/outside.mkv",
            },
        };

        var result = await new SymlinkRestoreService(h.Store) { Ops = ops }.RestoreAsync(archiveName);

        Assert.Equal(0, result.Restored);
        Assert.Equal(2, result.Failed);
        Assert.Equal("/nzbdav/changed-after-rewrite.mkv", ops.Links[inRoot]);
        Assert.Equal("/nzbdav/outside.mkv", ops.Links[outsideRoot]);
        Assert.Contains(result.Issues, i => i.Path == inRoot && i.Reason.Contains("changed after rewriting"));
        Assert.Contains(result.Issues, i => i.Path == outsideRoot && i.Reason.Contains("outside"));

        Directory.Delete(backupDir, recursive: true);
    }

    [Fact]
    public async Task Restore_LegacyArchiveUsesCurrentPlanForDriftGuard()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        var root = Path.Join(Path.GetTempPath(), $"altmig-library-{Guid.NewGuid():N}");
        var backupDir = Path.Join(Path.GetTempPath(), $"altmig-backups-{Guid.NewGuid():N}");
        var link = Path.Join(root, "episode.mkv");
        var archiveName = "altmount-symlink-backup-20260720-120001.tar.gz";
        await h.Store.UpdateSessionAsync(s =>
        {
            s.Status = "linked";
            s.SymlinkLibraryRoot = root;
            s.SymlinkBackupDir = backupDir;
        });
        await using (var ctx = h.Mig())
        {
            ctx.SymlinkRewrites.Add(new MigrationSymlinkRewrite
            {
                SymlinkPath = link,
                OldTarget = "/alt/original.mkv",
                NewTarget = "/nzbdav/replacement.mkv",
                Status = "applied",
                UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }
        await SymlinkBackup.WriteAsync(
            Path.Join(backupDir, archiveName),
            [new SymlinkBackup.Entry(link, "/alt/original.mkv")]);
        var ops = new FakeSymlinkOps { Links = { [link] = "/nzbdav/replacement.mkv" } };

        var result = await new SymlinkRestoreService(h.Store) { Ops = ops }.RestoreAsync(archiveName);

        Assert.Equal(1, result.Restored);
        Assert.Equal(1, result.Requeued);
        Assert.Equal("/alt/original.mkv", ops.Links[link]);
        await using var verify = h.Mig();
        Assert.Equal("rewrite", (await verify.SymlinkRewrites.SingleAsync()).Status);

        Directory.Delete(backupDir, recursive: true);
    }

    [Fact]
    public async Task Restore_CurrentArchiveRecreatesMissingRewritePlanRow()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        var root = Path.Join(Path.GetTempPath(), $"altmig-library-{Guid.NewGuid():N}");
        var backupDir = Path.Join(Path.GetTempPath(), $"altmig-backups-{Guid.NewGuid():N}");
        var link = Path.Join(root, "movie.mkv");
        var archiveName = "altmount-symlink-backup-20260720-120002.tar.gz";
        await h.Store.UpdateSessionAsync(s =>
        {
            s.Status = "linked";
            s.SymlinkLibraryRoot = root;
            s.SymlinkBackupDir = backupDir;
        });
        await SymlinkBackup.WriteAsync(
            Path.Join(backupDir, archiveName),
            [new SymlinkBackup.Entry(link, "/alt/original.mkv", "/nzbdav/replacement.mkv")]);
        var ops = new FakeSymlinkOps { Links = { [link] = "/nzbdav/replacement.mkv" } };

        var result = await new SymlinkRestoreService(h.Store) { Ops = ops }.RestoreAsync(archiveName);

        Assert.Equal(1, result.Restored);
        Assert.Equal(1, result.Requeued);
        await using var verify = h.Mig();
        var row = await verify.SymlinkRewrites.SingleAsync();
        Assert.Equal(link, row.SymlinkPath);
        Assert.Equal("/alt/original.mkv", row.OldTarget);
        Assert.Equal("/nzbdav/replacement.mkv", row.NewTarget);
        Assert.Equal("rewrite", row.Status);

        Directory.Delete(backupDir, recursive: true);
    }

    [Fact]
    public async Task Restore_RecreatesEntirelyMissingSymlink()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        var root = Path.Join(Path.GetTempPath(), $"altmig-library-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var backupDir = Path.Join(Path.GetTempPath(), $"altmig-backups-{Guid.NewGuid():N}");
        var link = Path.Join(root, "movie.mkv");
        var archiveName = "altmount-symlink-backup-20260720-120003.tar.gz";
        await h.Store.UpdateSessionAsync(s =>
        {
            s.Status = "linked";
            s.SymlinkLibraryRoot = root;
            s.SymlinkBackupDir = backupDir;
        });
        await SymlinkBackup.WriteAsync(
            Path.Join(backupDir, archiveName),
            [new SymlinkBackup.Entry(link, "/alt/original.mkv", "/nzbdav/replacement.mkv")]);
        var ops = new FakeSymlinkOps(); // link absent

        var result = await new SymlinkRestoreService(h.Store) { Ops = ops }.RestoreAsync(archiveName);

        Assert.Equal(1, result.Restored);
        Assert.Equal("/alt/original.mkv", ops.Links[link]);

        Directory.Delete(backupDir, recursive: true);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Restore_RefusesRealFileAtPath()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        var root = Path.Join(Path.GetTempPath(), $"altmig-library-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var backupDir = Path.Join(Path.GetTempPath(), $"altmig-backups-{Guid.NewGuid():N}");
        var link = Path.Join(root, "movie.mkv");
        File.WriteAllText(link, "precious");
        var archiveName = "altmount-symlink-backup-20260720-120004.tar.gz";
        await h.Store.UpdateSessionAsync(s =>
        {
            s.Status = "linked";
            s.SymlinkLibraryRoot = root;
            s.SymlinkBackupDir = backupDir;
        });
        await SymlinkBackup.WriteAsync(
            Path.Join(backupDir, archiveName),
            [new SymlinkBackup.Entry(link, "/alt/original.mkv", "/nzbdav/replacement.mkv")]);
        var ops = new FakeSymlinkOps();

        var result = await new SymlinkRestoreService(h.Store) { Ops = ops }.RestoreAsync(archiveName);

        Assert.Equal(0, result.Restored);
        Assert.Equal(1, result.Failed);
        Assert.Contains(result.Issues, i => i.Reason.Contains("real file"));
        Assert.Equal("precious", File.ReadAllText(link));
        Assert.False(ops.Links.ContainsKey(link));

        Directory.Delete(backupDir, recursive: true);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Restore_OrphanRemovalArchiveRecreatesMissingLinkAndRestoresPlanStatus()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        var root = Directory.CreateTempSubdirectory("altmig-library-");
        var backupDir = Directory.CreateTempSubdirectory("altmig-backups-");
        var link = Path.Join(root.FullName, "orphan.mkv");
        const string target = "/mnt/altmount/orphan.mkv";
        var archiveName = "altmount-orphan-symlink-backup-20260720-120005.tar.gz";
        try
        {
            await h.Store.UpdateSessionAsync(s =>
            {
                s.Status = "linked";
                s.SymlinkLibraryRoot = root.FullName;
                s.SymlinkBackupDir = backupDir.FullName;
            });
            await using (var migration = h.Mig())
            {
                migration.SymlinkRewrites.Add(new MigrationSymlinkRewrite
                {
                    SymlinkPath = link,
                    OldTarget = target,
                    Status = "removed",
                    UpdatedAt = DateTime.UtcNow,
                });
                await migration.SaveChangesAsync();
            }
            await SymlinkBackup.WriteAsync(
                Path.Join(backupDir.FullName, archiveName),
                [new SymlinkBackup.Entry(
                    link,
                    target,
                    Operation: SymlinkBackup.OrphanRemovalOperation)]);
            var ops = new FakeSymlinkOps();

            var result = await new SymlinkRestoreService(h.Store) { Ops = ops }
                .RestoreAsync(archiveName);

            Assert.Equal(1, result.Restored);
            Assert.Equal(1, result.OrphansRestored);
            Assert.Equal(0, result.Requeued);
            Assert.Equal(target, ops.Links[link]);
            await using var verify = h.Mig();
            var row = await verify.SymlinkRewrites.SingleAsync();
            Assert.Equal("orphan", row.Status);
            Assert.Null(row.NewTarget);
        }
        finally
        {
            backupDir.Delete(recursive: true);
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task List_LabelsRewriteAndOrphanRemovalArchives()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        var backupDir = Directory.CreateTempSubdirectory("altmig-backups-");
        try
        {
            await h.Store.UpdateSessionAsync(s => s.SymlinkBackupDir = backupDir.FullName);
            await SymlinkBackup.WriteAsync(
                Path.Join(backupDir.FullName, "altmount-symlink-backup-20260720-120006.tar.gz"),
                [new SymlinkBackup.Entry("/lib/rewrite.mkv", "/alt/rewrite.mkv", "/nzbdav/rewrite.mkv")]);
            await SymlinkBackup.WriteAsync(
                Path.Join(backupDir.FullName, "altmount-orphan-symlink-backup-20260720-120007.tar.gz"),
                [new SymlinkBackup.Entry(
                    "/lib/orphan.mkv",
                    "/alt/orphan.mkv",
                    Operation: SymlinkBackup.OrphanRemovalOperation)]);

            var backups = await new SymlinkRestoreService(h.Store).ListAsync();

            Assert.Equal("rewrite", backups.Single(b => b.FileName.Contains("120006")).Kind);
            var orphan = backups.Single(b => b.FileName.Contains("120007"));
            Assert.Equal("orphan-removal", orphan.Kind);
            Assert.Equal(0, orphan.LegacyEntryCount);
        }
        finally
        {
            backupDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task List_InvalidArchiveErrorNamesTheArchive()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        var backupDir = Directory.CreateTempSubdirectory("altmig-backups-");
        const string archiveName = "altmount-symlink-backup-20260720-120009.tar.gz";
        try
        {
            await h.Store.UpdateSessionAsync(s => s.SymlinkBackupDir = backupDir.FullName);
            await SymlinkBackup.WriteAsync(
                Path.Join(backupDir.FullName, archiveName),
                [new SymlinkBackup.Entry(
                    "/lib/orphan.mkv",
                    "/alt/orphan.mkv",
                    Operation: SymlinkBackup.OrphanRemovalOperation)]);

            var backup = Assert.Single(await new SymlinkRestoreService(h.Store).ListAsync());

            Assert.False(backup.IsValid);
            Assert.Contains(archiveName, backup.Error);
        }
        finally
        {
            backupDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveArchivePath_AcceptsOrphanRemovalArchive()
    {
        const string name = "altmount-orphan-symlink-backup-20260720-120008.tar.gz";
        Assert.Equal(
            Path.GetFullPath(Path.Join(Path.GetTempPath(), name)),
            SymlinkRestoreService.ResolveArchivePath(Path.GetTempPath(), name));
    }

    [Theory]
    [InlineData("../altmount-symlink-backup-20260720.tar.gz")]
    [InlineData("../altmount-orphan-symlink-backup-20260720.tar.gz")]
    [InlineData("other.tar.gz")]
    [InlineData("")]
    public void ResolveArchivePath_RejectsUntrustedNames(string fileName)
    {
        Assert.Throws<InvalidDataException>(() =>
            SymlinkRestoreService.ResolveArchivePath(Path.GetTempPath(), fileName));
    }
}
