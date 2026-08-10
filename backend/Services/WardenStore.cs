using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

public partial class WardenStore
{
    public const string LocalSourceId = "local";
    public const string TrustFull = "full";
    public const string TrustCorroborate = "corroborate";
    public const string TrustObserve = "observe";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    [GeneratedRegex("^wd1:[0-9a-f]{32}$")]
    private static partial Regex FpPattern();

    public static bool IsValidFp(string? fp) => fp is not null && FpPattern().IsMatch(fp);

    private readonly string _connectionString;
    private readonly ConfigManager _configManager;

    public WardenStore(ConfigManager configManager)
    {
        _configManager = configManager;
        var path = Path.Join(DavDatabaseContext.ConfigPath, "warden.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        Initialize();
        TryMigrateLegacyJson();
    }

    public int Count => ScalarInt("SELECT COUNT(*) FROM warden_entries");

    public int LocalCount => ScalarInt("SELECT COUNT(*) FROM warden_entries WHERE source_id = 'local'");

    public int EffectiveCount()
    {
        var q = _configManager.GetWardenQuorum();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM (" +
                "SELECT e.fp FROM warden_entries e JOIN warden_sources s ON s.id = e.source_id " +
                "WHERE s.enabled = 1 AND s.trust IN ('full','corroborate') " +
                "GROUP BY e.fp HAVING MAX(s.trust = 'full') = 1 OR COUNT(*) >= $q)";
            cmd.Parameters.AddWithValue("$q", q);
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
        catch (SqliteException e)
        {
            Log.Debug(e, "Warden: effective-count failed");
            return 0;
        }
    }

    public void MarkDead(string? fp)
    {
        if (!IsValidFp(fp)) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            using var conn = Open();
            var existing = "";
            using (var sel = conn.CreateCommand())
            {
                sel.CommandText = "SELECT backbones FROM warden_entries WHERE source_id = 'local' AND fp = $fp";
                sel.Parameters.AddWithValue("$fp", fp);
                if (sel.ExecuteScalar() is string s) existing = s;
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO warden_entries (source_id, fp, dead_at, n, backbones) VALUES ('local', $fp, $t, 1, $bk) " +
                "ON CONFLICT(source_id, fp) DO UPDATE SET dead_at = $t, n = n + 1, backbones = $bk";
            cmd.Parameters.AddWithValue("$fp", fp);
            cmd.Parameters.AddWithValue("$t", now);
            cmd.Parameters.AddWithValue("$bk", MergeBackbones(existing, CurrentBackbones()));
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException e)
        {
            Log.Debug(e, "Warden: mark failed");
        }
    }

    public bool IsDeadAnywhere(string? fp)
    {
        if (string.IsNullOrEmpty(fp)) return false;
        var quorum = Math.Max(1, _configManager.GetWardenQuorum());
        var scope = _configManager.IsWardenBackboneScopeEnabled();
        var mine = scope
            ? new HashSet<string>(CurrentBackbones().Select(WardenFingerprint.RootDomain).Where(b => b != "unknown"), StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        if (mine.Count == 0) scope = false;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT s.trust, e.backbones, e.source_id FROM warden_entries e JOIN warden_sources s ON s.id = e.source_id " +
                "WHERE e.fp = $fp AND s.enabled = 1 AND s.trust IN ('full','corroborate')";
            cmd.Parameters.AddWithValue("$fp", fp);
            using var reader = cmd.ExecuteReader();
            var agree = 0;
            while (reader.Read())
            {
                if (scope && reader.GetString(2) != LocalSourceId
                          && !BackboneInScope(reader.IsDBNull(1) ? "" : reader.GetString(1), mine))
                    continue;
                if (reader.GetString(0) == TrustFull) return true;
                if (++agree >= quorum) return true;
            }
            return false;
        }
        catch (SqliteException e)
        {
            Log.Debug(e, "Warden: lookup failed");
            return false;
        }
    }

    public List<WardenSourceInfo> GetSources()
    {
        var list = new List<WardenSourceInfo>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT s.id, s.kind, s.name, s.url, s.enabled, s.trust, s.refresh_hours, " +
                "s.last_checked, s.last_updated, s.status, " +
                "(SELECT COUNT(*) FROM warden_entries e WHERE e.source_id = s.id) " +
                "FROM warden_sources s ORDER BY CASE WHEN s.id = 'local' THEN 0 ELSE 1 END, s.sort, s.name";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new WardenSourceInfo
                {
                    Id = reader.GetString(0),
                    Kind = reader.GetString(1),
                    Name = reader.GetString(2),
                    Url = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Enabled = reader.GetInt32(4) != 0,
                    Trust = reader.GetString(5),
                    RefreshHours = reader.GetInt32(6),
                    LastChecked = reader.GetInt64(7),
                    LastUpdated = reader.GetInt64(8),
                    Status = reader.IsDBNull(9) ? null : reader.GetString(9),
                    Count = reader.GetInt32(10),
                });
            }
        }
        catch (SqliteException e)
        {
            Log.Debug(e, "Warden: get-sources failed");
        }
        return list;
    }

    public string AddSource(string kind, string name, string? url, string trust, int refreshHours)
    {
        var id = "src_" + Guid.NewGuid().ToString("n")[..12];
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO warden_sources (id, kind, name, url, enabled, trust, refresh_hours, last_checked, last_updated, status, sort) " +
            "VALUES ($id, $kind, $name, $url, 1, $trust, $rh, 0, 0, NULL, (SELECT COALESCE(MAX(sort),0)+1 FROM warden_sources))";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$name", string.IsNullOrWhiteSpace(name) ? "Untitled" : name.Trim());
        cmd.Parameters.AddWithValue("$url", (object?)url ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$trust", NormalizeTrust(trust));
        cmd.Parameters.AddWithValue("$rh", ClampRefreshHours(refreshHours));
        cmd.ExecuteNonQuery();
        return id;
    }

    public (int added, int skipped) ImportRemoteSources(IReadOnlyList<RemoteSourceSpec> specs)
    {
        if (specs.Count == 0) return (0, 0);

        using var conn = Open();
        using var tx = conn.BeginTransaction();

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var sel = conn.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = "SELECT url FROM warden_sources WHERE url IS NOT NULL";
            using var r = sel.ExecuteReader();
            while (r.Read())
                if (!r.IsDBNull(0)) existing.Add(r.GetString(0));
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO warden_sources (id, kind, name, url, enabled, trust, refresh_hours, last_checked, last_updated, status, sort) " +
            "VALUES ($id, 'remote', $name, $url, 1, $trust, $rh, 0, 0, NULL, (SELECT COALESCE(MAX(sort),0)+1 FROM warden_sources))";
        var pId = new SqliteParameter("$id", SqliteType.Text);
        var pName = new SqliteParameter("$name", SqliteType.Text);
        var pUrl = new SqliteParameter("$url", SqliteType.Text);
        var pTrust = new SqliteParameter("$trust", SqliteType.Text);
        var pRh = new SqliteParameter("$rh", SqliteType.Integer);
        cmd.Parameters.Add(pId);
        cmd.Parameters.Add(pName);
        cmd.Parameters.Add(pUrl);
        cmd.Parameters.Add(pTrust);
        cmd.Parameters.Add(pRh);

        var added = 0;
        var skipped = 0;
        foreach (var spec in specs)
        {
            var url = spec.Url?.Trim();
            if (string.IsNullOrEmpty(url) || !existing.Add(url)) { skipped++; continue; }

            pId.Value = "src_" + Guid.NewGuid().ToString("n")[..12];
            pName.Value = string.IsNullOrWhiteSpace(spec.Name)
                ? (Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "Untitled")
                : spec.Name!.Trim();
            pUrl.Value = url;
            pTrust.Value = NormalizeTrust(spec.Trust);
            pRh.Value = ClampRefreshHours(spec.RefreshHours);
            cmd.ExecuteNonQuery();
            added++;
        }

        tx.Commit();
        return (added, skipped);
    }

    public void UpdateSource(string id, bool? enabled, string? trust, int? refreshHours, string? name)
    {
        if (id == LocalSourceId) return;
        var sets = new List<string>();
        if (enabled.HasValue) sets.Add("enabled = $enabled");
        if (trust is not null) sets.Add("trust = $trust");
        if (refreshHours.HasValue) sets.Add("refresh_hours = $rh");
        if (name is not null) sets.Add("name = $name");
        if (sets.Count == 0) return;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE warden_sources SET {string.Join(", ", sets)} WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        if (enabled.HasValue) cmd.Parameters.AddWithValue("$enabled", enabled.Value ? 1 : 0);
        if (trust is not null) cmd.Parameters.AddWithValue("$trust", NormalizeTrust(trust));
        if (refreshHours.HasValue) cmd.Parameters.AddWithValue("$rh", ClampRefreshHours(refreshHours.Value));
        if (name is not null) cmd.Parameters.AddWithValue("$name", name.Trim());
        cmd.ExecuteNonQuery();
    }

    public int ClearSource(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM warden_entries WHERE source_id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery();
    }

    public bool RemoveSource(string id)
    {
        if (id == LocalSourceId) return false;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM warden_entries WHERE source_id = $id";
            del.Parameters.AddWithValue("$id", id);
            del.ExecuteNonQuery();
        }
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM warden_sources WHERE id = $id";
            del.Parameters.AddWithValue("$id", id);
            del.ExecuteNonQuery();
        }
        tx.Commit();
        return true;
    }

    public int Clear() => ClearSource(LocalSourceId);

    public void SetSourceStatus(string id, string? etag, long lastChecked, long lastUpdated, string? status)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE warden_sources SET etag = $etag, last_checked = $lc, last_updated = $lu, status = $status WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$etag", (object?)etag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lc", lastChecked);
        cmd.Parameters.AddWithValue("$lu", lastUpdated);
        cmd.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void TouchChecked(string id, long when, string? status)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE warden_sources SET last_checked = $lc, status = $status WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$lc", when);
        cmd.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public string? GetSourceEtag(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT etag FROM warden_sources WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() as string;
    }

    public Task<int> MergeIntoLocalAsync(Stream input, CancellationToken ct) =>
        LoadIntoAsync(input, LocalSourceId, replace: false, ct);

    public async Task<(string id, int count)> ImportAsNewSourceAsync(Stream input, string name, string trust, CancellationToken ct)
    {
        var id = AddSource("imported", name, null, trust, 24);
        var count = await LoadIntoAsync(input, id, replace: true, ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SetSourceStatus(id, null, now, now, $"imported {count}");
        return (id, count);
    }

    public Task<int> ReplaceSourceAsync(string id, Stream input, CancellationToken ct) =>
        LoadIntoAsync(input, id, replace: true, ct);

    private async Task<int> LoadIntoAsync(Stream input, string sourceId, bool replace, CancellationToken ct)
    {
        var cap = _configManager.GetWardenMaxSourceEntries();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var reader = new StreamReader(input);
        using var conn = Open();
        var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        if (replace)
        {
            using var del = conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM warden_entries WHERE source_id = $sid";
            del.Parameters.AddWithValue("$sid", sourceId);
            await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        using var sel = conn.CreateCommand();
        sel.CommandText = "SELECT backbones FROM warden_entries WHERE source_id = $ssid AND fp = $sfp";
        var pSsid = new SqliteParameter("$ssid", sourceId);
        var pSfp = new SqliteParameter("$sfp", SqliteType.Text);
        sel.Parameters.Add(pSsid);
        sel.Parameters.Add(pSfp);

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO warden_entries (source_id, fp, dead_at, n, backbones) VALUES ($sid, $fp, $t, $n, $bk) " +
            "ON CONFLICT(source_id, fp) DO UPDATE SET dead_at = MAX(dead_at, $t), n = MIN(n + $n, 1000000000), backbones = $bk";
        var pSid = new SqliteParameter("$sid", sourceId);
        var pFp = new SqliteParameter("$fp", SqliteType.Text);
        var pT = new SqliteParameter("$t", SqliteType.Integer);
        var pN = new SqliteParameter("$n", SqliteType.Integer);
        var pBk = new SqliteParameter("$bk", SqliteType.Text);
        cmd.Parameters.Add(pSid);
        cmd.Parameters.Add(pFp);
        cmd.Parameters.Add(pT);
        cmd.Parameters.Add(pN);
        cmd.Parameters.Add(pBk);

        sel.Transaction = tx;
        cmd.Transaction = tx;

        var processed = 0;
        var recordsRead = 0;
        var inBatch = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (++recordsRead > WardenInputLimits.MaxRecords)
                throw new InvalidOperationException($"Source exceeds the {WardenInputLimits.MaxRecords:N0}-record processing limit.");
            if (line.Length > WardenInputLimits.MaxRecordCharacters)
                throw new InvalidOperationException($"Source contains a record longer than the {WardenInputLimits.MaxRecordCharacters:N0}-character limit.");
            if (line.Length == 0) continue;
            if (line.StartsWith("{\"warden\"", StringComparison.Ordinal)) continue;

            WardenRecord? rec;
            try { rec = JsonSerializer.Deserialize<WardenRecord>(line, JsonOptions); }
            catch (JsonException) { continue; }
            if (rec is null || !IsValidFp(rec.Fp)) continue;

            if (processed >= cap)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                await tx.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException($"Source exceeds the {cap:N0}-fingerprint limit.");
            }

            pSfp.Value = rec.Fp;
            var existing = await sel.ExecuteScalarAsync(ct).ConfigureAwait(false) is string s ? s : "";

            pFp.Value = rec.Fp;
            pT.Value = Math.Clamp(rec.DeadAt, 0, now + 86400);
            pN.Value = rec.Count <= 0 ? 1 : Math.Min(rec.Count, 1000000000);
            pBk.Value = MergeBackbones(existing, rec.Backbones ?? Array.Empty<string>());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            processed++;

            // Replacements must remain invisible until the complete source has
            // passed validation; merge imports may still commit in batches.
            if (!replace && ++inBatch >= 5000)
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
                await tx.DisposeAsync().ConfigureAwait(false);
                tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
                sel.Transaction = tx;
                cmd.Transaction = tx;
                inBatch = 0;
            }
        }

        if (replace && processed == 0)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            await tx.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("No valid fingerprints found.");
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        await tx.DisposeAsync().ConfigureAwait(false);
        return processed;
    }

    public async Task ExportToAsync(Stream output, IReadOnlyCollection<string> sourceIds, bool dedup, CancellationToken ct)
    {
        var ids = (sourceIds.Count == 0 ? new[] { LocalSourceId } : sourceIds.ToArray());
        var placeholders = string.Join(",", ids.Select((_, i) => "$s" + i));

        await using var writer = new StreamWriter(output, new UTF8Encoding(false), 1 << 16, leaveOpen: true);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await writer.WriteLineAsync($"{{\"warden\":1,\"updated\":{now}}}".AsMemory(), ct).ConfigureAwait(false);

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = dedup
            ? $"SELECT fp, MAX(dead_at), SUM(n), group_concat(backbones, ',') FROM warden_entries " +
              $"WHERE source_id IN ({placeholders}) GROUP BY fp"
            : $"SELECT fp, dead_at, n, backbones FROM warden_entries WHERE source_id IN ({placeholders})";
        for (var i = 0; i < ids.Length; i++) cmd.Parameters.AddWithValue("$s" + i, ids[i]);

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var rec = new WardenRecord
            {
                Fp = reader.GetString(0),
                DeadAt = reader.GetInt64(1),
                Count = reader.GetInt32(2),
                Backbones = SplitBackbones(reader.GetString(3)),
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(rec, JsonOptions).AsMemory(), ct).ConfigureAwait(false);
        }
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    private string[] CurrentBackbones()
    {
        try
        {
            var providers = _configManager.GetUsenetProviderConfig().Providers;
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var bk in providers
                         .Select(p => WardenFingerprint.Backbone(p.Host))
                         .Where(bk => bk != "unknown"))
            {
                set.Add(bk);
            }
            return set.Count == 0 ? new[] { "unknown" } : set.ToArray();
        }
        catch
        {
            return new[] { "unknown" };
        }
    }

    private static string NormalizeTrust(string? trust) => trust switch
    {
        TrustFull => TrustFull,
        TrustObserve => TrustObserve,
        _ => TrustCorroborate,
    };

    private static int ClampRefreshHours(int hours) => Math.Clamp(hours, 1, 24 * 30);

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private int ScalarInt(string sql)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
        catch (SqliteException e)
        {
            Log.Debug(e, "Warden: scalar failed");
            return 0;
        }
    }

    private void Initialize()
    {
        try
        {
            using var conn = Open();
            Exec(conn, "PRAGMA journal_mode=WAL;");
            Exec(conn,
                "CREATE TABLE IF NOT EXISTS warden_sources (" +
                "id TEXT PRIMARY KEY, kind TEXT NOT NULL, name TEXT NOT NULL, url TEXT, " +
                "enabled INTEGER NOT NULL DEFAULT 1, trust TEXT NOT NULL DEFAULT 'full', " +
                "refresh_hours INTEGER NOT NULL DEFAULT 24, last_checked INTEGER NOT NULL DEFAULT 0, " +
                "last_updated INTEGER NOT NULL DEFAULT 0, etag TEXT, status TEXT, sort INTEGER NOT NULL DEFAULT 0);");
            MigrateEntriesSchema(conn);
            Exec(conn,
                "CREATE TABLE IF NOT EXISTS warden_entries (" +
                "source_id TEXT NOT NULL DEFAULT 'local', fp TEXT NOT NULL, dead_at INTEGER NOT NULL, " +
                "n INTEGER NOT NULL, backbones TEXT NOT NULL DEFAULT '', PRIMARY KEY (source_id, fp));");
            Exec(conn,
                "CREATE INDEX IF NOT EXISTS ix_warden_fp ON warden_entries(fp);" +
                "CREATE INDEX IF NOT EXISTS ix_warden_dead_at ON warden_entries(dead_at);");
            Exec(conn,
                "INSERT OR IGNORE INTO warden_sources (id, kind, name, url, enabled, trust, refresh_hours) " +
                "VALUES ('local', 'local', 'My list', NULL, 1, 'full', 24);");
        }
        catch (SqliteException e)
        {
            Log.Warning(e, "Warden: initialize failed");
        }
    }

    private void MigrateEntriesSchema(SqliteConnection conn)
    {
        using (var exists = conn.CreateCommand())
        {
            exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='warden_entries'";
            if (exists.ExecuteScalar() is null) return;
        }
        using (var info = conn.CreateCommand())
        {
            info.CommandText = "SELECT 1 FROM pragma_table_info('warden_entries') WHERE name='source_id'";
            if (info.ExecuteScalar() is not null) return;
        }
        Exec(conn,
            "ALTER TABLE warden_entries RENAME TO warden_entries_legacy;" +
            "CREATE TABLE warden_entries (" +
            "source_id TEXT NOT NULL DEFAULT 'local', fp TEXT NOT NULL, dead_at INTEGER NOT NULL, " +
            "n INTEGER NOT NULL, backbones TEXT NOT NULL DEFAULT '', PRIMARY KEY (source_id, fp));" +
            "INSERT OR IGNORE INTO warden_entries (source_id, fp, dead_at, n, backbones) " +
            "SELECT 'local', fp, dead_at, n, backbones FROM warden_entries_legacy;" +
            "DROP TABLE warden_entries_legacy;");
        Log.Information("Warden: migrated entries to layered schema");
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void TryMigrateLegacyJson()
    {
        try
        {
            var jsonPath = Path.Join(DavDatabaseContext.ConfigPath, "warden.json");
            if (!File.Exists(jsonPath)) return;
            if (Count > 0) return;

            var model = JsonSerializer.Deserialize<WardenFile>(File.ReadAllText(jsonPath), JsonOptions);
            var migrated = 0;
            if (model?.Entries is not null)
            {
                using var conn = Open();
                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    "INSERT OR IGNORE INTO warden_entries (source_id, fp, dead_at, n, backbones) " +
                    "VALUES ('local', $fp, $t, $n, $bk)";
                var pFp = new SqliteParameter("$fp", SqliteType.Text);
                var pT = new SqliteParameter("$t", SqliteType.Integer);
                var pN = new SqliteParameter("$n", SqliteType.Integer);
                var pBk = new SqliteParameter("$bk", SqliteType.Text);
                cmd.Parameters.Add(pFp);
                cmd.Parameters.Add(pT);
                cmd.Parameters.Add(pN);
                cmd.Parameters.Add(pBk);
                foreach (var r in model.Entries.Where(r => IsValidFp(r.Fp)))
                {
                    pFp.Value = r.Fp;
                    pT.Value = r.DeadAt;
                    pN.Value = r.Count <= 0 ? 1 : r.Count;
                    pBk.Value = JoinBackbones(r.Backbones);
                    cmd.ExecuteNonQuery();
                    migrated++;
                }
                tx.Commit();
            }
            File.Move(jsonPath, jsonPath + ".migrated", overwrite: true);
            Log.Information("Warden: migrated {Count} fingerprint(s) from legacy warden.json", migrated);
        }
        catch (Exception e) when (e is JsonException or SqliteException or IOException)
        {
            Log.Warning(e, "Warden: legacy migration failed");
        }
    }

    private static string MergeBackbones(string existingCsv, IEnumerable<string> add)
    {
        var set = new HashSet<string>(SplitBackbones(existingCsv), StringComparer.Ordinal);
        foreach (var b in add.Where(b => !string.IsNullOrWhiteSpace(b)))
            set.Add(b);
        return set.Count == 0 ? "unknown" : string.Join(",", set);
    }

    private static string JoinBackbones(string[]? backbones)
    {
        if (backbones is null || backbones.Length == 0) return "unknown";
        var set = new HashSet<string>(backbones.Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.Ordinal);
        return set.Count == 0 ? "unknown" : string.Join(",", set);
    }

    private static string[] SplitBackbones(string csv) =>
        string.IsNullOrEmpty(csv)
            ? Array.Empty<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal).ToArray();

    private static bool BackboneInScope(string entryCsv, HashSet<string> mine)
    {
        var known = false;
        foreach (var rb in SplitBackbones(entryCsv)
                     .Select(b => WardenFingerprint.RootDomain(b))
                     .Where(rb => rb != "unknown"))
        {
            known = true;
            if (mine.Contains(rb)) return true;
        }
        return !known;
    }
}

public class WardenFile
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("entries")] public List<WardenRecord> Entries { get; set; } = new();
}

public class WardenRecord
{
    [JsonPropertyName("fp")] public string Fp { get; set; } = "";
    [JsonPropertyName("bk")] public string[]? Backbones { get; set; }
    [JsonPropertyName("deadAt")] public long DeadAt { get; set; }
    [JsonPropertyName("n")] public int Count { get; set; }
}

public class WardenSourceInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("trust")] public string Trust { get; set; } = WardenStore.TrustCorroborate;
    [JsonPropertyName("refreshHours")] public int RefreshHours { get; set; }
    [JsonPropertyName("lastChecked")] public long LastChecked { get; set; }
    [JsonPropertyName("lastUpdated")] public long LastUpdated { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
}

public class RemoteSourceSpec
{
    public string Url { get; set; } = "";
    public string? Name { get; set; }
    public string? Trust { get; set; }
    public int RefreshHours { get; set; } = 24;
}
