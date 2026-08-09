using System.Text.Json;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Backup;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Manages on-disk database backups under <c>{CONFIG_PATH}/backups/</c>.
/// Manifests live next to the <c>.sql</c> dumps so metadata survives a restore.
/// </summary>
public sealed class DatabaseBackupStore
{
    public const string DbSqlName = "db.sql";
    public const string MetricsSqlName = "metrics.sql";
    public const string WardenSqlName = "warden.sql";
    public const string ManifestFileName = "manifest.json";
    public const string RollbackFolderName = "rollback";
    public const string LastRestoreReportFileName = "last-restore-report.json";
    public const string PendingRestoreFileName = "pending-restore.json";
    public const string RestoreStagingFolderName = "restore-staging";

    private readonly object _gate = new();

    public string BackupsRoot => Path.Join(DavDatabaseContext.ConfigPath, "backups");
    public string RestoreStagingRoot => Path.Join(DavDatabaseContext.ConfigPath, RestoreStagingFolderName);
    public string PendingRestorePath => Path.Join(DavDatabaseContext.ConfigPath, PendingRestoreFileName);
    public string LastRestoreReportPath => Path.Join(BackupsRoot, LastRestoreReportFileName);

    public void EnsureInitialized()
    {
        Directory.CreateDirectory(BackupsRoot);
        CleanupTempFolders();
    }

    public IReadOnlyList<DatabaseBackupManifest> List()
    {
        EnsureInitialized();

        var results = new List<DatabaseBackupManifest>();
        foreach (var dir in Directory.EnumerateDirectories(BackupsRoot))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith(".tmp-", StringComparison.Ordinal))
                continue;

            try
            {
                var manifest = ReadManifest(name);
                if (manifest is not null)
                    results.Add(manifest);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Skipping unreadable database backup {BackupId}", name);
            }
        }

        return results
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id, StringComparer.Ordinal)
            .ToList();
    }

    public DatabaseBackupManifest? Get(string backupId)
    {
        ValidateBackupId(backupId);
        return ReadManifest(backupId);
    }

    public string GetBackupDirectory(string backupId)
    {
        ValidateBackupId(backupId);
        return Path.Join(BackupsRoot, backupId);
    }

    public string CreateStaging(string kind)
    {
        EnsureInitialized();
        var id = $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{SanitizeKind(kind)}-{Guid.NewGuid().ToString("N")[..6]}";
        var stagingPath = Path.Join(BackupsRoot, $".tmp-{id}");
        if (Directory.Exists(stagingPath))
            Directory.Delete(stagingPath, recursive: true);
        Directory.CreateDirectory(stagingPath);
        return stagingPath;
    }

    public string GetStagingBackupId(string stagingPath)
    {
        var name = Path.GetFileName(stagingPath);
        if (!name.StartsWith(".tmp-", StringComparison.Ordinal))
            throw new ArgumentException("Not a staging path.", nameof(stagingPath));
        return name[".tmp-".Length..];
    }

    public DatabaseBackupManifest CommitStaging(
        string stagingPath,
        string kind,
        string? notes,
        bool preserved,
        string? appVersion,
        string? lastMainMigration)
    {
        var backupId = GetStagingBackupId(stagingPath);
        var files = new List<DatabaseBackupFileEntry>();
        foreach (var sqlName in new[] { DbSqlName, MetricsSqlName, WardenSqlName })
        {
            var path = Path.Join(stagingPath, sqlName);
            if (!File.Exists(path))
                continue;
            files.Add(new DatabaseBackupFileEntry
            {
                Name = sqlName,
                Bytes = new FileInfo(path).Length,
            });
        }

        if (files.Count == 0)
            throw new InvalidOperationException("Backup staging folder contains no .sql dumps.");

        var manifest = new DatabaseBackupManifest
        {
            Id = backupId,
            CreatedAt = DateTimeOffset.UtcNow,
            Kind = SanitizeKind(kind),
            Notes = notes ?? "",
            Preserved = preserved,
            AppVersion = appVersion,
            LastMainMigration = lastMainMigration,
            Files = files,
        };

        WriteManifestAtomic(stagingPath, manifest);

        var finalPath = Path.Join(BackupsRoot, backupId);
        if (Directory.Exists(finalPath))
            throw new InvalidOperationException($"Backup id already exists: {backupId}");

        Directory.Move(stagingPath, finalPath);
        return manifest;
    }

    public void DiscardStaging(string? stagingPath)
    {
        if (string.IsNullOrWhiteSpace(stagingPath))
            return;
        try
        {
            if (Directory.Exists(stagingPath))
                Directory.Delete(stagingPath, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to discard backup staging folder {Path}", stagingPath);
        }
    }

    public DatabaseBackupManifest UpdateManifest(string backupId, bool? preserved = null, string? notes = null)
    {
        ValidateBackupId(backupId);
        var manifest = ReadManifest(backupId)
            ?? throw new FileNotFoundException($"Backup not found: {backupId}");

        if (preserved.HasValue)
            manifest.Preserved = preserved.Value;
        if (notes is not null)
            manifest.Notes = notes;

        WriteManifestAtomic(GetBackupDirectory(backupId), manifest);
        return manifest;
    }

    public void SaveManifest(DatabaseBackupManifest manifest)
    {
        ValidateBackupId(manifest.Id);
        WriteManifestAtomic(GetBackupDirectory(manifest.Id), manifest);
    }

    public void Delete(string backupId)
    {
        ValidateBackupId(backupId);
        var path = GetBackupDirectory(backupId);
        if (!Directory.Exists(path))
            throw new FileNotFoundException($"Backup not found: {backupId}");
        Directory.Delete(path, recursive: true);
    }

    public int Prune(int retentionCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retentionCount);
        if (retentionCount == 0)
            return 0;

        var candidates = List()
            .Where(x => !x.Preserved)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id, StringComparer.Ordinal)
            .ToList();

        var removed = 0;
        for (var i = retentionCount; i < candidates.Count; i++)
        {
            try
            {
                Delete(candidates[i].Id);
                removed++;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to prune database backup {BackupId}", candidates[i].Id);
            }
        }

        return removed;
    }

    public bool HasPendingRestore() => File.Exists(PendingRestorePath);

    public PendingRestoreIntent? ReadPendingRestore()
    {
        if (!File.Exists(PendingRestorePath))
            return null;

        var json = File.ReadAllText(PendingRestorePath);
        return JsonSerializer.Deserialize<PendingRestoreIntent>(json, DatabaseBackupJson.Options);
    }

    public void WritePendingRestore(PendingRestoreIntent intent)
    {
        Directory.CreateDirectory(DavDatabaseContext.ConfigPath);
        var json = JsonSerializer.Serialize(intent, DatabaseBackupJson.Options);
        var temp = PendingRestorePath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, PendingRestorePath, overwrite: true);
    }

    public void ClearPendingRestore()
    {
        if (File.Exists(PendingRestorePath))
            File.Delete(PendingRestorePath);
    }

    public LastRestoreReport? ReadLastRestoreReport()
    {
        if (!File.Exists(LastRestoreReportPath))
            return null;
        var json = File.ReadAllText(LastRestoreReportPath);
        return JsonSerializer.Deserialize<LastRestoreReport>(json, DatabaseBackupJson.Options);
    }

    public void WriteLastRestoreReport(LastRestoreReport report)
    {
        EnsureInitialized();
        var json = JsonSerializer.Serialize(report, DatabaseBackupJson.Options);
        var temp = LastRestoreReportPath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, LastRestoreReportPath, overwrite: true);
    }

    public void ClearRestoreStaging()
    {
        if (Directory.Exists(RestoreStagingRoot))
            Directory.Delete(RestoreStagingRoot, recursive: true);
    }

    public void PrepareRestoreStaging()
    {
        ClearRestoreStaging();
        Directory.CreateDirectory(RestoreStagingRoot);
    }

    private DatabaseBackupManifest? ReadManifest(string backupId)
    {
        var path = Path.Join(GetBackupDirectory(backupId), ManifestFileName);
        if (!File.Exists(path))
            return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<DatabaseBackupManifest>(json, DatabaseBackupJson.Options);
    }

    private void WriteManifestAtomic(string backupDirectory, DatabaseBackupManifest manifest)
    {
        Directory.CreateDirectory(backupDirectory);
        var path = Path.Join(backupDirectory, ManifestFileName);
        var temp = path + ".tmp";
        var json = JsonSerializer.Serialize(manifest, DatabaseBackupJson.Options);
        lock (_gate)
        {
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
    }

    private void CleanupTempFolders()
    {
        if (!Directory.Exists(BackupsRoot))
            return;

        foreach (var dir in Directory.EnumerateDirectories(BackupsRoot, ".tmp-*"))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to clean leftover backup staging folder {Path}", dir);
            }
        }
    }

    private static string SanitizeKind(string kind)
    {
        var cleaned = new string(kind.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray());
        while (cleaned.Contains("--", StringComparison.Ordinal))
            cleaned = cleaned.Replace("--", "-", StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(cleaned) ? DatabaseBackupKinds.Manual : cleaned.Trim('-');
    }

    public static void ValidateBackupId(string backupId)
    {
        if (string.IsNullOrWhiteSpace(backupId))
            throw new ArgumentException("Backup id is required.", nameof(backupId));
        if (backupId.Contains('/', StringComparison.Ordinal) || backupId.Contains('\\', StringComparison.Ordinal) || backupId.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Invalid backup id.", nameof(backupId));
        if (backupId.StartsWith('.'))
            throw new ArgumentException("Invalid backup id.", nameof(backupId));
    }
}
