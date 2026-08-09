using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace NzbWebDAV.Database.Backup;

/// <summary>
/// Streams a logical <c>.sql</c> dump of a SQLite database in a format compatible
/// with <c>sqlite3 .dump</c>. Safe to run against a live WAL database — uses a
/// deferred read transaction for snapshot isolation.
/// </summary>
public static class SqliteDumper
{
    public static async Task DumpAsync(
        string databaseFilePath,
        Stream output,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseFilePath);
        ArgumentNullException.ThrowIfNull(output);

        if (!File.Exists(databaseFilePath))
            throw new FileNotFoundException("SQLite database file not found.", databaseFilePath);

        await using var connection = await OpenConnectionAsync(databaseFilePath, cancellationToken).ConfigureAwait(false);

        await using var begin = connection.CreateCommand();
        begin.CommandText = "BEGIN;";
        await begin.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
            await writer.WriteLineAsync("PRAGMA foreign_keys=OFF;").ConfigureAwait(false);
            await writer.WriteLineAsync("BEGIN TRANSACTION;").ConfigureAwait(false);

            var tables = await ListMasterObjectsAsync(connection, "table", cancellationToken).ConfigureAwait(false);
            var userTables = tables
                .Where(t => !t.Name.StartsWith("sqlite_", StringComparison.Ordinal))
                .ToList();

            for (var i = 0; i < userTables.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var table = userTables[i];
                progress?.Invoke($"Dumping table {table.Name} ({i + 1}/{userTables.Count})");

                if (!string.IsNullOrWhiteSpace(table.Sql))
                    await writer.WriteLineAsync(EnsureTrailingSemicolon(table.Sql)).ConfigureAwait(false);

                await DumpTableDataAsync(connection, writer, table.Name, cancellationToken).ConfigureAwait(false);
            }

            if (tables.Any(t => t.Name == "sqlite_sequence"))
            {
                progress?.Invoke("Dumping sqlite_sequence");
                await writer.WriteLineAsync("DELETE FROM sqlite_sequence;").ConfigureAwait(false);
                await DumpTableDataAsync(connection, writer, "sqlite_sequence", cancellationToken).ConfigureAwait(false);
            }

            foreach (var type in new[] { "index", "trigger", "view" })
            {
                var objects = await ListMasterObjectsAsync(connection, type, cancellationToken).ConfigureAwait(false);
                foreach (var obj in objects)
                {
                    if (string.IsNullOrWhiteSpace(obj.Sql))
                        continue; // skip autoindexes

                    progress?.Invoke($"Dumping {type} {obj.Name}");
                    await writer.WriteLineAsync(EnsureTrailingSemicolon(obj.Sql)).ConfigureAwait(false);
                }
            }

            await writer.WriteLineAsync("COMMIT;").ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await using var end = connection.CreateCommand();
            end.CommandText = "COMMIT;";
            await end.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public static async Task DumpToFileAsync(
        string databaseFilePath,
        string sqlFilePath,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(sqlFilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using var stream = new FileStream(
            sqlFilePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        await DumpAsync(databaseFilePath, stream, progress, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(
        string databaseFilePath,
        CancellationToken cancellationToken)
    {
        var readOnly = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFilePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString();

        var connection = new SqliteConnection(readOnly);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        // Readonly open can fail when a WAL DB needs -shm created; fall back to read-write.
        var readWrite = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFilePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString();

        connection = new SqliteConnection(readWrite);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<List<MasterObject>> ListMasterObjectsAsync(
        SqliteConnection connection,
        string type,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT name, sql
            FROM sqlite_master
            WHERE type = $type
            ORDER BY rowid;
            """;
        cmd.Parameters.AddWithValue("$type", type);

        var results = new List<MasterObject>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new MasterObject(
                reader.GetString(0),
                await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(1)));
        }

        return results;
    }

    private static async Task DumpTableDataAsync(
        SqliteConnection connection,
        StreamWriter writer,
        string tableName,
        CancellationToken cancellationToken)
    {
        var quotedTable = QuoteIdent(tableName);
        var columns = await GetColumnNamesAsync(connection, tableName, cancellationToken).ConfigureAwait(false);
        if (columns.Count == 0)
            return;

        var quotedColumns = string.Join(",", columns.Select(QuoteIdent));

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {quotedTable};";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var valueBuffer = new object[columns.Count];
        var sb = new StringBuilder();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            reader.GetValues(valueBuffer);
            sb.Clear();
            sb.Append("INSERT INTO ").Append(quotedTable)
                .Append(" (").Append(quotedColumns).Append(") VALUES (");

            for (var i = 0; i < valueBuffer.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(FormatValue(valueBuffer[i]));
            }

            sb.Append(");");
            await writer.WriteLineAsync(sb.ToString()).ConfigureAwait(false);
        }
    }

    private static async Task<List<string>> GetColumnNamesAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        // PRAGMA table_info does not accept bound parameters for the table name.
        cmd.CommandText = $"PRAGMA table_info({QuoteIdent(tableName)});";

        var columns = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            columns.Add(reader.GetString(1));

        return columns;
    }

    internal static string FormatValue(object? value) => value switch
    {
        null or DBNull => "NULL",
        long l => l.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        short s => s.ToString(CultureInfo.InvariantCulture),
        byte b => b.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("G17", CultureInfo.InvariantCulture),
        float f => ((double)f).ToString("G17", CultureInfo.InvariantCulture),
        decimal m => ((double)m).ToString("G17", CultureInfo.InvariantCulture),
        string s => QuoteString(s),
        byte[] bytes => FormatBlob(bytes),
        bool boolean => boolean ? "1" : "0",
        DateTime dt => QuoteString(dt.ToString("O", CultureInfo.InvariantCulture)),
        Guid guid => QuoteString(guid.ToString()),
        _ => QuoteString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
    };

    private static string FormatBlob(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2 + 3);
        sb.Append("X'");
        foreach (var b in bytes)
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        sb.Append('\'');
        return sb.ToString();
    }

    private static string QuoteString(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string QuoteIdent(string name) =>
        "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string EnsureTrailingSemicolon(string sql) =>
        sql.TrimEnd().EndsWith(';') ? sql.TrimEnd() : sql.TrimEnd() + ";";

    private sealed record MasterObject(string Name, string? Sql);
}
