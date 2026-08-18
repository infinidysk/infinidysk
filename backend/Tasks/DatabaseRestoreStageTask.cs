using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Backup;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Tasks;

/// <summary>
/// Validates a backup, creates a pre-restore safety dump, imports dumps into
/// restore-staging, writes the pending-restore intent, then requests a restart
/// so the maintenance phase can swap databases safely.
/// </summary>
public class DatabaseRestoreStageTask(
    ConfigManager configManager,
    WebsocketManager websocketManager,
    DatabaseBackupStore store,
    RestartService restartService,
    string backupId
) : BaseTask
{
    protected override async Task ExecuteInternal()
    {
        try
        {
            DatabaseBackupStore.ValidateBackupId(backupId);
            if (store.HasPendingRestore())
            {
                Report("Failed: a restore is already pending.");
                throw new InvalidOperationException("A database restore is already pending.");
            }

            var manifest = store.Get(backupId)
                ?? throw new FileNotFoundException($"Backup not found: {backupId}");
            var backupDir = store.GetBackupDirectory(backupId);
            var dbSql = Path.Join(backupDir, DatabaseBackupStore.DbSqlName);
            if (!DatabaseProviderConfig.IsPostgres && !File.Exists(dbSql))
                throw new InvalidOperationException("Backup is missing db.sql and cannot be restored.");

            if (DatabaseProviderConfig.IsPostgres && File.Exists(dbSql))
            {
                throw new InvalidOperationException(
                    "This backup contains a SQLite main-database dump. PostgreSQL main databases are externally managed and cannot be restored here.");
            }

            if (DatabaseProviderConfig.IsPostgres
                && !File.Exists(Path.Join(backupDir, DatabaseBackupStore.MetricsSqlName))
                && !File.Exists(Path.Join(backupDir, DatabaseBackupStore.WardenSqlName)))
            {
                throw new InvalidOperationException(
                    "Backup has no local metrics or warden SQLite dumps to restore. Restore the PostgreSQL main database with PostgreSQL tooling.");
            }

            if (!DatabaseProviderConfig.IsPostgres)
                await ValidateMigrationCompatibilityAsync(manifest).ConfigureAwait(false);

            Report($"Creating pre-restore safety backup before restoring {backupId}");
            var safetyTask = new DatabaseBackupTask(
                configManager,
                websocketManager,
                store,
                DatabaseBackupKinds.PreRestore,
                notes: $"Automatic backup before restoring {backupId}",
                preserved: true);
            var preRestore = await safetyTask.RunInternalAsync().ConfigureAwait(false);

            Report("Importing dumps into restore staging");
            store.PrepareRestoreStaging();
            var stagedFiles = new List<string>();

            if (!DatabaseProviderConfig.IsPostgres)
            {
                await ImportOptionalAsync(
                    Path.Join(backupDir, DatabaseBackupStore.DbSqlName),
                    Path.Join(store.RestoreStagingRoot, "db.sqlite"),
                    requireMigrationsHistory: true,
                    "main database",
                    stagedFiles).ConfigureAwait(false);
            }
            else
            {
                Report("Skipping main database restore (PostgreSQL is externally managed)");
            }

            await ImportOptionalAsync(
                Path.Join(backupDir, DatabaseBackupStore.MetricsSqlName),
                Path.Join(store.RestoreStagingRoot, "metrics.sqlite"),
                requireMigrationsHistory: false,
                "metrics database",
                stagedFiles).ConfigureAwait(false);

            await ImportOptionalAsync(
                Path.Join(backupDir, DatabaseBackupStore.WardenSqlName),
                Path.Join(store.RestoreStagingRoot, "warden.db"),
                requireMigrationsHistory: false,
                "warden database",
                stagedFiles).ConfigureAwait(false);

            if (stagedFiles.Count == 0)
                throw new InvalidOperationException("No database dumps were imported into restore staging.");

            store.WritePendingRestore(new PendingRestoreIntent
            {
                BackupId = backupId,
                PreRestoreBackupId = preRestore.Id,
                StagedFiles = stagedFiles,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            Report("Restore staged. Restarting into maintenance mode…");
            restartService.RequestRestartForRestore();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            store.ClearPendingRestore();
            store.ClearRestoreStaging();
            Report($"Failed: {ex.Message}");
            ex.LogWarningKnownOrStack("Database restore staging failed");
            throw;
        }
    }

    private async Task ImportOptionalAsync(
        string sqlPath,
        string targetPath,
        bool requireMigrationsHistory,
        string label,
        List<string> stagedFiles)
    {
        if (!File.Exists(sqlPath))
        {
            Report($"Skipping {label} (dump not present in backup)");
            return;
        }

        Report($"Importing {label}");
        await SqliteSqlImporter.ImportAsync(
            sqlPath,
            targetPath,
            requireMigrationsHistory,
            message => Report($"Importing {label} — {message}"),
            CancellationToken).ConfigureAwait(false);
        stagedFiles.Add(Path.GetFileName(targetPath));
    }

    private static async Task ValidateMigrationCompatibilityAsync(DatabaseBackupManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.LastMainMigration))
            return;

        await using var ctx = new DavDatabaseContext();
        var known = ctx.Database.GetMigrations();
        if (!known.Contains(manifest.LastMainMigration, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "This backup was created by a newer version of nzbdav and cannot be restored here.");
        }
    }

    private void Report(string message)
    {
        _ = websocketManager.SendMessage(WebsocketTopic.DatabaseRestoreTaskProgress, message);
        Log.Information("DatabaseRestoreStageTask: {Message}", message);
    }
}
