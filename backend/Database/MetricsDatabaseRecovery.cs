using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Extensions;
using Serilog;

namespace NzbWebDAV.Database;

/// <summary>
/// Self-heal for the metrics database. Unlike the main database it is rebuildable
/// and disposable (see <see cref="DatabaseIntegrityCheck"/>), so a corrupt file is
/// quarantined next to itself as <c>metrics.sqlite.corrupt-&lt;utc&gt;</c> and a
/// fresh one is created, instead of every metrics service failing against it until
/// an operator notices.
///
/// Motivation: a host filesystem crash left metrics.sqlite with a malformed page
/// tree. The backend kept starting, but <see cref="Services.Metrics.MetricsWriter"/>
/// retried its flush in a hot loop (~10 warnings/s, one core pegged) for hours and
/// every read that touched metrics failed.
/// </summary>
internal static class MetricsDatabaseRecovery
{
    private const int MaxLoggedFindings = 5;

    /// <summary>
    /// Runs PRAGMA quick_check on the database behind <paramref name="context"/>.
    /// When the file is corrupt, or not a SQLite database at all, it is quarantined
    /// and the context's connection is closed so the next open creates a fresh file
    /// (a following MigrateAsync rebuilds the schema). Returns true when a quarantine
    /// happened. Never throws and never blocks startup.
    /// </summary>
    public static async Task<bool> QuarantineIfCorruptAsync(
        MetricsDbContext context,
        CancellationToken cancellationToken = default)
    {
        var databasePath = context.Database.GetDbConnection().DataSource;
        if (string.IsNullOrEmpty(databasePath) || !File.Exists(databasePath))
            return false;

        List<string> findings;
        try
        {
            // Table-valued pragma form so EF can materialize the scalar result.
            findings = (await context.Database
                .SqlQueryRaw<string>("SELECT quick_check AS Value FROM pragma_quick_check")
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
                .Where(x => !string.Equals(x, "ok", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (findings.Count == 0)
                return false;
        }
        catch (Exception ex) when (IsUnusableDatabase(ex))
        {
            // The file is so damaged that quick_check itself could not run.
            findings = [ex.GetBaseException().Message];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A diagnostic must never break startup.
            Log.Warning(ex, "Metrics database integrity check could not run; continuing.");
            return false;
        }

        await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        return Quarantine(databasePath, findings);
    }

    /// <summary>
    /// True for SQLITE_CORRUPT (11) and SQLITE_NOTADB (26). Both mean the file can
    /// never be written again and must be replaced; busy/locked/disk errors are
    /// deliberately excluded because they can heal on retry.
    /// </summary>
    public static bool IsUnusableDatabase(Exception exception)
    {
        if (exception.IsDatabaseCorruptionException())
            return true;

        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is SqliteException { SqliteErrorCode: 26 })
                return true;
        }

        return false;
    }

    /// <summary>
    /// Moves the database and its WAL/SHM sidecars out of the way. Pooled
    /// connections are cleared first so no handle keeps the old inode alive; the
    /// sidecars travel with the file so the quarantined copy stays inspectable.
    /// </summary>
    internal static bool Quarantine(string databasePath, IReadOnlyList<string> findings)
    {
        var target = databasePath + QuarantineSuffix(DateTimeOffset.UtcNow);
        var primaryMoved = false;
        try
        {
            SqliteConnection.ClearAllPools();
            File.Move(databasePath, target);
            primaryMoved = true;
            foreach (var sidecar in new[] { "-wal", "-shm", "-journal" })
            {
                if (File.Exists(databasePath + sidecar))
                    File.Move(databasePath + sidecar, target + sidecar, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!primaryMoved)
            {
                Log.Error(ex, "Metrics database at {Path} is corrupt but could not be quarantined", databasePath);
                return false;
            }

            Log.Warning(
                ex,
                "Metrics database at {Path} was quarantined, but one or more sidecars could not be moved",
                databasePath);
        }

        Log.Error(
            "Metrics database is corrupt and was quarantined to {Target}; a fresh metrics database will be created. Findings: {Findings}",
            target,
            findings.Take(MaxLoggedFindings).ToList());
        return true;
    }

    internal static string QuarantineSuffix(DateTimeOffset now) =>
        $".corrupt-{now:yyyyMMdd'T'HHmmss'Z'}";
}
