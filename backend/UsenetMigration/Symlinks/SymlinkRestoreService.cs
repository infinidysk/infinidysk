using Microsoft.EntityFrameworkCore;
using Serilog;

namespace NzbWebDAV.UsenetMigration.Symlinks;

public sealed record SymlinkBackupInfo(
    string FileName,
    DateTime CreatedAt,
    long SizeBytes,
    int EntryCount,
    int LegacyEntryCount,
    bool IsValid,
    string? Error);

public sealed record SymlinkRestoreIssue(string Path, string Reason);

public sealed class SymlinkRestoreSummary
{
    public required string FileName { get; init; }
    public int Total { get; init; }
    public int Restored { get; init; }
    public int AlreadyRestored { get; init; }
    public int Failed { get; init; }
    public int Requeued { get; init; }
    public IReadOnlyList<SymlinkRestoreIssue> Issues { get; init; } = [];
}

/// <summary>
/// Lists and restores the archives created before Step 6 rewrites. Restore is
/// confined to the configured library and refuses links whose targets have
/// changed since the archive was written.
/// </summary>
public sealed class SymlinkRestoreService(UsenetMigrationStore store)
{
    internal const string ArchivePrefix = "altmount-symlink-backup-";
    internal const string ArchiveSuffix = ".tar.gz";

    internal ISymlinkOps Ops { get; set; } = RealSymlinkOps.Instance;
    internal Func<string, DateTime> GetLastWriteTimeUtc { get; set; } = File.GetLastWriteTimeUtc;
    internal Func<string, long> GetFileLength { get; set; } = path => new FileInfo(path).Length;

    public async Task<IReadOnlyList<SymlinkBackupInfo>> ListAsync(CancellationToken ct = default)
    {
        var session = await store.GetSessionAsync(ct).ConfigureAwait(false);
        var backupDir = session.SymlinkBackupDir;
        if (string.IsNullOrWhiteSpace(backupDir) || !Directory.Exists(backupDir))
            return [];

        var archives = new List<SymlinkBackupInfo>();
        foreach (var path in Directory.EnumerateFiles(backupDir, $"{ArchivePrefix}*{ArchiveSuffix}"))
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(path);
            try
            {
                var entries = await SymlinkBackup.ReadAsync(path, ct).ConfigureAwait(false);
                archives.Add(new SymlinkBackupInfo(
                    fileName,
                    GetLastWriteTimeUtc(path),
                    GetFileLength(path),
                    entries.Count,
                    entries.Count(e => string.IsNullOrWhiteSpace(e.ReplacementTarget)),
                    true,
                    null));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                Log.Warning(e, "Unable to read symlink restore archive {ArchivePath}", path);
                archives.Add(new SymlinkBackupInfo(
                    fileName, GetLastWriteTimeUtc(path), GetFileLength(path), 0, 0, false, e.Message));
            }
        }

        return archives.OrderByDescending(a => a.CreatedAt).ToList();
    }

    public async Task<SymlinkRestoreSummary> RestoreAsync(string fileName, CancellationToken ct = default)
    {
        var session = await store.GetSessionAsync(ct).ConfigureAwait(false);
        var libraryRoot = session.SymlinkLibraryRoot
                          ?? throw new InvalidOperationException("Restoring symlinks requires Library Root to be configured.");
        var backupDir = session.SymlinkBackupDir
                        ?? throw new InvalidOperationException("Restoring symlinks requires Backup Directory to be configured.");
        var archivePath = ResolveArchivePath(backupDir, fileName);
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("The selected symlink restore archive no longer exists.", fileName);

        var entries = await SymlinkBackup.ReadAsync(archivePath, ct).ConfigureAwait(false);
        if (entries.Count == 0)
            throw new InvalidDataException("The selected archive does not contain any symlinks.");

        await using var ctx = store.NewContext();
        var planRows = await ctx.SymlinkRewrites.ToListAsync(ct).ConfigureAwait(false);
        var issues = new List<SymlinkRestoreIssue>();
        var seenPaths = new HashSet<string>(PathComparer);
        int restored = 0, alreadyRestored = 0, requeued = 0;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entry.Path) || string.IsNullOrWhiteSpace(entry.Target))
            {
                issues.Add(new SymlinkRestoreIssue(entry.Path, "The archive entry is missing a symlink path or original target."));
                continue;
            }
            if (!IsWithinRoot(libraryRoot, entry.Path))
            {
                issues.Add(new SymlinkRestoreIssue(entry.Path, "The symlink is outside the configured Library Root."));
                continue;
            }
            if (!seenPaths.Add(Path.GetFullPath(entry.Path)))
            {
                issues.Add(new SymlinkRestoreIssue(entry.Path, "The archive contains this symlink more than once."));
                continue;
            }

            var planRow = planRows.FirstOrDefault(r => PathsEqual(r.SymlinkPath, entry.Path));
            var expectedReplacement = entry.ReplacementTarget
                                      ?? (planRow is not null && PathsEqual(planRow.OldTarget, entry.Target)
                                          ? planRow.NewTarget
                                          : null);

            try
            {
                var current = Ops.ReadLink(entry.Path);
                if (current is null)
                {
                    issues.Add(new SymlinkRestoreIssue(entry.Path, "The path is missing or is no longer a symlink."));
                    continue;
                }
                if (PathsEqual(current, entry.Target))
                {
                    alreadyRestored++;
                    requeued += Requeue(ctx, planRow, entry, expectedReplacement);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(expectedReplacement))
                {
                    issues.Add(new SymlinkRestoreIssue(
                        entry.Path,
                        "This older archive cannot verify the current target. Rebuild the rewrite plan before restoring it."));
                    continue;
                }
                if (!PathsEqual(current, expectedReplacement))
                {
                    issues.Add(new SymlinkRestoreIssue(
                        entry.Path,
                        $"The target changed after rewriting (now '{current}'); the symlink was left untouched."));
                    continue;
                }

                Ops.CreateOrReplaceSymlink(entry.Path, entry.Target);
                restored++;
                requeued += Requeue(ctx, planRow, entry, expectedReplacement);
            }
            catch (Exception e)
            {
                issues.Add(new SymlinkRestoreIssue(entry.Path, e.Message));
            }
        }

        if (requeued > 0)
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

        Log.Information(
            "Symlink restore from {ArchivePath}: {Restored} restored, {AlreadyRestored} already restored, {Failed} failed",
            archivePath, restored, alreadyRestored, issues.Count);

        return new SymlinkRestoreSummary
        {
            FileName = fileName,
            Total = entries.Count,
            Restored = restored,
            AlreadyRestored = alreadyRestored,
            Failed = issues.Count,
            Requeued = requeued,
            Issues = issues,
        };
    }

    internal static string ResolveArchivePath(string backupDir, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            || !fileName.StartsWith(ArchivePrefix, StringComparison.Ordinal)
            || !fileName.EndsWith(ArchiveSuffix, StringComparison.Ordinal))
            throw new InvalidDataException("The selected symlink restore archive name is invalid.");

        var root = Path.GetFullPath(backupDir);
        var path = Path.GetFullPath(Path.Combine(root, fileName));
        if (!IsWithinRoot(root, path))
            throw new InvalidDataException("The selected symlink restore archive is outside the configured backup directory.");
        return path;
    }

    internal static bool IsWithinRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative)
               && relative != ".."
               && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
               && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static int Requeue(
        Database.UsenetMigrationDbContext ctx,
        Database.Models.UsenetMigration.MigrationSymlinkRewrite? row,
        SymlinkBackup.Entry entry,
        string? replacementTarget)
    {
        if (string.IsNullOrWhiteSpace(replacementTarget))
            return 0;
        if (row is null)
        {
            row = new Database.Models.UsenetMigration.MigrationSymlinkRewrite
            {
                SymlinkPath = entry.Path,
            };
            ctx.SymlinkRewrites.Add(row);
        }
        row.OldTarget = entry.Target;
        row.NewTarget = replacementTarget;
        row.Status = "rewrite";
        row.Error = null;
        row.UpdatedAt = DateTime.UtcNow;
        return 1;
    }

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            a.Replace('\\', '/').TrimEnd('/'),
            b.Replace('\\', '/').TrimEnd('/'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
