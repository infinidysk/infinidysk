using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Serilog;

namespace NzbWebDAV.Database.Interceptors;

/// <summary>
/// Applies main-database PRAGMAs: foreign keys, busy timeout, memory temp store,
/// a 256 MB mmap window, and a 64 MB page cache (matching the metrics DB). On
/// write-capable connections also enables WAL, synchronous=NORMAL, and a 64 MB
/// journal size limit so the WAL shrinks after bulk imports.
/// </summary>
public class SqliteMainDbPragmas : DbConnectionInterceptor
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

            command.CommandText = "PRAGMA mmap_size = 268435456;";
            command.ExecuteNonQuery();

            command.CommandText = "PRAGMA cache_size = -65536;";
            command.ExecuteNonQuery();

            // WAL / synchronous / journal_size_limit may attempt writes to the DB file.
            // Skip when the connection string explicitly requests read-only mode (CI/goss, etc).
            if (IsExplicitlyReadOnly(connection.ConnectionString))
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
            catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
            {
                Log.Warning(
                    ex,
                    "Could not set WAL/synchronous PRAGMA on main SQLite connection; database may be read-only.");
            }
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
        {
            Log.Warning(ex, "SQLite main connection opened but PRAGMA commands failed. Continuing without PRAGMA changes.");
        }
    }

    internal static bool IsExplicitlyReadOnly(string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString)) return false;
        return connectionString.Contains("mode=readonly", StringComparison.OrdinalIgnoreCase)
               || connectionString.Contains("mode=read-only", StringComparison.OrdinalIgnoreCase)
               || connectionString.Contains("Mode=ReadOnly", StringComparison.OrdinalIgnoreCase);
    }
}
