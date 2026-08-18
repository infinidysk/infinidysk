using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Backup;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Tasks;

public class DatabaseBackupTask(
    ConfigManager configManager,
    WebsocketManager websocketManager,
    DatabaseBackupStore store,
    string kind,
    string? notes = null,
    bool preserved = false
) : BaseTask
{
    protected override Task ExecuteInternal() => RunInternalAsync();

    /// <summary>
    /// Runs the backup body without acquiring <see cref="BaseTask"/>'s global
    /// single-flight slot. Used by the restore stager for the pre-restore dump.
    /// </summary>
    internal Task<DatabaseBackupManifest> RunInternalAsync() => ExecuteBodyAsync();

    private async Task<DatabaseBackupManifest> ExecuteBodyAsync()
    {
        string? stagingPath = null;
        try
        {
            if (store.HasPendingRestore())
            {
                Report("Failed: a restore is already pending. Wait for the restart to finish.");
                throw new InvalidOperationException("A database restore is already pending.");
            }

            store.EnsureInitialized();
            stagingPath = store.CreateStaging(kind);
            var backupId = store.GetStagingBackupId(stagingPath);
            Report($"Creating backup {backupId}");

            if (DatabaseProviderConfig.IsPostgres)
            {
                Report("Skipping main database (PostgreSQL is externally managed and is not included in local backups)");
            }
            else
            {
                await DumpIfExistsAsync(
                    DavDatabaseContext.DatabaseFilePath,
                    Path.Join(stagingPath, DatabaseBackupStore.DbSqlName),
                    "main database").ConfigureAwait(false);
            }

            await DumpIfExistsAsync(
                MetricsDbContext.DatabaseFilePath,
                Path.Join(stagingPath, DatabaseBackupStore.MetricsSqlName),
                "metrics database").ConfigureAwait(false);

            var wardenPath = Path.Join(DavDatabaseContext.ConfigPath, "warden.db");
            await DumpIfExistsAsync(
                wardenPath,
                Path.Join(stagingPath, DatabaseBackupStore.WardenSqlName),
                "warden database").ConfigureAwait(false);

            var lastMigration = await ReadLastMainMigrationAsync().ConfigureAwait(false);
            var isPreRestore = string.Equals(kind, DatabaseBackupKinds.PreRestore, StringComparison.Ordinal);
            var manifest = store.CommitStaging(
                stagingPath,
                kind,
                notes,
                preserved: preserved || isPreRestore,
                appVersion: ConfigManager.AppVersion,
                lastMainMigration: lastMigration);
            stagingPath = null;

            var retention = configManager.GetDatabaseBackupRetentionCount();
            var pruned = store.Prune(retention);
            if (pruned > 0)
                Report($"Pruned {pruned} old backup(s)");

            Report($"Completed backup {manifest.Id}");
            return manifest;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            store.DiscardStaging(stagingPath);
            Report($"Failed: {ex.Message}");
            ex.LogWarningKnownOrStack("Database backup failed");
            throw;
        }
    }

    private async Task DumpIfExistsAsync(string databasePath, string sqlPath, string label)
    {
        if (!File.Exists(databasePath))
        {
            Report($"Skipping {label} (file not found)");
            return;
        }

        Report($"Dumping {label}");
        await SqliteDumper.DumpToFileAsync(
            databasePath,
            sqlPath,
            message => Report($"Dumping {label} — {message}"),
            CancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadLastMainMigrationAsync()
    {
        if (DatabaseProviderConfig.IsPostgres)
        {
            try
            {
                await using var postgresContext = new DavDatabaseContext();
                return (await postgresContext.Database.GetAppliedMigrationsAsync().ConfigureAwait(false))
                    .LastOrDefault();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warning(ex, "Could not read last applied PostgreSQL main-database migration for backup manifest");
                return null;
            }
        }

        if (!File.Exists(DavDatabaseContext.DatabaseFilePath))
            return null;

        try
        {
            await using var ctx = new DavDatabaseContext();
            var applied = await ctx.Database.GetAppliedMigrationsAsync().ConfigureAwait(false);
            return applied.LastOrDefault();
        }
        catch (Exception ex) when (ex is InvalidOperationException or SqliteException)
        {
            Log.Warning(ex, "Could not read last applied main-database migration for backup manifest");

            // Fallback: query sqlite_master / __EFMigrationsHistory directly.
            try
            {
                var cs = new SqliteConnectionStringBuilder
                {
                    DataSource = DavDatabaseContext.DatabaseFilePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString();
                await using var connection = new SqliteConnection(cs);
                await connection.OpenAsync().ConfigureAwait(false);
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    SELECT MigrationId FROM "__EFMigrationsHistory"
                    ORDER BY MigrationId DESC LIMIT 1;
                    """;
                return (string?)await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }
    }

    private void Report(string message)
    {
        _ = websocketManager.SendMessage(WebsocketTopic.DatabaseBackupTaskProgress, message);
        Log.Information("DatabaseBackupTask: {Message}", message);
    }
}
