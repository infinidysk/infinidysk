using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace NzbWebDAV.Services.NativeCache;

/// <summary>Durable per-article revision stamps, not cached article bytes. Tombstones survive patch eviction.</summary>
public sealed class RepairRevisionStore : IDisposable
{
    private readonly SqliteConnection _database;
    private readonly Lock _gate = new();
    private readonly string _epoch;
    private long _version;
    private int _publications;

    public RepairRevisionStore(string path)
    {
        NativeFileSystem.RequireLocalMetadata(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _database.Open();
        Execute("""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            PRAGMA cache_size=-2048;
            CREATE TABLE IF NOT EXISTS Identity(Id TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS Revisions(Hash TEXT PRIMARY KEY,Stamp TEXT NOT NULL);
            """);
        using var identity = Command("SELECT Id FROM Identity LIMIT 1");
        _epoch = identity.ExecuteScalar() as string ?? Guid.NewGuid().ToString("N");
        Execute("INSERT OR IGNORE INTO Identity(Id) VALUES($id)", ("$id", _epoch));
    }

    public Snapshot Capture(IEnumerable<string> segmentIds)
    {
        // Keep references to existing metadata strings, not a library-sized hash map.
        // Queries and temporary hash maps below are limited to 256 IDs at a time.
        var ids = segmentIds.ToArray();
        lock (_gate) return new Snapshot(this, ids, Fingerprint(ids), _version, _publications > 0);
    }

    public IDisposable BeginPublication(IEnumerable<string> hashedSegmentIds)
    {
        lock (_gate)
        {
            using var transaction = _database.BeginTransaction();
            var stamp = Guid.NewGuid().ToString("N");
            foreach (var id in hashedSegmentIds)
            {
                using var command = Command("INSERT INTO Revisions(Hash,Stamp) VALUES($hash,$stamp) ON CONFLICT(Hash) DO UPDATE SET Stamp=excluded.Stamp",
                    ("$hash", id), ("$stamp", stamp));
                command.Transaction = transaction;
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            _version++;
            _publications++;
            return new Publication(this);
        }
    }

    private string Fingerprint(string[] ids)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        digest.AppendData(Encoding.ASCII.GetBytes(_epoch));
        using var any = Command("SELECT 1 FROM Revisions LIMIT 1");
        if (any.ExecuteScalar() is null) return Convert.ToHexString(digest.GetHashAndReset());
        foreach (var chunk in ids.Chunk(256))
        {
            var hashes = chunk.Select(HashSegmentId).ToArray();
            using var query = _database.CreateCommand();
            query.CommandText = "SELECT Hash,Stamp FROM Revisions WHERE Hash IN (" + string.Join(',', hashes.Select((_, index) => "$p" + index)) + ")";
            for (var index = 0; index < hashes.Length; index++) query.Parameters.AddWithValue("$p" + index, hashes[index]);
            using var reader = query.ExecuteReader();
            var stamps = new Dictionary<string, string>(StringComparer.Ordinal);
            while (reader.Read()) stamps[reader.GetString(0)] = reader.GetString(1);
            foreach (var hash in hashes)
                if (stamps.TryGetValue(hash, out var stamp)) digest.AppendData(Encoding.ASCII.GetBytes(hash + stamp));
        }
        return Convert.ToHexString(digest.GetHashAndReset());
    }

    public static string HashSegmentId(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));

    public sealed class Snapshot
    {
        private readonly RepairRevisionStore _store;
        private readonly string[] _ids;
        private long _version;
        private bool _invalid;
        internal Snapshot(RepairRevisionStore store, string[] ids, string fingerprint, long version, bool invalid)
        { _store = store; _ids = ids; Fingerprint = fingerprint; _version = version; _invalid = invalid; }
        public string Fingerprint { get; }
        public bool IsCurrent
        {
            get
            {
                lock (_store._gate)
                {
                    if (_invalid || _store._publications > 0) return false;
                    if (_version == _store._version) return true;
                    try { _invalid = Fingerprint != _store.Fingerprint(_ids); }
                    catch (SqliteException) { _invalid = true; }
                    _version = _store._version;
                    return !_invalid;
                }
            }
        }
    }
    private sealed class Publication(RepairRevisionStore store) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (store._gate) store._publications--;
        }
    }
    private SqliteCommand Command(string sql, params (string Key, object Value)[] parameters)
    {
        var command = _database.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Key, parameter.Value);
        return command;
    }
    private void Execute(string sql, params (string Key, object Value)[] parameters)
    { using var command = Command(sql, parameters); command.ExecuteNonQuery(); }
    public void Dispose() { lock (_gate) _database.Dispose(); }
}
