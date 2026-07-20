using Microsoft.EntityFrameworkCore;
using NzbWebDAV.UsenetMigration.Symlinks;
using NzbWebDAV.Database.Models.UsenetMigration;

namespace NzbWebDAV.Tests.UsenetMigration;

/// <summary>
/// Verifies the symlink apply safety model: backup first, retarget only, never
/// delete, orphans untouched, drift-guarded, idempotent, and the backup restores.
/// </summary>
public class SymlinkRewriterTests
{
    /// <summary>In-memory symlink table; models a filesystem of symlinks only.</summary>
    private sealed class FakeSymlinkOps : ISymlinkOps
    {
        public readonly Dictionary<string, string> Links = new(StringComparer.Ordinal);

        public string? ReadLink(string path) => Links.GetValueOrDefault(path);
        public void CreateOrReplaceSymlink(string path, string target) => Links[path] = target;
    }

    private static async Task SeedPlanAsync(MigrationTestHarness h, params MigrationSymlinkRewrite[] rows)
    {
        await using var mig = h.Mig();
        mig.SymlinkRewrites.AddRange(rows);
        await mig.SaveChangesAsync();
    }

    private static MigrationSymlinkRewrite Row(string status, string path, string oldTarget, string? newTarget) =>
        new() { Status = status, SymlinkPath = path, OldTarget = oldTarget, NewTarget = newTarget, UpdatedAt = DateTime.UtcNow };

    [Fact]
    public async Task Apply_RetargetsRewrites_LeavesOrphansAndOthersUntouched_AndBacksUpFirst()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        var backupDir = Path.Combine(Path.GetTempPath(), $"altmig-backup-{Guid.NewGuid():N}");
        await h.Store.UpdateSessionAsync(s =>
        {
            s.SymlinkBackupDir = backupDir;
            s.SymlinkLibraryRoot = "/lib";
            s.Status = "linked";
        });

        await SeedPlanAsync(h,
            Row("rewrite", "/lib/a.mkv", "/mnt/altmount/tv/a.mkv", "/mnt/nzbdav/.ids/1/2/3/4/5/aaa"),
            Row("orphan", "/lib/b.mkv", "/mnt/altmount/tv/b.mkv", null),
            Row("not-altmount", "/lib/c.mkv", "/mnt/other/c.mkv", null));

        var ops = new FakeSymlinkOps
        {
            Links =
            {
                ["/lib/a.mkv"] = "/mnt/altmount/tv/a.mkv",
                ["/lib/b.mkv"] = "/mnt/altmount/tv/b.mkv",
                ["/lib/c.mkv"] = "/mnt/other/c.mkv",
            },
        };

        var rewriter = new SymlinkRewriter(h.Store) { Ops = ops };
        var summary = await rewriter.ApplyAsync();

        Assert.Equal(1, summary.Applied);
        Assert.Equal(0, summary.Failed);

        // Rewrite retargeted; orphan + not-altmount untouched.
        Assert.Equal("/mnt/nzbdav/.ids/1/2/3/4/5/aaa", ops.Links["/lib/a.mkv"]);
        Assert.Equal("/mnt/altmount/tv/b.mkv", ops.Links["/lib/b.mkv"]);
        Assert.Equal("/mnt/other/c.mkv", ops.Links["/lib/c.mkv"]);

        // Backup written BEFORE the change, capturing the prior target.
        Assert.NotNull(summary.BackupPath);
        Assert.True(File.Exists(summary.BackupPath));
        var backup = await SymlinkBackup.ReadAsync(summary.BackupPath!);
        var entry = Assert.Single(backup);
        Assert.Equal("/lib/a.mkv", entry.Path);
        Assert.Equal("/mnt/altmount/tv/a.mkv", entry.Target);
        Assert.Equal("/mnt/nzbdav/.ids/1/2/3/4/5/aaa", entry.ReplacementTarget);

        // Row status persisted.
        await using var mig = h.Mig();
        var row = await mig.SymlinkRewrites.SingleAsync(r => r.SymlinkPath == "/lib/a.mkv");
        Assert.Equal("applied", row.Status);

        // Restore returns the library to its pre-rewrite state.
        var restore = await new SymlinkRestoreService(h.Store) { Ops = ops }
            .RestoreAsync(Path.GetFileName(summary.BackupPath!));
        Assert.Equal(1, restore.Restored);
        Assert.Equal(0, restore.Failed);
        Assert.Equal("/mnt/altmount/tv/a.mkv", ops.Links["/lib/a.mkv"]);

        await mig.Entry(row).ReloadAsync();
        Assert.Equal("rewrite", row.Status);

        try { Directory.Delete(backupDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Apply_IsIdempotent_OnReRun()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        await h.Store.UpdateSessionAsync(s => s.SymlinkBackupDir = Path.Combine(Path.GetTempPath(), $"altmig-{Guid.NewGuid():N}"));
        await SeedPlanAsync(h, Row("rewrite", "/lib/a.mkv", "/mnt/altmount/tv/a.mkv", "/mnt/nzbdav/.ids/1/2/3/4/5/aaa"));

        var ops = new FakeSymlinkOps { Links = { ["/lib/a.mkv"] = "/mnt/altmount/tv/a.mkv" } };
        var rewriter = new SymlinkRewriter(h.Store) { Ops = ops };

        var first = await rewriter.ApplyAsync();
        // Second run: the plan row was set to "applied", so nothing is reloaded — but
        // even a rewrite row already at NewTarget is a no-op success (drift-safe).
        await using (var mig = h.Mig())
        {
            var row = await mig.SymlinkRewrites.SingleAsync();
            row.Status = "rewrite"; // force reconsideration
            await mig.SaveChangesAsync();
        }
        var second = await rewriter.ApplyAsync();

        Assert.Equal(1, first.Applied);
        Assert.Equal(1, second.Applied);
        Assert.Equal(0, second.Failed);
        Assert.Equal("/mnt/nzbdav/.ids/1/2/3/4/5/aaa", ops.Links["/lib/a.mkv"]);
    }

    [Fact]
    public async Task Apply_DriftGuard_FailsWhenTargetChangedSincePlan()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        await h.Store.UpdateSessionAsync(s => s.SymlinkBackupDir = Path.Combine(Path.GetTempPath(), $"altmig-{Guid.NewGuid():N}"));
        await SeedPlanAsync(h, Row("rewrite", "/lib/a.mkv", "/mnt/altmount/tv/a.mkv", "/mnt/nzbdav/.ids/1/2/3/4/5/aaa"));

        // On-disk target no longer matches the plan's OldTarget.
        var ops = new FakeSymlinkOps { Links = { ["/lib/a.mkv"] = "/somewhere/else.mkv" } };
        var summary = await new SymlinkRewriter(h.Store) { Ops = ops }.ApplyAsync();

        Assert.Equal(0, summary.Applied);
        Assert.Equal(1, summary.Failed);
        Assert.Equal("/somewhere/else.mkv", ops.Links["/lib/a.mkv"]); // left untouched

        await using var mig = h.Mig();
        var row = await mig.SymlinkRewrites.SingleAsync();
        Assert.Equal("failed", row.Status);
        Assert.NotNull(row.Error);
    }

    [Fact]
    public void RealOps_NeverDeletesRealFile_RefusesNonSymlink()
    {
        // The never-delete invariant, tested on the real filesystem without needing
        // symlink privileges: a real file at the path must not be replaced.
        var dir = Path.Combine(Path.GetTempPath(), $"altmig-realops-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var realFile = Path.Combine(dir, "real.mkv");
        File.WriteAllText(realFile, "precious content");

        try
        {
            var ex = Assert.Throws<IOException>(() =>
                RealSymlinkOps.Instance.CreateOrReplaceSymlink(realFile, "/mnt/nzbdav/.ids/x"));
            Assert.Contains("non-symlink", ex.Message);
            Assert.Equal("precious content", File.ReadAllText(realFile)); // untouched
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
