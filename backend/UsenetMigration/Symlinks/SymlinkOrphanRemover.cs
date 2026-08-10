using Microsoft.EntityFrameworkCore;
using Serilog;

namespace NzbWebDAV.UsenetMigration.Symlinks;

public sealed class OrphanRemovalSummary
{
    public int Removed { get; init; }
    public int Failed { get; init; }

    /// <summary>Path of the restore tarball written before any deletion.</summary>
    public string? BackupPath { get; init; }
}

/// <summary>
/// Removes only links classified as orphaned by the current Step 6 plan. Every
/// candidate is backed up and revalidated before its link inode is deleted; link
/// targets, real files, unrelated links, and unreadable rows are never touched.
/// </summary>
public sealed class SymlinkOrphanRemover(UsenetMigrationStore store)
{
    internal ISymlinkOps Ops { get; set; } = RealSymlinkOps.Instance;
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;
    internal string ArchivePrefix { get; set; } = SymlinkRestoreService.OrphanRemovalArchivePrefix;

    public async Task<OrphanRemovalSummary> RemoveAsync(CancellationToken ct = default)
    {
        var session = await store.GetSessionAsync(ct).ConfigureAwait(false);
        if (session.Status is not "removing_orphans")
        {
            throw new InvalidOperationException(
                "Removing orphaned symlinks requires an active orphan-removal operation.");
        }
        var libraryRoot = session.SymlinkLibraryRoot
                          ?? throw new InvalidOperationException("Removing orphaned symlinks requires Library Root to be configured.");
        var backupDir = session.SymlinkBackupDir
                        ?? throw new InvalidOperationException("Removing orphaned symlinks requires SymlinkBackupDir to be set.");

        await using var ctx = store.NewContext();
        var rows = await ctx.SymlinkRewrites
            .Where(r => r.Status == "orphan")
            .OrderBy(r => r.SymlinkPath)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rows.Count == 0)
            return new OrphanRemovalSummary();

        var candidates = new List<Database.Models.UsenetMigration.MigrationSymlinkRewrite>(rows.Count);
        var failed = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var current = Ops.ReadLink(libraryRoot, row.SymlinkPath);
                if (current is null)
                {
                    Fail(row, "No symlink is present; rebuild the plan before retrying removal.");
                    failed++;
                }
                else if (!PathsEqual(current, row.OldTarget))
                {
                    Fail(row, $"Symlink target changed since planning (now '{current}'); left untouched.");
                    failed++;
                }
                else
                {
                    candidates.Add(row);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Fail(row, e.Message);
                failed++;
            }

            row.UpdatedAt = UtcNow();
        }

        if (candidates.Count == 0)
        {
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            return new OrphanRemovalSummary { Failed = failed };
        }

        // Intentionally back up every pre-validated candidate, not only links that
        // are ultimately deleted. This safe superset guarantees that every possible
        // mutation is recoverable before the first delete; restore treats an unchanged
        // link as already restored and leaves a subsequently changed link untouched.
        var backupPath = BuildBackupPath(backupDir, UtcNow(), ArchivePrefix);
        var entries = candidates
            .Select(r => new SymlinkBackup.Entry(
                r.SymlinkPath,
                r.OldTarget,
                Operation: SymlinkBackup.OrphanRemovalOperation))
            .ToList();
        await SymlinkBackup.WriteAsync(backupPath, entries, ct).ConfigureAwait(false);

        IReadOnlyList<SymlinkBackup.Entry> verified;
        try
        {
            verified = await SymlinkBackup.ReadAsync(backupPath, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            throw new InvalidDataException(
                $"Orphan symlink backup at '{backupPath}' could not be verified after writing; removal aborted.", e);
        }

        if (verified.Count != entries.Count || !verified.SequenceEqual(entries))
        {
            throw new InvalidDataException(
                $"Orphan symlink backup at '{backupPath}' did not match the requested removals; removal aborted.");
        }

        var removed = 0;
        try
        {
            foreach (var row in candidates)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    Ops.DeleteSymlink(libraryRoot, row.SymlinkPath, row.OldTarget);
                    row.Status = "removed";
                    row.Error = null;
                    removed++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Fail(row, e.Message);
                    failed++;
                }

                row.UpdatedAt = UtcNow();
            }
        }
        finally
        {
            // Persist completed deletions even when the user cancels a large batch.
            await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }

        Log.Information(
            "Orphan symlink removal: {Removed} removed, {Failed} failed. Backup at {BackupPath}",
            removed, failed, backupPath);

        return new OrphanRemovalSummary
        {
            Removed = removed,
            Failed = failed,
            BackupPath = backupPath,
        };
    }

    private static void Fail(Database.Models.UsenetMigration.MigrationSymlinkRewrite row, string error)
    {
        row.Status = "orphan";
        row.Error = error;
    }

    private static string BuildBackupPath(string backupDir, DateTime createdAt, string archivePrefix)
    {
        var stem = $"{archivePrefix}{createdAt:yyyyMMdd-HHmmss}";
        var path = Path.Join(backupDir, $"{stem}.tar.gz");
        for (var suffix = 2; File.Exists(path); suffix++)
            path = Path.Join(backupDir, $"{stem}-{suffix}.tar.gz");
        return path;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            a.Replace('\\', '/').TrimEnd('/'),
            b.Replace('\\', '/').TrimEnd('/'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
