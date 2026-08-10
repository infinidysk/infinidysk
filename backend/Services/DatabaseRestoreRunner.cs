using Microsoft.Data.Sqlite;
using System.Text.Json;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Backup;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Applies a staged database restore during the <c>--db-migration</c> maintenance
/// phase: checkpoint/VACUUM current DBs, move them into a rollback folder, swap
/// in the staged files, then scan for missing blob references.
/// </summary>
public static class DatabaseRestoreRunner
{
    public const string RollbackStepId = "__restore_rollback__";
    public const string SwapStepId = "__restore_swap__";
    public const string BlobScanStepId = "__restore_blobscan__";

    public static bool HasPendingRestore(DatabaseBackupStore? store = null)
    {
        store ??= new DatabaseBackupStore();
        return store.HasPendingRestore();
    }

    public static List<MigrationProgress.MigrationStep> GetRestoreSteps(PendingRestoreIntent intent)
    {
        return
        [
            new MigrationProgress.MigrationStep(RollbackStepId, "Preparing current databases for rollback", true),
            new MigrationProgress.MigrationStep(SwapStepId, $"Restoring backup {intent.BackupId}", true),
            new MigrationProgress.MigrationStep(BlobScanStepId, "Scanning for missing blob files", false),
        ];
    }

    public static async Task ApplyPendingRestoreAsync(
        MigrationProgress progress,
        CancellationToken cancellationToken = default)
    {
        var store = new DatabaseBackupStore();
        var intent = store.ReadPendingRestore();
        if (intent is null)
            return;

        if (intent.StagedFiles.Count == 0 ||
            !intent.StagedFiles.All(name => File.Exists(Path.Join(store.RestoreStagingRoot, name))))
        {
            Log.Warning(
                "Pending restore intent {BackupId} is missing staged files; discarding and continuing startup",
                intent.BackupId);
            store.ClearPendingRestore();
            store.ClearRestoreStaging();
            return;
        }

        var movedAway = new List<(string originalPath, string rollbackPath)>();
        try
        {
            progress.BeginStep(RollbackStepId);
            var rollbackDir = Path.Join(
                store.GetBackupDirectory(intent.PreRestoreBackupId),
                DatabaseBackupStore.RollbackFolderName);
            Directory.CreateDirectory(rollbackDir);

            foreach (var stagedName in intent.StagedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var livePath = ResolveLivePath(stagedName);
                if (!File.Exists(livePath) && !File.Exists(livePath + "-wal") && !File.Exists(livePath + "-shm"))
                    continue;

                await CheckpointAndVacuumAsync(livePath, cancellationToken).ConfigureAwait(false);
                SqliteConnection.ClearAllPools();

                var rollbackPath = Path.Join(rollbackDir, Path.GetFileName(livePath));
                MoveDatabaseFiles(livePath, rollbackPath);
                movedAway.Add((livePath, rollbackPath));
            }

            progress.CompleteStep(RollbackStepId);

            progress.BeginStep(SwapStepId);
            foreach (var stagedName in intent.StagedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stagedPath = Path.Join(store.RestoreStagingRoot, stagedName);
                var livePath = ResolveLivePath(stagedName);
                Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
                File.Move(stagedPath, livePath, overwrite: false);
            }

            progress.CompleteStep(SwapStepId);

            // Update pre-restore manifest with rollback file sizes.
            try
            {
                var pre = store.Get(intent.PreRestoreBackupId);
                if (pre is not null)
                {
                    pre.Files.RemoveAll(f => f.Name.StartsWith(DatabaseBackupStore.RollbackFolderName + "/", StringComparison.Ordinal)
                                             || f.Name.StartsWith(DatabaseBackupStore.RollbackFolderName + "\\", StringComparison.Ordinal));
                    foreach (var file in Directory.EnumerateFiles(rollbackDir))
                    {
                        pre.Files.Add(new DatabaseBackupFileEntry
                        {
                            Name = Path.Join(DatabaseBackupStore.RollbackFolderName, Path.GetFileName(file))
                                .Replace('\\', '/'),
                            Bytes = new FileInfo(file).Length,
                        });
                    }

                    store.SaveManifest(pre);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Log.Warning(ex, "Could not update pre-restore backup manifest with rollback files");
            }

            store.ClearPendingRestore();
            store.ClearRestoreStaging();

            progress.BeginStep(BlobScanStepId);
            var report = await ScanMissingBlobsAsync(intent.BackupId, cancellationToken).ConfigureAwait(false);
            store.WriteLastRestoreReport(report);
            if (report.MissingBlobRefs > 0)
            {
                Log.Warning(
                    "Restore {BackupId}: {Missing}/{Checked} blob references point to missing files under blobs/",
                    report.BackupId, report.MissingBlobRefs, report.CheckedRefs);
            }
            else
            {
                Log.Information(
                    "Restore {BackupId}: all {Checked} blob references resolved on disk",
                    report.BackupId, report.CheckedRefs);
            }

            progress.CompleteStep(BlobScanStepId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Database restore swap failed; attempting to restore pre-swap databases");
            try
            {
                SqliteConnection.ClearAllPools();
                foreach (var (originalPath, rollbackPath) in movedAway)
                {
                    if (File.Exists(rollbackPath))
                        MoveDatabaseFiles(rollbackPath, originalPath);
                }
            }
            catch (Exception rollbackEx) when (rollbackEx is not OutOfMemoryException)
            {
                Log.Error(rollbackEx, "Failed to roll back database files after a failed restore swap");
            }

            progress.Fail(ex.Message);
            throw;
        }
    }

    private static string ResolveLivePath(string stagedFileName) => stagedFileName switch
    {
        "db.sqlite" => DavDatabaseContext.DatabaseFilePath,
        "metrics.sqlite" => MetricsDbContext.DatabaseFilePath,
        "warden.db" => Path.Join(DavDatabaseContext.ConfigPath, "warden.db"),
        _ => throw new InvalidOperationException($"Unknown staged database file: {stagedFileName}"),
    };

    private static async Task CheckpointAndVacuumAsync(string databasePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath))
            return;

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 60,
        }.ToString();

        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var vacuum = connection.CreateCommand())
        {
            vacuum.CommandText = "VACUUM;";
            await vacuum.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void MoveDatabaseFiles(string fromDbPath, string toDbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(toDbPath)!);
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var from = fromDbPath + suffix;
            var to = toDbPath + suffix;
            if (!File.Exists(from))
                continue;
            if (File.Exists(to))
                File.Delete(to);
            File.Move(from, to);
        }
    }

    private static async Task<LastRestoreReport> ScanMissingBlobsAsync(
        string backupId,
        CancellationToken cancellationToken)
    {
        long checkedRefs = 0;
        long missing = 0;

        if (!File.Exists(DavDatabaseContext.DatabaseFilePath))
        {
            return new LastRestoreReport
            {
                BackupId = backupId,
                RestoredAt = DateTimeOffset.UtcNow,
                MissingBlobRefs = 0,
                CheckedRefs = 0,
            };
        }

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = DavDatabaseContext.DatabaseFilePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await CollectBlobIdsAsync(connection, """
            SELECT DISTINCT FileBlobId FROM DavItems WHERE FileBlobId IS NOT NULL AND FileBlobId != ''
            """, ids, cancellationToken).ConfigureAwait(false);
        await CollectBlobIdsAsync(connection, """
            SELECT DISTINCT NzbBlobId FROM DavItems WHERE NzbBlobId IS NOT NULL AND NzbBlobId != ''
            """, ids, cancellationToken).ConfigureAwait(false);
        await CollectBlobIdsAsync(connection, """
            SELECT DISTINCT NzbBlobId FROM HistoryItems WHERE NzbBlobId IS NOT NULL AND NzbBlobId != ''
            """, ids, cancellationToken).ConfigureAwait(false);

        foreach (var idText in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            checkedRefs++;
            if (!Guid.TryParse(idText, out var id))
            {
                missing++;
                continue;
            }

            if (!File.Exists(GetBlobPath(id)))
                missing++;
        }

        return new LastRestoreReport
        {
            BackupId = backupId,
            RestoredAt = DateTimeOffset.UtcNow,
            MissingBlobRefs = missing,
            CheckedRefs = checkedRefs,
        };
    }

    private static async Task CollectBlobIdsAsync(
        SqliteConnection connection,
        string sql,
        HashSet<string> ids,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false))
                    ids.Add(reader.GetString(0));
            }
        }
        catch (SqliteException ex)
        {
            // Older schemas may lack a column — treat as no refs.
            Log.Debug(ex, "Skipping blob-id query during restore scan: {Sql}", sql);
        }
    }

    private static string GetBlobPath(Guid id)
    {
        var guidStr = id.ToString("N");
        var firstTwo = guidStr[..2];
        var nextTwo = guidStr.Substring(2, 2);
        var fileName = id.ToString();
        return Path.Join(DavDatabaseContext.ConfigPath, "blobs", firstTwo, nextTwo, fileName);
    }
}
