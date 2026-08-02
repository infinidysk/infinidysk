using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Extensions;
using Serilog;

namespace NzbWebDAV.UsenetMigration.Symlinks;

public sealed record SymlinkBackupInfo(
    string FileName,
    DateTime CreatedAt,
    long SizeBytes,
    int EntryCount,
    int LegacyEntryCount,
    string Kind,
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
    public int OrphansRestored { get; init; }
    public IReadOnlyList<SymlinkRestoreIssue> Issues { get; init; } = [];
}

/// <summary>
/// Lists and restores the archives created before Step 6 rewrites and orphan
/// removals. Restore is confined to the configured library and refuses paths
/// whose contents have changed since the archive was written.
/// </summary>
public sealed class SymlinkRestoreService(UsenetMigrationStore store)
{
    internal const string DefaultArchivePrefix = "altmount-symlink-backup-";
    internal const string OrphanRemovalArchivePrefix = "altmount-orphan-symlink-backup-";

    internal const string ArchiveSuffix = ".tar.gz";

    internal ISymlinkOps Ops { get; set; } = RealSymlinkOps.Instance;
    internal Func<string, DateTime> GetLastWriteTimeUtc { get; set; } = File.GetLastWriteTimeUtc;
    internal Func<string, long> GetFileLength { get; set; } = path => new FileInfo(path).Length;
    internal Func<CancellationToken, Task>? BeforeFilesystemWorkForTests { get; set; }

    public async Task<IReadOnlyList<SymlinkBackupInfo>> ListAsync(CancellationToken ct = default)
    {
        var session = await store.GetSessionAsync(ct).ConfigureAwait(false);
        var backupDir = session.SymlinkBackupDir;
        if (string.IsNullOrWhiteSpace(backupDir) || !Directory.Exists(backupDir))
            return [];

        var archives = new List<SymlinkBackupInfo>();
        var archivePaths = Directory
            .EnumerateFiles(backupDir, $"{DefaultArchivePrefix}*{ArchiveSuffix}")
            .Concat(Directory.EnumerateFiles(
                backupDir, $"{OrphanRemovalArchivePrefix}*{ArchiveSuffix}"));
        foreach (var path in archivePaths)
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(path);
            if (!TryGetArchiveKind(fileName, out var fileKind))
                continue;
            try
            {
                var entries = await SymlinkBackup.ReadAsync(path, ct).ConfigureAwait(false);
                var kind = ClassifyArchive(entries, fileKind, fileName);
                archives.Add(new SymlinkBackupInfo(
                    fileName,
                    GetLastWriteTimeUtc(path),
                    GetFileLength(path),
                    entries.Count,
                    entries.Count(e => string.IsNullOrWhiteSpace(e.ReplacementTarget)
                                       && string.IsNullOrWhiteSpace(e.Operation)),
                    kind,
                    true,
                    null));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                Log.Warning(
                    "Unable to read symlink restore archive {ArchivePath}. Reason: {Reason}",
                    path, e.Message);
                Log.Debug(e, "Unreadable symlink restore archive {ArchivePath} failure stack", path);
                archives.Add(new SymlinkBackupInfo(
                    fileName, GetLastWriteTimeUtc(path), GetFileLength(path), 0, 0,
                    fileKind, false, e.Message));
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
        TryGetArchiveKind(fileName, out var fileKind);
        _ = ClassifyArchive(entries, fileKind, fileName);

        // Load plan rows then release the SQLite connection before filesystem work.
        // Holding an open context across CreateOrReplaceSymlink deadlocks callers that
        // need a write transaction on the same migration DB (e.g. concurrent plan/apply).
        List<Database.Models.UsenetMigration.MigrationSymlinkRewrite> planRows;
        await using (var planCtx = store.NewContext())
        {
            planRows = await planCtx.SymlinkRewrites.AsNoTracking().ToListAsync(ct)
                .ConfigureAwait(false);
        }

        if (BeforeFilesystemWorkForTests is { } beforeFilesystemWork)
            await beforeFilesystemWork(ct).ConfigureAwait(false);

        var issues = new List<SymlinkRestoreIssue>();
        var seenPaths = new HashSet<string>(PathComparer);
        var pendingPlanUpdates = new List<PendingPlanUpdate>();
        int restored = 0, alreadyRestored = 0;

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
            var isOrphanRemoval = string.Equals(
                entry.Operation, SymlinkBackup.OrphanRemovalOperation, StringComparison.Ordinal);
            var expectedReplacement = isOrphanRemoval
                ? null
                : entry.ReplacementTarget
                  ?? (planRow is not null && PathsEqual(planRow.OldTarget, entry.Target)
                      ? planRow.NewTarget
                      : null);

            try
            {
                var current = Ops.ReadLink(libraryRoot, entry.Path);
                if (current is null)
                {
                    // Distinguish entirely-missing (stranded by a failed create) from
                    // a real file/dir that must never be overwritten.
                    if (PathExistsAsNonSymlink(entry.Path))
                    {
                        issues.Add(new SymlinkRestoreIssue(
                            entry.Path, "The path exists as a real file or directory and was left untouched."));
                        continue;
                    }

                    Ops.CreateSymlink(libraryRoot, entry.Path, entry.Target);
                    restored++;
                    QueuePlanUpdate(pendingPlanUpdates, entry, expectedReplacement, isOrphanRemoval);
                    continue;
                }
                if (PathsEqual(current, entry.Target))
                {
                    alreadyRestored++;
                    QueuePlanUpdate(pendingPlanUpdates, entry, expectedReplacement, isOrphanRemoval);
                    continue;
                }
                if (isOrphanRemoval)
                {
                    issues.Add(new SymlinkRestoreIssue(
                        entry.Path,
                        $"A different symlink now exists at this path (target '{current}'); it was left untouched."));
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

                Ops.ReplaceSymlink(libraryRoot, entry.Path, expectedReplacement, entry.Target);
                restored++;
                QueuePlanUpdate(pendingPlanUpdates, entry, expectedReplacement, isOrphanRemoval);
            }
            catch (Exception e)
            {
                if (e is IOException)
                {
                    issues.Add(new SymlinkRestoreIssue(entry.Path, e.Message));
                }
                else if (e.TryGetKnownErrorMessage(out var reason))
                {
                    Log.Warning(
                        "Symlink restore skipped {Path}. Reason: {Reason}",
                        entry.Path, reason);
                    Log.Debug(e, "Symlink restore known failure stack for {Path}", entry.Path);
                    issues.Add(new SymlinkRestoreIssue(entry.Path, reason));
                }
                else
                {
                    Log.Error(e, "Unexpected error restoring symlink {Path}: {Message}", entry.Path, e.Message);
                    issues.Add(new SymlinkRestoreIssue(entry.Path, e.Message));
                }
            }
        }

        var requeued = 0;
        var orphansRestored = 0;
        if (pendingPlanUpdates.Count > 0)
        {
            await using var ctx = store.NewContext();
            var rows = await ctx.SymlinkRewrites.ToListAsync(ct).ConfigureAwait(false);
            foreach (var pending in pendingPlanUpdates)
            {
                var row = rows.FirstOrDefault(r => PathsEqual(r.SymlinkPath, pending.Path));
                if (row is null)
                {
                    row = new Database.Models.UsenetMigration.MigrationSymlinkRewrite
                    {
                        SymlinkPath = pending.Path,
                    };
                    ctx.SymlinkRewrites.Add(row);
                    rows.Add(row);
                }

                row.OldTarget = pending.OldTarget;
                row.NewTarget = pending.NewTarget;
                row.Status = pending.Status;
                row.Error = null;
                row.UpdatedAt = DateTime.UtcNow;
                if (pending.Status == "orphan")
                    orphansRestored++;
                else
                    requeued++;
            }

            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }

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
            OrphansRestored = orphansRestored,
            Issues = issues,
        };
    }

    internal static string ResolveArchivePath(string backupDir, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            || !TryGetArchiveKind(fileName, out _))
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

    private static void QueuePlanUpdate(
        List<PendingPlanUpdate> pending,
        SymlinkBackup.Entry entry,
        string? replacementTarget,
        bool isOrphanRemoval)
    {
        if (isOrphanRemoval)
        {
            pending.Add(new PendingPlanUpdate(entry.Path, entry.Target, null, "orphan"));
            return;
        }

        if (!string.IsNullOrWhiteSpace(replacementTarget))
            pending.Add(new PendingPlanUpdate(entry.Path, entry.Target, replacementTarget, "rewrite"));
    }

    private static string ClassifyArchive(
        IReadOnlyList<SymlinkBackup.Entry> entries,
        string fileKind,
        string fileName)
    {
        if (entries.Any(e => !string.IsNullOrWhiteSpace(e.Operation)
                             && !string.Equals(
                                 e.Operation,
                                 SymlinkBackup.OrphanRemovalOperation,
                                 StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"The archive '{fileName}' contains an unsupported symlink operation.");
        }
        var hasOrphanEntries = entries.Any(e => string.Equals(
            e.Operation, SymlinkBackup.OrphanRemovalOperation, StringComparison.Ordinal));
        var hasOtherEntries = entries.Any(e => !string.Equals(
            e.Operation, SymlinkBackup.OrphanRemovalOperation, StringComparison.Ordinal));
        if (hasOrphanEntries && hasOtherEntries)
            throw new InvalidDataException(
                $"The archive '{fileName}' mixes rewrite and orphan-removal entries.");
        if (fileKind == "orphan-removal" && !hasOrphanEntries)
            throw new InvalidDataException(
                $"The orphan-removal archive '{fileName}' does not contain orphan-removal entries.");
        if (fileKind == "rewrite" && hasOrphanEntries)
            throw new InvalidDataException(
                $"The rewrite archive '{fileName}' contains orphan-removal entries.");
        return fileKind;
    }

    private static bool TryGetArchiveKind(string fileName, out string kind)
    {
        kind = "";
        if (!fileName.EndsWith(ArchiveSuffix, StringComparison.Ordinal))
            return false;
        if (fileName.StartsWith(OrphanRemovalArchivePrefix, StringComparison.Ordinal))
        {
            kind = "orphan-removal";
            return true;
        }
        if (fileName.StartsWith(DefaultArchivePrefix, StringComparison.Ordinal))
        {
            kind = "rewrite";
            return true;
        }
        return false;
    }

    private sealed record PendingPlanUpdate(
        string Path,
        string OldTarget,
        string? NewTarget,
        string Status);

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            a.Replace('\\', '/').TrimEnd('/'),
            b.Replace('\\', '/').TrimEnd('/'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool PathExistsAsNonSymlink(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.ReparsePoint) == 0;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
