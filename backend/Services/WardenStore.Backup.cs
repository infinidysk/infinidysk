using Microsoft.Data.Sqlite;
using Serilog;

namespace NzbWebDAV.Services;

public partial class WardenStore
{
    private const string BkEnabled = "enabled";
    private const string BkRepo = "repo";
    private const string BkPath = "path";
    private const string BkBranch = "branch";
    private const string BkScope = "scope";
    private const string BkInterval = "interval_hours";
    private const string BkToken = "token";
    private const string BkLastAt = "last_at";
    private const string BkLastStatus = "last_status";
    private const string BkSha = "sha";
    private const string BkHash = "hash";

    public WardenBackupSettings GetBackupSettings()
    {
        var m = ReadAllBackupMeta();
        return new WardenBackupSettings
        {
            Enabled = m.GetValueOrDefault(BkEnabled) == "true",
            Repo = m.GetValueOrDefault(BkRepo) ?? "",
            Path = m.GetValueOrDefault(BkPath) ?? "warden/warden.ndjson.gz",
            Branch = m.GetValueOrDefault(BkBranch) is { Length: > 0 } b ? b : "main",
            Scope = m.GetValueOrDefault(BkScope) == "merged" ? "merged" : "local",
            IntervalHours = int.TryParse(m.GetValueOrDefault(BkInterval), out var n) ? Math.Clamp(n, 1, 720) : 24,
            HasToken = !string.IsNullOrEmpty(m.GetValueOrDefault(BkToken)),
            LastAt = long.TryParse(m.GetValueOrDefault(BkLastAt), out var t) ? t : 0,
            LastStatus = m.GetValueOrDefault(BkLastStatus),
        };
    }

    public string? GetBackupToken()
    {
        var v = GetBackupMeta(BkToken);
        return string.IsNullOrEmpty(v) ? null : v;
    }

    public (string? Sha, string? Hash) GetBackupSyncState()
        => (GetBackupMeta(BkSha), GetBackupMeta(BkHash));

    public void SaveBackupSettings(bool enabled, string repo, string path, string branch, string scope, int intervalHours, string? token)
    {
        var sets = new Dictionary<string, string?>
        {
            [BkEnabled] = enabled ? "true" : "false",
            [BkRepo] = repo.Trim(),
            [BkPath] = string.IsNullOrWhiteSpace(path) ? "warden/warden.ndjson.gz" : path.Trim().TrimStart('/'),
            [BkBranch] = string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim(),
            [BkScope] = scope == "merged" ? "merged" : "local",
            [BkInterval] = Math.Clamp(intervalHours, 1, 720).ToString(),
        };
        if (token is not null) sets[BkToken] = token;
        WriteBackupMeta(sets);
    }

    public void SetBackupResult(long at, string status, string? sha, string? contentHash)
    {
        var sets = new Dictionary<string, string?>
        {
            [BkLastAt] = at.ToString(),
            [BkLastStatus] = status,
        };
        if (sha is not null) sets[BkSha] = sha;
        if (contentHash is not null) sets[BkHash] = contentHash;
        WriteBackupMeta(sets);
    }

    private static void EnsureBackupTable(SqliteConnection conn)
        => Exec(conn, "CREATE TABLE IF NOT EXISTS warden_backup (k TEXT PRIMARY KEY, v TEXT NOT NULL);");

    private string? GetBackupMeta(string key)
    {
        try
        {
            using var conn = Open();
            EnsureBackupTable(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT v FROM warden_backup WHERE k = $k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
        catch (Exception e) when (e is SqliteException or InvalidOperationException)
        {
            Log.Debug(e, "Warden: backup-meta read failed");
            return null;
        }
    }

    private Dictionary<string, string> ReadAllBackupMeta()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var conn = Open();
            EnsureBackupTable(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT k, v FROM warden_backup";
            using var r = cmd.ExecuteReader();
            while (r.Read()) d[r.GetString(0)] = r.GetString(1);
        }
        catch (Exception e) when (e is SqliteException or InvalidOperationException)
        {
            Log.Debug(e, "Warden: backup-meta read-all failed");
        }
        return d;
    }

    private void WriteBackupMeta(Dictionary<string, string?> values)
    {
        try
        {
            using var conn = Open();
            EnsureBackupTable(conn);
            using var tx = conn.BeginTransaction();
            foreach (var (k, v) in values)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                if (v is null)
                {
                    cmd.CommandText = "DELETE FROM warden_backup WHERE k = $k";
                    cmd.Parameters.AddWithValue("$k", k);
                }
                else
                {
                    cmd.CommandText = "INSERT INTO warden_backup (k, v) VALUES ($k, $v) ON CONFLICT(k) DO UPDATE SET v = $v";
                    cmd.Parameters.AddWithValue("$k", k);
                    cmd.Parameters.AddWithValue("$v", v);
                }
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch (Exception e) when (e is SqliteException or InvalidOperationException)
        {
            Log.Debug(e, "Warden: backup-meta write failed");
        }
    }
}

public class WardenBackupSettings
{
    public bool Enabled { get; set; }
    public string Repo { get; set; } = "";
    public string Path { get; set; } = "";
    public string Branch { get; set; } = "main";
    public string Scope { get; set; } = "local";
    public int IntervalHours { get; set; } = 24;
    public bool HasToken { get; set; }
    public long LastAt { get; set; }
    public string? LastStatus { get; set; }
}
