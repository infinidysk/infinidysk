using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.WebDav;
using Serilog;

namespace NzbWebDAV.UsenetMigration.Symlinks;

public sealed class RewriteSummary
{
    public int Applied { get; init; }
    public int Failed { get; init; }

    /// <summary>Path of the restore tarball written before any change.</summary>
    public string? BackupPath { get; init; }
}

/// <summary>
/// Applies the Step 6 rewrite plan. It retargets matched links with three guarantees:
/// <list type="number">
/// <item><b>Backup first.</b> A restore tarball of every to-be-changed symlink's prior
///   state is written before the first rewrite.</item>
/// <item><b>Retarget only, never delete.</b> Each rewrite replaces a symlink's target
///   via <see cref="ISymlinkOps"/>, which removes only the link inode; migrated content
///   and any real (non-symlink) file are never touched. Orphan / not-altmount /
///   already-nzbdav rows are never loaded, so they are untouched by rewrite apply.</item>
/// <item><b>Drift-guarded and idempotent.</b> A row is rewritten only if the on-disk
///   target still equals the planned <c>OldTarget</c>; a symlink already pointing at
///   <c>NewTarget</c> is a no-op success, so re-running apply is safe.</item>
/// </list>
/// </summary>
public sealed class SymlinkRewriter(UsenetMigrationStore store, ConfigManager configManager)
{
    /// <summary>Test seam for filesystem symlink operations; production uses the real FS.</summary>
    internal ISymlinkOps Ops { get; set; } = RealSymlinkOps.Instance;

    /// <summary>Test seam for the current time, so backup filenames are deterministic.</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>Test seam for the live NzbDAV context; production leaves it null.</summary>
    internal Func<DavDatabaseContext>? DavContextFactory { get; set; }

    /// <summary>Archive filename prefix; override for a future migration source with distinct archives.</summary>
    internal string ArchivePrefix { get; set; } = SymlinkRestoreService.DefaultArchivePrefix;

    public async Task<RewriteSummary> ApplyAsync(CancellationToken ct = default)
    {
        var session = await store.GetSessionAsync(ct).ConfigureAwait(false);
        var libraryRoot = session.SymlinkLibraryRoot
                          ?? throw new InvalidOperationException("Applying symlinks requires Library Root to be configured.");
        var backupDir = session.SymlinkBackupDir
                        ?? throw new InvalidOperationException("Applying symlinks requires SymlinkBackupDir to be set.");

        await using var ctx = store.NewContext();
        var rows = await ctx.SymlinkRewrites
            .Where(r => r.Status == "rewrite" && r.NewTarget != null)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rows.Count == 0)
            return new RewriteSummary();

        // Re-validate destinations before any backup or rewrite: DavItems may have
        // been deleted and the rclone mount dir may have changed since planning.
        var mountDir = configManager.GetRcloneMountDir();
        var invalidBeforeApply = await InvalidateStaleDestinationsAsync(rows, mountDir, ct)
            .ConfigureAwait(false);
        if (invalidBeforeApply > 0)
        {
            Log.Warning(
                "Symlink apply skipped {Failed} row(s) whose destinations are no longer valid",
                invalidBeforeApply);
        }

        var actionable = rows.Where(r => r.Status == "rewrite").ToList();
        if (actionable.Count == 0)
        {
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            return new RewriteSummary { Failed = invalidBeforeApply };
        }

        // 1) Backup FIRST — the prior state of every row we might change.
        var backupPath = BuildBackupPath(backupDir, UtcNow(), ArchivePrefix);
        await SymlinkBackup.WriteAsync(
            backupPath,
            actionable.Select(r => new SymlinkBackup.Entry(r.SymlinkPath, r.OldTarget, r.NewTarget)).ToList(),
            ct).ConfigureAwait(false);

        // Verify the archive is readable before touching the library.
        IReadOnlyList<SymlinkBackup.Entry> verified;
        try
        {
            verified = await SymlinkBackup.ReadAsync(backupPath, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            throw new InvalidDataException(
                $"Symlink backup at '{backupPath}' could not be verified after writing; apply aborted.", e);
        }

        if (verified.Count != actionable.Count)
        {
            throw new InvalidDataException(
                $"Symlink backup at '{backupPath}' entry count mismatch " +
                $"(wrote {actionable.Count}, read {verified.Count}); apply aborted.");
        }

        // 2) Retarget each, drift-guarded and idempotent.
        int applied = 0, failed = invalidBeforeApply;
        foreach (var row in actionable)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var current = Ops.ReadLink(libraryRoot, row.SymlinkPath);
                if (current is null)
                {
                    Fail(row, "No symlink present at apply time.");
                    failed++;
                }
                else if (PathsEqual(current, row.NewTarget!))
                {
                    // Already retargeted — idempotent no-op.
                    row.Status = "applied";
                    row.Error = null;
                    applied++;
                }
                else if (!PathsEqual(current, row.OldTarget))
                {
                    Fail(row, $"Symlink target changed since plan (now '{current}'); left untouched.");
                    failed++;
                }
                else
                {
                    Ops.ReplaceSymlink(libraryRoot, row.SymlinkPath, row.OldTarget, row.NewTarget!);
                    row.Status = "applied";
                    row.Error = null;
                    applied++;
                }
            }
            catch (Exception e)
            {
                Fail(row, e.Message);
                failed++;
            }

            row.UpdatedAt = UtcNow();
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

        Log.Information(
            "Symlink apply: {Applied} applied, {Failed} failed. Backup at {BackupPath}",
            applied, failed, backupPath);

        return new RewriteSummary { Applied = applied, Failed = failed, BackupPath = backupPath };
    }

    private async Task<int> InvalidateStaleDestinationsAsync(
        List<Database.Models.UsenetMigration.MigrationSymlinkRewrite> rows,
        string mountDir,
        CancellationToken ct)
    {
        var parsed = rows
            .Select(r => (Row: r, Id: TryParseDavItemId(r.NewTarget)))
            .ToList();
        var guids = parsed
            .Where(p => p.Id is not null)
            .Select(p => p.Id!.Value)
            .Distinct()
            .ToList();

        HashSet<Guid> existing;
        await using (var dav = DavDatabaseContexts.Create(DavContextFactory))
        {
            // Chunked IN queries keep SQLite parameter limits safe for large plans.
            existing = new HashSet<Guid>();
            const int chunkSize = 400;
            for (var i = 0; i < guids.Count; i += chunkSize)
            {
                var chunk = guids.Skip(i).Take(chunkSize).ToList();
                var found = await dav.Items.AsNoTracking()
                    .Where(item => chunk.Contains(item.Id))
                    .Select(item => item.Id)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                foreach (var id in found)
                    existing.Add(id);
            }
        }

        var failed = 0;
        var now = UtcNow();
        foreach (var (row, id) in parsed)
        {
            if (id is null || !existing.Contains(id.Value))
            {
                Fail(row, "target DavItem no longer exists");
                row.UpdatedAt = now;
                failed++;
            }
            else if (!MountDirStillPrefixes(row.NewTarget!, mountDir))
            {
                Fail(row, "rclone mount dir changed since planning — rebuild the plan");
                row.UpdatedAt = now;
                failed++;
            }
        }

        return failed;
    }

    /// <summary>
    /// Extracts the DavItem GUID from a planned <c>.ids/…/&lt;guid&gt;</c> target
    /// produced by <see cref="DatabaseStoreSymlinkFile.GetTargetPath(Guid,string,char?)"/>.
    /// </summary>
    internal static Guid? TryParseDavItemId(string? newTarget)
    {
        if (string.IsNullOrWhiteSpace(newTarget))
            return null;
        var leaf = Path.GetFileName(newTarget.Replace('\\', '/').TrimEnd('/'));
        return Guid.TryParse(leaf, out var id) ? id : null;
    }

    internal static bool MountDirStillPrefixes(string newTarget, string mountDir)
    {
        var normTarget = newTarget.Replace('\\', '/');
        var normMount = mountDir.Replace('\\', '/').TrimEnd('/');
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return normTarget.StartsWith(normMount + "/", comparison)
               || string.Equals(normTarget, normMount, comparison);
    }

    private static void Fail(Database.Models.UsenetMigration.MigrationSymlinkRewrite row, string error)
    {
        row.Status = "failed";
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
