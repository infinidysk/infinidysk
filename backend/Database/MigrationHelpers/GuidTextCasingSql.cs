namespace NzbWebDAV.Database.MigrationHelpers;

/// <summary>
/// SQLite stores <see cref="Guid"/> as TEXT. Microsoft.Data.Sqlite binds parameters
/// as uppercase, but some historical SQL used <c>lower(hex(...))</c>. Comparisons are
/// case-sensitive, so mixed-case rows are invisible to EF queries.
/// <para>
/// Future SQL that generates GUIDs must emit uppercase hex (for example
/// <c>upper(hex(randomblob(...)))</c>) so it matches parameter binding. Do not use
/// <c>lower(hex(...))</c>.
/// </para>
/// </summary>
internal static class GuidTextCasingSql
{
    internal const string MigrationId = "20260820160000_Normalize-Guid-Text-Casing";

    /// <summary>
    /// Every Guid-typed TEXT column on the SQLite <c>DavDatabaseContext</c> snapshot.
    /// 18 tables / 27 columns. PostgreSQL uses native uuid and must not run this rewrite.
    /// </summary>
    internal static readonly (string Table, string[] Columns)[] GuidColumns =
    [
        ("BlobCleanupItems", ["Id"]),
        ("DavCleanupItems", ["Id"]),
        ("DavItems", ["Id", "ParentId", "HistoryItemId", "FileBlobId", "NzbBlobId"]),
        ("DavMultipartFiles", ["Id"]),
        ("DavNzbFiles", ["Id"]),
        ("DavRarFiles", ["Id"]),
        ("HealthCheckResults", ["Id", "DavItemId"]),
        ("HistoryCleanupItems", ["Id"]),
        ("HistoryItems", ["Id", "DownloadDirId", "NzbBlobId"]),
        ("ListSources", ["Id"]),
        ("NzbBlobCleanupItems", ["Id"]),
        ("NzbNames", ["Id"]),
        ("NzbResolutionGroups", ["Id"]),
        ("Par2RepairJobs", ["Id", "DavItemId"]),
        ("QueueItems", ["Id"]),
        ("QueueNzbContents", ["Id"]),
        ("WantedItems", ["Id"]),
        ("WatchdogEntries", ["ClickId", "QueueItemId"]),
    ];

    internal static IEnumerable<string> PrimaryKeyTables =>
        GuidColumns
            .Where(entry => entry.Columns[0] == "Id")
            .Select(entry => entry.Table);

    internal const string DedupeCleanupItemsSql = """
        DELETE FROM BlobCleanupItems
        WHERE rowid IN (
            SELECT rowid FROM (
                SELECT d.rowid
                FROM BlobCleanupItems AS d
                WHERE EXISTS (
                    SELECT 1 FROM BlobCleanupItems AS keep
                    WHERE keep.rowid < d.rowid
                      AND upper(keep.Id) = upper(d.Id)
                )
            )
        );

        DELETE FROM DavCleanupItems
        WHERE rowid IN (
            SELECT rowid FROM (
                SELECT d.rowid
                FROM DavCleanupItems AS d
                WHERE EXISTS (
                    SELECT 1 FROM DavCleanupItems AS keep
                    WHERE keep.rowid < d.rowid
                      AND upper(keep.Id) = upper(d.Id)
                )
            )
        );

        UPDATE HistoryCleanupItems
        SET DeleteMountedFiles = 1
        WHERE rowid IN (
            SELECT keep.rowid
            FROM HistoryCleanupItems AS keep
            WHERE keep.rowid = (
                SELECT MIN(m.rowid)
                FROM HistoryCleanupItems AS m
                WHERE upper(m.Id) = upper(keep.Id)
            )
            AND EXISTS (
                SELECT 1
                FROM HistoryCleanupItems AS other
                WHERE upper(other.Id) = upper(keep.Id)
                  AND other.DeleteMountedFiles != 0
            )
        );

        DELETE FROM HistoryCleanupItems
        WHERE rowid IN (
            SELECT rowid FROM (
                SELECT d.rowid
                FROM HistoryCleanupItems AS d
                WHERE EXISTS (
                    SELECT 1 FROM HistoryCleanupItems AS keep
                    WHERE keep.rowid < d.rowid
                      AND upper(keep.Id) = upper(d.Id)
                )
            )
        );

        DELETE FROM NzbBlobCleanupItems
        WHERE rowid IN (
            SELECT rowid FROM (
                SELECT d.rowid
                FROM NzbBlobCleanupItems AS d
                WHERE EXISTS (
                    SELECT 1 FROM NzbBlobCleanupItems AS keep
                    WHERE keep.rowid < d.rowid
                      AND upper(keep.Id) = upper(d.Id)
                )
            )
        );
        """;

    internal static string AbortIfDuplicateIdsSql
    {
        get
        {
            var raises = string.Join("\n\n          ", PrimaryKeyTables.Select(table =>
                $"""
                 SELECT RAISE(ABORT, 'Normalize-Guid-Text-Casing: duplicate {table}.Id values differ only by case')
                          WHERE EXISTS (
                              SELECT 1 FROM {table} GROUP BY upper(Id) HAVING COUNT(*) > 1
                          );
                 """));

            return $"""
                CREATE TEMP TABLE guid_casing_guard (x INTEGER PRIMARY KEY);
                CREATE TEMP TRIGGER TR_NormalizeGuidTextCasing_AbortIfDuplicateIds
                BEFORE INSERT ON guid_casing_guard
                BEGIN
                  {raises}
                END;
                INSERT INTO guid_casing_guard (x) VALUES (1);
                DROP TRIGGER IF EXISTS TR_NormalizeGuidTextCasing_AbortIfDuplicateIds;
                DROP TABLE IF EXISTS guid_casing_guard;
                """;
        }
    }

    internal const string RenameParentNameCollisionsSql = """
        UPDATE DavItems
        SET Name = CASE
            WHEN length(Name) + 8 > 255
                THEN substr(Name, 1, 247) || ' (' || substr(upper(Id), 1, 5) || ')'
            ELSE Name || ' (' || substr(upper(Id), 1, 5) || ')'
        END
        WHERE rowid IN (
            SELECT rowid FROM (
                SELECT d.rowid
                FROM DavItems AS d
                WHERE EXISTS (
                    SELECT 1
                    FROM DavItems AS keep
                    WHERE keep.rowid < d.rowid
                      AND keep.Name = d.Name
                      AND upper(keep.ParentId) IS upper(d.ParentId)
                )
            )
        );
        """;

    internal static IEnumerable<string> UppercaseTableStatements =>
        GuidColumns.Select(entry => BuildRewriteSql(entry.Table, entry.Columns, toUpper: true));

    internal static string LowercaseAllSql =>
        string.Join("\n", GuidColumns.Select(entry => BuildRewriteSql(entry.Table, entry.Columns, toUpper: false)));

    private static string BuildRewriteSql(string table, string[] columns, bool toUpper)
    {
        var fn = toUpper ? "upper" : "lower";
        var assignments = columns.Select(column => $"{column} = {fn}({column})").ToList();
        if (toUpper && table == "DavItems")
            assignments.Add("IdPrefix = lower(substr(upper(Id), 1, 5))");

        var predicates = columns.Select(column =>
            $"{column} IS NOT NULL AND {column} <> {fn}({column})");

        return $"""
            UPDATE {table}
            SET {string.Join(",\n    ", assignments)}
            WHERE {string.Join("\n   OR ", predicates)};
            """;
    }
}
