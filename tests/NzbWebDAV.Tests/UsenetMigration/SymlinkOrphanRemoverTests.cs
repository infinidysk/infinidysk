using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database.Models.UsenetMigration;
using NzbWebDAV.UsenetMigration.Symlinks;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class SymlinkOrphanRemoverTests
{
    private sealed class FakeSymlinkOps : ISymlinkOps
    {
        public readonly Dictionary<string, string> Links = new(StringComparer.Ordinal);
        public int DeleteCalls { get; private set; }
        public Action<int>? AfterDelete { get; init; }

        public string? ReadLink(string libraryRoot, string path) => Links.GetValueOrDefault(path);

        public void ReplaceSymlink(string libraryRoot, string path, string expectedOldTarget, string newTarget) =>
            throw new NotSupportedException();

        public void DeleteSymlink(string libraryRoot, string path, string expectedTarget)
        {
            if (!Links.TryGetValue(path, out var current)
                || !string.Equals(current, expectedTarget, StringComparison.Ordinal))
            {
                throw new IOException("Target changed during removal.");
            }

            Links.Remove(path);
            DeleteCalls++;
            AfterDelete?.Invoke(DeleteCalls);
        }

        public void CreateSymlink(string libraryRoot, string path, string target) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Remove_RejectsUnexpectedSessionStatus()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        await h.Store.UpdateSessionAsync(s => s.Status = "linked");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SymlinkOrphanRemover(h.Store).RemoveAsync());

        Assert.Contains("active orphan-removal operation", error.Message);
    }

    [Fact]
    public async Task Remove_BackupsAndDeletesOnlyCurrentOrphanRows()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        var library = Directory.CreateTempSubdirectory("altmig-orphan-library-");
        var backups = Directory.CreateTempSubdirectory("altmig-orphan-backups-");
        var orphan = Path.Combine(library.FullName, "orphan.mkv");
        var rewrite = Path.Combine(library.FullName, "rewrite.mkv");
        const string orphanTarget = "/mnt/altmount/orphan.mkv";
        const string rewriteTarget = "/mnt/altmount/rewrite.mkv";
        var ops = new FakeSymlinkOps
        {
            Links =
            {
                [orphan] = orphanTarget,
                [rewrite] = rewriteTarget,
            },
        };

        try
        {
            await ConfigureSessionAsync(h, library.FullName, backups.FullName);
            await SeedPlanAsync(h,
                Row(orphan, orphanTarget, "orphan"),
                Row(rewrite, rewriteTarget, "rewrite"),
                Row(Path.Combine(library.FullName, "unreadable.mkv"), "", "unreadable"));

            var remover = new SymlinkOrphanRemover(h.Store)
            {
                Ops = ops,
                UtcNow = () => new DateTime(2026, 8, 2, 12, 34, 56, DateTimeKind.Utc),
            };
            var result = await remover.RemoveAsync();

            Assert.Equal(1, result.Removed);
            Assert.Equal(0, result.Failed);
            Assert.Equal(1, ops.DeleteCalls);
            Assert.DoesNotContain(orphan, ops.Links.Keys);
            Assert.Equal(rewriteTarget, ops.Links[rewrite]);
            Assert.Equal(
                Path.Combine(backups.FullName, "altmount-orphan-symlink-backup-20260802-123456.tar.gz"),
                result.BackupPath);

            var entries = await SymlinkBackup.ReadAsync(result.BackupPath!);
            var entry = Assert.Single(entries);
            Assert.Equal(orphan, entry.Path);
            Assert.Equal(orphanTarget, entry.Target);
            Assert.Equal(SymlinkBackup.OrphanRemovalOperation, entry.Operation);

            await using var verify = h.Mig();
            Assert.Equal("removed", (await verify.SymlinkRewrites.SingleAsync(r => r.SymlinkPath == orphan)).Status);
            Assert.Equal("rewrite", (await verify.SymlinkRewrites.SingleAsync(r => r.SymlinkPath == rewrite)).Status);
        }
        finally
        {
            library.Delete(recursive: true);
            backups.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Remove_BackupFailureAbortsBeforeAnyDeletion()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        var library = Directory.CreateTempSubdirectory("altmig-orphan-library-");
        var backupPathThatIsAFile = Path.GetTempFileName();
        var orphan = Path.Combine(library.FullName, "orphan.mkv");
        const string target = "/mnt/altmount/orphan.mkv";
        var ops = new FakeSymlinkOps { Links = { [orphan] = target } };

        try
        {
            await ConfigureSessionAsync(h, library.FullName, backupPathThatIsAFile);
            await SeedPlanAsync(h, Row(orphan, target, "orphan"));

            var remover = new SymlinkOrphanRemover(h.Store) { Ops = ops };
            await Assert.ThrowsAnyAsync<IOException>(() => remover.RemoveAsync());

            Assert.Equal(0, ops.DeleteCalls);
            Assert.Equal(target, ops.Links[orphan]);
            await using var verify = h.Mig();
            Assert.Equal("orphan", (await verify.SymlinkRewrites.SingleAsync()).Status);
        }
        finally
        {
            library.Delete(recursive: true);
            File.Delete(backupPathThatIsAFile);
        }
    }

    [Fact]
    public async Task Remove_DriftedLinkIsLeftUntouchedAndRetryable()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        var library = Directory.CreateTempSubdirectory("altmig-orphan-library-");
        var backups = Directory.CreateTempSubdirectory("altmig-orphan-backups-");
        var orphan = Path.Combine(library.FullName, "orphan.mkv");
        const string planned = "/mnt/altmount/original.mkv";
        const string current = "/mnt/elsewhere/replaced.mkv";
        var ops = new FakeSymlinkOps { Links = { [orphan] = current } };

        try
        {
            await ConfigureSessionAsync(h, library.FullName, backups.FullName);
            await SeedPlanAsync(h, Row(orphan, planned, "orphan"));

            var result = await new SymlinkOrphanRemover(h.Store) { Ops = ops }.RemoveAsync();

            Assert.Equal(0, result.Removed);
            Assert.Equal(1, result.Failed);
            Assert.Null(result.BackupPath);
            Assert.Equal(current, ops.Links[orphan]);
            await using var verify = h.Mig();
            var row = await verify.SymlinkRewrites.SingleAsync();
            Assert.Equal("orphan", row.Status);
            Assert.Contains("changed since planning", row.Error);
        }
        finally
        {
            library.Delete(recursive: true);
            backups.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Remove_CancellationPersistsCompletedDeletesAndBacksUpWholeBatch()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        var library = Directory.CreateTempSubdirectory("altmig-orphan-library-");
        var backups = Directory.CreateTempSubdirectory("altmig-orphan-backups-");
        var first = Path.Combine(library.FullName, "a.mkv");
        var second = Path.Combine(library.FullName, "b.mkv");
        using var cancel = new CancellationTokenSource();
        var ops = new FakeSymlinkOps
        {
            Links =
            {
                [first] = "/mnt/altmount/a.mkv",
                [second] = "/mnt/altmount/b.mkv",
            },
            AfterDelete = count =>
            {
                if (count == 1) cancel.Cancel();
            },
        };

        try
        {
            await ConfigureSessionAsync(h, library.FullName, backups.FullName);
            await SeedPlanAsync(h,
                Row(first, "/mnt/altmount/a.mkv", "orphan"),
                Row(second, "/mnt/altmount/b.mkv", "orphan"));

            var remover = new SymlinkOrphanRemover(h.Store)
            {
                Ops = ops,
                UtcNow = () => new DateTime(2026, 8, 2, 13, 0, 0, DateTimeKind.Utc),
            };
            await Assert.ThrowsAsync<OperationCanceledException>(() => remover.RemoveAsync(cancel.Token));

            Assert.Equal(1, ops.DeleteCalls);
            Assert.False(ops.Links.ContainsKey(first));
            Assert.True(ops.Links.ContainsKey(second));
            var archive = Path.Combine(
                backups.FullName, "altmount-orphan-symlink-backup-20260802-130000.tar.gz");
            Assert.Equal(2, (await SymlinkBackup.ReadAsync(archive)).Count);

            await using var verify = h.Mig();
            Assert.Equal("removed", (await verify.SymlinkRewrites.SingleAsync(r => r.SymlinkPath == first)).Status);
            Assert.Equal("orphan", (await verify.SymlinkRewrites.SingleAsync(r => r.SymlinkPath == second)).Status);
        }
        finally
        {
            library.Delete(recursive: true);
            backups.Delete(recursive: true);
        }
    }

    [SkippableFact]
    public void RealOps_DeleteRemovesOnlyTheSymlinkAndPreservesItsTarget()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating symlinks may require Windows developer mode.");
        var root = Directory.CreateTempSubdirectory("altmig-real-delete-");
        var target = Path.Combine(root.FullName, "target.mkv");
        var link = Path.Combine(root.FullName, "link.mkv");
        File.WriteAllText(target, "precious content");
        File.CreateSymbolicLink(link, target);

        try
        {
            RealSymlinkOps.Instance.DeleteSymlink(root.FullName, link, target);

            Assert.False(File.Exists(link));
            Assert.Equal("precious content", File.ReadAllText(target));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void RealOps_DeleteRefusesRealFile()
    {
        var root = Directory.CreateTempSubdirectory("altmig-real-delete-");
        var path = Path.Combine(root.FullName, "real.mkv");
        File.WriteAllText(path, "precious content");

        try
        {
            var error = Assert.Throws<IOException>(() =>
                RealSymlinkOps.Instance.DeleteSymlink(root.FullName, path, "/mnt/altmount/real.mkv"));
            Assert.Contains("non-symlink", error.Message);
            Assert.Equal("precious content", File.ReadAllText(path));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [SkippableFact]
    public void RealOps_DeleteLeafSwapLeavesReplacementFileUntouched()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating symlinks may require Windows developer mode.");
        var root = Directory.CreateTempSubdirectory("altmig-delete-race-");
        var link = Path.Combine(root.FullName, "movie.mkv");
        const string target = "/mnt/altmount/movie.mkv";
        File.CreateSymbolicLink(link, target);

        try
        {
            var ops = new RealSymlinkOps
            {
                BeforeFinalLeafValidation = path =>
                {
                    File.Delete(path);
                    File.WriteAllText(path, "replacement content");
                },
            };

            var error = Assert.Throws<IOException>(() =>
                ops.DeleteSymlink(root.FullName, link, target));

            Assert.Contains("no longer the expected symlink", error.Message);
            Assert.Equal("replacement content", File.ReadAllText(link));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void RealOps_DeleteRejectsPathOutsideLibraryRoot()
    {
        var root = Directory.CreateTempSubdirectory("altmig-delete-root-");
        var outside = Path.Combine(Path.GetTempPath(), $"altmig-outside-{Guid.NewGuid():N}.mkv");
        try
        {
            var error = Assert.Throws<IOException>(() =>
                RealSymlinkOps.Instance.DeleteSymlink(root.FullName, outside, "/mnt/altmount/movie.mkv"));
            Assert.Contains("outside the configured Library Root", error.Message);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task ConfigureSessionAsync(
        MigrationTestHarness h,
        string libraryRoot,
        string backupDir) =>
        await h.Store.UpdateSessionAsync(s =>
        {
            s.Status = "removing_orphans";
            s.SymlinkLibraryRoot = libraryRoot;
            s.SymlinkBackupDir = backupDir;
        });

    private static async Task SeedPlanAsync(MigrationTestHarness h, params MigrationSymlinkRewrite[] rows)
    {
        await using var ctx = h.Mig();
        ctx.SymlinkRewrites.AddRange(rows);
        await ctx.SaveChangesAsync();
    }

    private static MigrationSymlinkRewrite Row(string path, string target, string status) => new()
    {
        SymlinkPath = path,
        OldTarget = target,
        Status = status,
        UpdatedAt = DateTime.UtcNow,
    };
}
