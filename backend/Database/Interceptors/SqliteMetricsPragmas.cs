using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Serilog;

namespace NzbWebDAV.Database.Interceptors;

/// <summary>
/// Applies metrics-tuned PRAGMAs: WAL, relaxed durability (NORMAL — losing one
/// second of metrics on a crash is acceptable), busy timeout, memory temp store,
/// a 256 MB mmap window, a 64 MB page cache, and a capped WAL journal. Incremental
/// auto-vacuum lets the retention sweep reclaim space without a full VACUUM.
/// </summary>
public class SqliteMetricsPragmas : DbConnectionInterceptor
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

            if (SqliteMainDbPragmas.IsExplicitlyReadOnly(connection.ConnectionString))
            {
                // Still apply read-only-safe settings that do not rewrite the DB header.
                command.CommandText =
                    "PRAGMA busy_timeout = 5000;" +
                    "PRAGMA temp_store = MEMORY;" +
                    "PRAGMA mmap_size = 268435456;" +
                    "PRAGMA cache_size = -65536;";
                command.ExecuteNonQuery();
                return;
            }

            try
            {
                command.CommandText =
                    "PRAGMA journal_mode = WAL;" +
                    "PRAGMA synchronous = NORMAL;" +
                    "PRAGMA busy_timeout = 5000;" +
                    "PRAGMA temp_store = MEMORY;" +
                    "PRAGMA mmap_size = 268435456;" +
                    "PRAGMA cache_size = -65536;" +
                    "PRAGMA journal_size_limit = 67108864;" +
                    "PRAGMA auto_vacuum = INCREMENTAL;";
                command.ExecuteNonQuery();
            }
            catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
            {
                Log.Warning(
                    ex,
                    "Could not set metrics SQLite PRAGMAs; database may be read-only.");
            }
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
        {
            Log.Warning(ex, "SQLite metrics connection opened but PRAGMA commands failed. Continuing without PRAGMA changes.");
        }
    }
}
