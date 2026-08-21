using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace NzbWebDAV.Database;

/// <summary>
/// Shared checks used during process startup / --db-migration before the schema is known to exist.
/// </summary>
internal static class DatabaseStartupGuards
{
    /// <summary>
    /// True when the operational database has a <c>ConfigItems</c> table.
    /// Fresh / WAL-created empty files do not, so callers must not query config yet.
    /// </summary>
    public static Task<bool> ConfigItemsTableExistsAsync(
        DbContext databaseContext,
        CancellationToken cancellationToken = default) =>
        TableExistsAsync(databaseContext, "ConfigItems", cancellationToken);

    public static async Task<bool> TableExistsAsync(
        DbContext databaseContext,
        string tableName,
        CancellationToken cancellationToken = default)
    {
        if (databaseContext.Database.IsNpgsql())
        {
            // quote_ident double-quotes the (case-sensitive) table name so to_regclass
            // resolves EF Core's quoted identifiers instead of folding to lowercase.
            // The result alias must stay quoted: EF wraps SqlQuery in a subselect and
            // references s."Value" case-sensitively on PostgreSQL.
            var exists = await databaseContext.Database
                .SqlQuery<bool>($"SELECT to_regclass(quote_ident({tableName})) IS NOT NULL AS \"Value\"")
                .SingleAsync(cancellationToken)
                .ConfigureAwait(false);
            return exists;
        }

        var count = await databaseContext.Database
            .SqlQuery<int>(
                $"""
                SELECT COUNT(*) AS Value
                FROM sqlite_master
                WHERE type = 'table' AND name = {tableName}
                """)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        return count > 0;
    }

    /// <summary>
    /// Clears EF's SQLite migration-lock row after the caller has acquired the
    /// crash-safe <see cref="DatabaseMigrationLease"/>.
    /// </summary>
    public static async Task ClearAbandonedMigrationLockAsync(
        DbContext databaseContext,
        CancellationToken cancellationToken = default)
    {
        if (!await TableExistsAsync(databaseContext, "__EFMigrationsLock", cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        var timestamps = await databaseContext.Database
            .SqlQueryRaw<string>(
                """
                SELECT "Timestamp" AS Value
                FROM "__EFMigrationsLock"
                WHERE "Id" = 1
                LIMIT 1
                """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (timestamps.Count == 0)
            return;

        var cleared = await databaseContext.Database
            .ExecuteSqlRawAsync("DELETE FROM \"__EFMigrationsLock\"", cancellationToken)
            .ConfigureAwait(false);
        if (cleared == 0)
            return;

        var timestamp = timestamps[0];
        if (DateTimeOffset.TryParse(
                timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var acquiredAt))
        {
            var age = DateTimeOffset.UtcNow - acquiredAt;
            if (age < TimeSpan.Zero)
                age = TimeSpan.Zero;
            Log.Warning(
                "Cleared {Count} abandoned EF migration lock row(s) acquired at {Timestamp} "
                + "({Age} old); the prior migration did not exit cleanly",
                cleared,
                acquiredAt,
                age);
        }
        else
        {
            Log.Warning(
                "Cleared {Count} abandoned EF migration lock row(s) with timestamp {Timestamp}; "
                + "the prior migration did not exit cleanly",
                cleared,
                timestamp);
        }
    }
}
