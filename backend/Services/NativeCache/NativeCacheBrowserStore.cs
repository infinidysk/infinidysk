using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NzbWebDAV.Services.NativeCache;

public sealed record NativeCacheBrowserFile(string Key, string FolderId, string ItemId, string? DisplayName,
    long Length, long AllocatedBytes, long VerifiedBytes, bool Pinned, long AccessBucket, long? AccessCount);
public sealed record NativeCacheBrowserPage(IReadOnlyList<NativeCacheBrowserFile> Items, long TotalCount, string? NextCursor);
public sealed record NativeCacheFolderTotals(string FolderId, long LiveFiles, long EmptyEntries, long VerifiedBlocks,
    long RetiredEntries, long RetiredBytes);
public sealed record NativeCacheBrowserTotals(long LiveFiles, long EmptyEntries, long VerifiedBlocks,
    long RetiredEntries, long RetiredBytes, IReadOnlyList<NativeCacheFolderTotals> Folders);
public sealed record NativeCacheEviction(long Id, string Key, string FolderId, string ItemId, string? DisplayName,
    long Length, long AllocatedBytes, long VerifiedBytes, long? AccessCount, long LastAccessUnix, string Reason, long EvictedUnix);
public sealed record NativeCacheEvictionPage(IReadOnlyList<NativeCacheEviction> Items, long TotalCount, long? NextCursor);

public sealed partial class NativeCacheStore
{
    private static string SafeDisplayName(string? name) => string.IsNullOrWhiteSpace(name) ? "" : name.Length <= 512 ? name : name[..512];

    private void EnsureBrowserSchema()
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        using (var command = Command("PRAGMA table_info(Entries)"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) columns.Add(reader.GetString(1));
        if (!columns.Contains("DisplayName")) Execute("ALTER TABLE Entries ADD COLUMN DisplayName TEXT NOT NULL DEFAULT ''");
        if (!columns.Contains("AccessCount")) Execute("ALTER TABLE Entries ADD COLUMN AccessCount INTEGER");
        var retiredColumns = new HashSet<string>(StringComparer.Ordinal);
        using (var command = Command("PRAGMA table_info(RetiredEntries)"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) retiredColumns.Add(reader.GetString(1));
        if (!retiredColumns.Contains("ItemId")) Execute("ALTER TABLE RetiredEntries ADD COLUMN ItemId TEXT NOT NULL DEFAULT ''");
        if (!retiredColumns.Contains("DisplayName")) Execute("ALTER TABLE RetiredEntries ADD COLUMN DisplayName TEXT NOT NULL DEFAULT ''");
        if (!retiredColumns.Contains("Length")) Execute("ALTER TABLE RetiredEntries ADD COLUMN Length INTEGER NOT NULL DEFAULT 0");
        if (!retiredColumns.Contains("VerifiedBytes")) Execute("ALTER TABLE RetiredEntries ADD COLUMN VerifiedBytes INTEGER NOT NULL DEFAULT 0");
        if (!retiredColumns.Contains("AccessCount")) Execute("ALTER TABLE RetiredEntries ADD COLUMN AccessCount INTEGER");
        if (!retiredColumns.Contains("LastAccessUnix")) Execute("ALTER TABLE RetiredEntries ADD COLUMN LastAccessUnix INTEGER NOT NULL DEFAULT 0");
        Execute("""
            CREATE INDEX IF NOT EXISTS EntryBrowserRecent ON Entries(Access DESC,Key DESC);
            CREATE INDEX IF NOT EXISTS EntryBrowserName ON Entries(DisplayName COLLATE NOCASE,Key);
            CREATE INDEX IF NOT EXISTS EntryUnnamed ON Entries(ItemId) WHERE DisplayName='';
            CREATE TABLE IF NOT EXISTS CacheEvictions(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, Key TEXT NOT NULL, Folder TEXT NOT NULL,
                ItemId TEXT NOT NULL, DisplayName TEXT NOT NULL, Length INTEGER NOT NULL,
                AllocatedBytes INTEGER NOT NULL, VerifiedBytes INTEGER NOT NULL,
                AccessCount INTEGER, LastAccessUnix INTEGER NOT NULL, Reason TEXT NOT NULL,
                EvictedUnix INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS CacheEvictionTime ON CacheEvictions(EvictedUnix DESC,Id DESC);
            CREATE INDEX IF NOT EXISTS CacheEvictionReason ON CacheEvictions(Reason,EvictedUnix DESC,Id DESC);
            CREATE TABLE IF NOT EXISTS CacheTraffic(
                Bucket INTEGER PRIMARY KEY, HitBlocks INTEGER NOT NULL, HitBytes INTEGER NOT NULL,
                MissBlocks INTEGER NOT NULL, MissBytes INTEGER NOT NULL, CommittedBytes INTEGER NOT NULL);
            """);
    }

    [SuppressMessage("Performance", "CA1849", Justification = LocalSqliteReason)]
    public async Task<NativeCacheBrowserTotals> GetBrowserTotalsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var command = Command("""
                SELECT e.Folder,
                  SUM(CASE WHEN e.VerifiedBytes>0 THEN 1 ELSE 0 END),
                  SUM(CASE WHEN e.VerifiedBytes=0 THEN 1 ELSE 0 END),
                  (SELECT COUNT(*) FROM Blocks b JOIN Entries owner ON owner.Key=b.Key WHERE owner.Folder=e.Folder)
                FROM Entries e GROUP BY e.Folder
                """);
            var rows = new Dictionary<string, (long Live, long Empty, long Blocks)>(StringComparer.Ordinal);
            using (var reader = command.ExecuteReader())
                while (reader.Read()) rows[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
            using var retired = Command("SELECT Folder,COUNT(*),COALESCE(SUM(Bytes),0) FROM RetiredEntries GROUP BY Folder");
            var debts = new Dictionary<string, (long Count, long Bytes)>(StringComparer.Ordinal);
            using (var reader = retired.ExecuteReader())
                while (reader.Read()) debts[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt64(2));
            var folders = _folders.Select(folder =>
            {
                var row = rows.GetValueOrDefault(folder.Id);
                var debt = debts.GetValueOrDefault(folder.Id);
                return new NativeCacheFolderTotals(folder.Id, row.Live, row.Empty, row.Blocks, debt.Count, debt.Bytes);
            }).ToArray();
            return new(folders.Sum(x => x.LiveFiles), folders.Sum(x => x.EmptyEntries),
                folders.Sum(x => x.VerifiedBlocks), folders.Sum(x => x.RetiredEntries),
                folders.Sum(x => x.RetiredBytes), folders);
        }
        finally { _gate.Release(); }
    }

    private sealed record BrowserCursor(string Sort, string Value, string Key);
    private static BrowserCursor? DecodeCursor(string? cursor, string sort)
    {
        if (string.IsNullOrEmpty(cursor)) return null;
        if (cursor.Length > 1024) throw new ArgumentException("Invalid file cursor.");
        try
        {
            var value = JsonSerializer.Deserialize<BrowserCursor>(Encoding.UTF8.GetString(Convert.FromBase64String(cursor)));
            if (value is not { Sort: not null, Value: not null, Key: not null } || value.Sort != sort
                || value.Key.Length != 64 || !value.Key.All(char.IsAsciiHexDigit) || value.Value.Length > 512)
                throw new ArgumentException("Invalid file cursor.");
            if (sort == "recent" && !long.TryParse(value.Value, out _)) throw new ArgumentException("Invalid file cursor.");
            if (sort == "coverage" && (!double.TryParse(value.Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || number is < 0 or > 100))
                throw new ArgumentException("Invalid file cursor.");
            return value;
        }
        catch (Exception e) when (e is FormatException or JsonException) { throw new ArgumentException("Invalid file cursor.", e); }
    }

    [SuppressMessage("Performance", "CA1849", Justification = LocalSqliteReason)]
    public async Task<NativeCacheBrowserPage> BrowseFilesAsync(string? folderId, string? search, string coverage,
        string sort, string? cursor, int limit, CancellationToken ct = default)
    {
        if (limit is < 1 or > 100 || coverage is not ("all" or "complete" or "partial" or "empty")
            || sort is not ("recent" or "name" or "coverage") || search?.Length > 128
            || (folderId is not null && !_folders.Any(folder => folder.Id == folderId)))
            throw new ArgumentException("Invalid native cache file filters.");
        var decoded = DecodeCursor(cursor, sort);
        var order = sort switch
        {
            "name" => "e.DisplayName COLLATE NOCASE ASC,e.Key ASC",
            "coverage" => "(e.VerifiedBytes*100.0/e.Length) DESC,e.Key DESC",
            _ => "e.Access DESC,e.Key DESC"
        };
        var cursorSql = decoded is null ? "" : sort switch
        {
            "name" => " AND (e.DisplayName COLLATE NOCASE>$cursorValue OR (e.DisplayName COLLATE NOCASE=$cursorValue AND e.Key>$cursorKey))",
            "coverage" => " AND ((e.VerifiedBytes*100.0/e.Length)<$cursorNumber OR ((e.VerifiedBytes*100.0/e.Length)=$cursorNumber AND e.Key<$cursorKey))",
            _ => " AND (e.Access<$cursorNumber OR (e.Access=$cursorNumber AND e.Key<$cursorKey))"
        };
        var escaped = (search ?? "").Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
        var where = "e.Length>0 AND ($folder='' OR e.Folder=$folder) AND ($search='' OR e.DisplayName LIKE $searchPattern ESCAPE '\\' OR e.ItemId LIKE $searchPattern ESCAPE '\\')"
            + (coverage switch
            {
                "complete" => " AND e.VerifiedBytes>=e.Length",
                "partial" => " AND e.VerifiedBytes>0 AND e.VerifiedBytes<e.Length",
                "empty" => " AND e.VerifiedBytes=0",
                _ => " AND e.VerifiedBytes>0"
            });
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var count = Command($"SELECT COUNT(*) FROM Entries e WHERE {where}",
                ("$folder", folderId ?? ""), ("$search", escaped), ("$searchPattern", "%" + escaped + "%"));
            var total = (long)(count.ExecuteScalar() ?? 0L);
            using var query = Command($"""
                SELECT e.Key,e.Folder,e.ItemId,e.DisplayName,e.Length,e.Bytes,e.VerifiedBytes,e.Pinned,e.Access,e.AccessCount
                FROM Entries e WHERE {where}{cursorSql} ORDER BY {order} LIMIT $limit
                """, ("$folder", folderId ?? ""), ("$search", escaped), ("$searchPattern", "%" + escaped + "%"),
                ("$cursorValue", decoded?.Value ?? ""), ("$cursorNumber", decoded is null || sort == "name" ? 0d : double.Parse(decoded.Value, System.Globalization.CultureInfo.InvariantCulture)),
                ("$cursorKey", decoded?.Key ?? ""), ("$limit", limit + 1));
            var items = new List<NativeCacheBrowserFile>(limit + 1);
            using (var reader = query.ExecuteReader())
                while (reader.Read()) items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7) != 0, reader.GetInt64(8),
                    reader.IsDBNull(9) ? null : reader.GetInt64(9)));
            var hasMore = items.Count > limit;
            if (hasMore) items.RemoveAt(items.Count - 1);
            var last = items.LastOrDefault();
            var value = last is null ? null : sort switch
            {
                "name" => last.DisplayName ?? "",
                "coverage" => (last.VerifiedBytes * 100.0 / last.Length).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                _ => last.AccessBucket.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            var next = hasMore && last is not null ? Convert.ToBase64String(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new BrowserCursor(sort, value!, last.Key)))) : null;
            return new(items, total, next);
        }
        finally { _gate.Release(); }
    }

    [SuppressMessage("Performance", "CA1849", Justification = LocalSqliteReason)]
    public async Task<IReadOnlyList<NativeCacheBrowserFile>> GetRecentFilesAsync(int limit = 10, CancellationToken ct = default)
    {
        if (limit is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(limit));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var query = Command("SELECT Key,Folder,ItemId,DisplayName,Length,Bytes,VerifiedBytes,Pinned,Access,AccessCount FROM Entries WHERE VerifiedBytes>0 ORDER BY Access DESC,Key DESC LIMIT $limit", ("$limit", limit));
            var rows = new List<NativeCacheBrowserFile>();
            using var reader = query.ExecuteReader();
            while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4),
                reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7) != 0, reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetInt64(9)));
            return rows;
        }
        finally { _gate.Release(); }
    }

    [SuppressMessage("Performance", "CA1849", Justification = LocalSqliteReason)]
    public async Task<IReadOnlyList<string>> GetUnnamedItemIdsAsync(int limit = 100, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var query = Command("SELECT DISTINCT ItemId FROM Entries WHERE DisplayName='' AND ItemId<>'' LIMIT $limit", ("$limit", limit));
            var ids = new List<string>();
            using var reader = query.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetString(0));
            return ids;
        }
        finally { _gate.Release(); }
    }

    [SuppressMessage("Performance", "CA1849", Justification = LocalSqliteReason)]
    public async Task UpdateDisplayNamesAsync(IReadOnlyDictionary<string, string> names, CancellationToken ct = default)
    {
        if (names.Count > 100) throw new ArgumentException("Too many cache names in one batch.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var (id, name) in names)
                Execute("UPDATE Entries SET DisplayName=$name WHERE ItemId=$item AND DisplayName=''", ("$name", SafeDisplayName(name)), ("$item", id));
        }
        finally { _gate.Release(); }
    }

    private void DeleteEntryWithEviction(string key, string reason)
    {
        using var transaction = _database.BeginTransaction();
        using (var log = Command("""
            INSERT INTO CacheEvictions(Key,Folder,ItemId,DisplayName,Length,AllocatedBytes,VerifiedBytes,AccessCount,LastAccessUnix,Reason,EvictedUnix)
            SELECT Key,Folder,ItemId,DisplayName,Length,Bytes,VerifiedBytes,AccessCount,Access*300,$reason,$at FROM Entries WHERE Key=$key
            """, ("$key", key), ("$reason", reason), ("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds())))
        { log.Transaction = transaction; log.ExecuteNonQuery(); }
        using (var remove = Command("DELETE FROM Entries WHERE Key=$key", ("$key", key)))
        { remove.Transaction = transaction; remove.ExecuteNonQuery(); }
        transaction.Commit();
    }

    private void DeleteRetiredWithEviction(string key, string folder, string reason)
    {
        using var transaction = _database.BeginTransaction();
        using (var log = Command("""
            INSERT INTO CacheEvictions(Key,Folder,ItemId,DisplayName,Length,AllocatedBytes,VerifiedBytes,AccessCount,LastAccessUnix,Reason,EvictedUnix)
            SELECT Key,Folder,ItemId,DisplayName,Length,Bytes,VerifiedBytes,AccessCount,LastAccessUnix,$reason,$at
            FROM RetiredEntries WHERE Key=$key AND Folder=$folder
            """, ("$key", key), ("$folder", folder), ("$reason", reason), ("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds())))
        { log.Transaction = transaction; log.ExecuteNonQuery(); }
        using (var remove = Command("DELETE FROM RetiredEntries WHERE Key=$key AND Folder=$folder", ("$key", key), ("$folder", folder)))
        { remove.Transaction = transaction; remove.ExecuteNonQuery(); }
        transaction.Commit();
    }

    [SuppressMessage("Performance", "CA1849", Justification = LocalSqliteReason)]
    public async Task<NativeCacheEvictionPage> GetEvictionsAsync(string? search, string? reason, long? cursor, int limit, CancellationToken ct = default)
    {
        if (limit is < 1 or > 100 || search?.Length > 128 || reason is not (null or "pressure" or "age" or "clear" or "relocation") || cursor < 0)
            throw new ArgumentException("Invalid eviction history filters.");
        var term = (search ?? "").Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
        var where = "($reason='' OR Reason=$reason) AND ($search='' OR DisplayName LIKE $pattern ESCAPE '\\')";
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var count = Command($"SELECT COUNT(*) FROM CacheEvictions WHERE {where}", ("$reason", reason ?? ""), ("$search", term), ("$pattern", "%" + term + "%"));
            var total = (long)(count.ExecuteScalar() ?? 0L);
            using var query = Command($"SELECT Id,Key,Folder,ItemId,DisplayName,Length,AllocatedBytes,VerifiedBytes,AccessCount,LastAccessUnix,Reason,EvictedUnix FROM CacheEvictions WHERE {where} AND ($cursor=0 OR Id<$cursor) ORDER BY Id DESC LIMIT $limit",
                ("$reason", reason ?? ""), ("$search", term), ("$pattern", "%" + term + "%"), ("$cursor", cursor ?? 0), ("$limit", limit + 1));
            var rows = new List<NativeCacheEviction>(limit + 1);
            using (var reader = query.ExecuteReader())
                while (reader.Read()) rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetInt64(8),
                    reader.GetInt64(9), reader.GetString(10), reader.GetInt64(11)));
            var more = rows.Count > limit;
            if (more) rows.RemoveAt(rows.Count - 1);
            return new(rows, total, more ? rows[^1].Id : null);
        }
        finally { _gate.Release(); }
    }

    [SuppressMessage("Performance", "CA1849", Justification = LocalSqliteReason)]
    public async Task SaveTrafficAsync(IReadOnlyList<NativeCacheTrafficBucket> buckets, CancellationToken ct = default)
    {
        if (buckets.Count == 0) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var transaction = _database.BeginTransaction();
            foreach (var bucket in buckets)
            {
                using var command = Command("""
                    INSERT INTO CacheTraffic(Bucket,HitBlocks,HitBytes,MissBlocks,MissBytes,CommittedBytes)
                    VALUES($bucket,$hits,$hitBytes,$misses,$missBytes,$committed)
                    ON CONFLICT(Bucket) DO UPDATE SET HitBlocks=HitBlocks+excluded.HitBlocks,HitBytes=HitBytes+excluded.HitBytes,
                    MissBlocks=MissBlocks+excluded.MissBlocks,MissBytes=MissBytes+excluded.MissBytes,CommittedBytes=CommittedBytes+excluded.CommittedBytes
                    """, ("$bucket", bucket.Bucket), ("$hits", bucket.HitBlocks), ("$hitBytes", bucket.HitBytes),
                    ("$misses", bucket.MissBlocks), ("$missBytes", bucket.MissBytes), ("$committed", bucket.CommittedBytes));
                command.Transaction = transaction;
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        finally { _gate.Release(); }
    }

    [SuppressMessage("Performance", "CA1849", Justification = LocalSqliteReason)]
    public async Task<NativeCacheTrafficBucket> GetTraffic24hAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var since = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 86400) / 300;
            using var query = Command("SELECT COALESCE(SUM(HitBlocks),0),COALESCE(SUM(HitBytes),0),COALESCE(SUM(MissBlocks),0),COALESCE(SUM(MissBytes),0),COALESCE(SUM(CommittedBytes),0) FROM CacheTraffic WHERE Bucket>=$since", ("$since", since));
            using var reader = query.ExecuteReader();
            reader.Read();
            return new(since, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
        }
        finally { _gate.Release(); }
    }

    [SuppressMessage("Performance", "CA1849", Justification = LocalSqliteReason)]
    public async Task<long?> GetFirstTrafficBucketAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var query = Command("SELECT MIN(Bucket) FROM CacheTraffic WHERE Bucket >= $since",
                ("$since", (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 86400) / 300));
            return query.ExecuteScalar() is long bucket ? bucket : null;
        }
        finally { _gate.Release(); }
    }

    [SuppressMessage("Performance", "CA1849", Justification = LocalSqliteReason)]
    public async Task PruneBrowserHistoryAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Execute("DELETE FROM CacheTraffic WHERE Bucket<$cutoff", ("$cutoff", (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7 * 86400) / 300));
            Execute("DELETE FROM CacheEvictions WHERE Id IN (SELECT Id FROM CacheEvictions WHERE EvictedUnix<$cutoff ORDER BY Id LIMIT 500)",
                ("$cutoff", DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds()));
            Execute("DELETE FROM CacheEvictions WHERE Id IN (SELECT Id FROM CacheEvictions ORDER BY Id DESC LIMIT -1 OFFSET 10000)");
        }
        finally { _gate.Release(); }
    }
}
