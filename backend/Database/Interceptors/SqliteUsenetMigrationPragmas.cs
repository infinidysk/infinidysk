using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Serilog;

namespace NzbWebDAV.Database.Interceptors;

/// <summary>
/// Applies migration-database PRAGMAs: foreign keys, busy timeout, and memory temp
/// store. On write-capable connections also enables WAL, synchronous=NORMAL, and
/// a 64 MB journal size limit. Skips mmap/cache-size tuning — this DB is small.
/// </summary>
public class SqliteUsenetMigrationPragmas : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => ApplyPragmas(connection);

    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ApplyPragmas(connection);
        return Task.CompletedTask;
    }

    private static void ApplyPragmas(DbConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();

            // Connection-level; usually succeeds even on read-only databases.
            command.CommandText = "PRAGMA foreign_keys = ON;";
            command.ExecuteNonQuery();

            command.CommandText = "PRAGMA busy_timeout = 5000;";
            command.ExecuteNonQuery();

            command.CommandText = "PRAGMA temp_store = MEMORY;";
            command.ExecuteNonQuery();

            // WAL / synchronous / journal_size_limit may attempt writes to the DB file.
            // Skip when the connection string explicitly requests read-only mode (CI/goss, etc).
            if (SqliteMainDbPragmas.IsExplicitlyReadOnly(connection.ConnectionString))
                return;

            try
            {
                command.CommandText = "PRAGMA journal_mode = WAL;";
                _ = command.ExecuteScalar();

                command.CommandText = "PRAGMA synchronous = NORMAL;";
                command.ExecuteNonQuery();

                command.CommandText = "PRAGMA journal_size_limit = 67108864;";
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Log.Warning(
                    ex,
                    "Could not set WAL/synchronous PRAGMA on usenet-migration SQLite connection; database may be read-only.");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SQLite usenet-migration connection opened but PRAGMA commands failed. Continuing without PRAGMA changes.");
        }
    }
}
