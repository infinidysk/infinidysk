using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NzbWebDAV.Services.NativeCache;

/// <summary>
/// Final-file storage with bounded integrity blocks and an indexed, local catalogue.
/// Sparse length is never coverage: only a checksummed committed block is a hit.
/// Writes are serialized and durably flushed before catalogue publication. Reads
/// open delete-sharing handles under the catalogue gate then perform IO outside it.
/// </summary>
public sealed class NativeCacheStore : IAsyncDisposable
{
    public const int BlockSize = 4 * 1024 * 1024;
    private const long EntryOverhead = 64 * 1024;
    private readonly SqliteConnection _database;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly NativeCacheFolder[] _folders;
    private readonly Dictionary<string, FileStream> _owners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _leases = new(StringComparer.Ordinal);
    private readonly Lock _leaseLock = new();
    private bool _disposed;

    public NativeCacheStore(string cataloguePath, IReadOnlyList<NativeCacheFolder> folders)
    {
        NativeCacheFolder.Validate(folders);
        _folders = folders.OrderByDescending(folder => folder.Priority).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cataloguePath))!);
        _database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = cataloguePath, Pooling = false
        }.ToString());
        _database.Open();
        Execute("""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            PRAGMA foreign_keys=ON;
            PRAGMA cache_size=-4096;
            CREATE TABLE IF NOT EXISTS Entries(
                Key TEXT PRIMARY KEY, Folder TEXT NOT NULL, Length INTEGER NOT NULL,
                Bytes INTEGER NOT NULL DEFAULT 0, Access INTEGER NOT NULL,
                Pinned INTEGER NOT NULL DEFAULT 0, Dirty INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS EntryEviction ON Entries(Folder, Pinned, Access);
            CREATE TABLE IF NOT EXISTS FolderTotals(Folder TEXT PRIMARY KEY, Bytes INTEGER NOT NULL DEFAULT 0);
            INSERT OR IGNORE INTO FolderTotals SELECT Folder,SUM(Bytes) FROM Entries GROUP BY Folder;
            CREATE TRIGGER IF NOT EXISTS EntryAdded AFTER INSERT ON Entries BEGIN
                INSERT INTO FolderTotals(Folder,Bytes) VALUES(NEW.Folder,NEW.Bytes)
                    ON CONFLICT(Folder) DO UPDATE SET Bytes=Bytes+NEW.Bytes;
            END;
            CREATE TRIGGER IF NOT EXISTS EntryChanged AFTER UPDATE OF Bytes ON Entries BEGIN
                UPDATE FolderTotals SET Bytes=Bytes+NEW.Bytes-OLD.Bytes WHERE Folder=NEW.Folder;
            END;
            CREATE TRIGGER IF NOT EXISTS EntryRemoved AFTER DELETE ON Entries BEGIN
                UPDATE FolderTotals SET Bytes=Bytes-OLD.Bytes WHERE Folder=OLD.Folder;
            END;
            CREATE TABLE IF NOT EXISTS Blocks(
                Key TEXT NOT NULL REFERENCES Entries(Key) ON DELETE CASCADE,
                Offset INTEGER NOT NULL, Count INTEGER NOT NULL, Hash BLOB NOT NULL,
                PRIMARY KEY(Key, Offset));
            """);
        foreach (var folder in _folders.Where(folder => folder.Enabled && !folder.ReadOnly && Directory.Exists(folder.Path)))
        {
            try
            {
                _owners[folder.Id] = new FileStream(Path.Combine(folder.Path, ".infinidysk-owner"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) { /* Locked/offline folders remain readable but cannot accept writes. */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    public async Task<int> ReadBlockAsync(NativeCacheIdentity identity, long offset, Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        ValidateOffset(identity, offset);
        using var lease = AcquireLease(identity);
        FileStream? data = null;
        byte[]? expected = null;
        var count = 0;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var command = Command("SELECT e.Folder, b.Count, b.Hash FROM Blocks b JOIN Entries e ON e.Key=b.Key WHERE b.Key=$key AND b.Offset=$offset",
                ("$key", identity.Key), ("$offset", offset));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return 0;
            var folder = _folders.FirstOrDefault(folder => folder.Id == reader.GetString(0) && folder.Enabled);
            if (folder is null) return 0;
            count = reader.GetInt32(1);
            if (destination.Length < count) throw new ArgumentException("Destination must hold a full integrity block.", nameof(destination));
            expected = (byte[])reader[2];
            try
            {
                data = new FileStream(DataPath(folder, identity.Key), FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
                data.Position = offset;
            }
            catch (IOException)
            {
                reader.Close();
                Execute("DELETE FROM Blocks WHERE Key=$key AND Offset=$offset AND Hash=$hash",
                    ("$key", identity.Key), ("$offset", offset), ("$hash", expected));
                return 0;
            }
            catch (UnauthorizedAccessException) { return 0; }
            reader.Close();
            // Coalesce access accounting to five-minute buckets rather than updating every read.
            var bucket = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300;
            try
            {
                Execute("UPDATE Entries SET Access=$access WHERE Key=$key AND Access<>$access", ("$access", bucket), ("$key", identity.Key));
            }
            catch (SqliteException) { /* Access telemetry cannot invalidate an otherwise usable open handle. */ }
        }
        finally { _gate.Release(); }

        await using (data.ConfigureAwait(false))
        {
            try
            {
                await data.ReadExactlyAsync(destination[..count], cancellationToken).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(destination.Span[..count]), expected))
                {
                    await InvalidateBlockAsync(identity.Key, offset, expected, cancellationToken).ConfigureAwait(false);
                    return 0;
                }
                return count;
            }
            catch (IOException)
            {
                await InvalidateBlockAsync(identity.Key, offset, expected, cancellationToken).ConfigureAwait(false);
                return 0;
            }
        }
    }

    public async Task<bool> WriteBlockAsync(NativeCacheIdentity identity, long offset, ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        ValidateOffset(identity, offset);
        if (data.Length != Math.Min(BlockSize, identity.Length - offset))
            throw new ArgumentException("Only complete integrity blocks may be published.", nameof(data));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var lookup = Command("SELECT Folder FROM Entries WHERE Key=$key", ("$key", identity.Key));
            var folderId = lookup.ExecuteScalar() as string;
            var allocation = RoundAllocation(data.Length);
            var folder = folderId is null
                ? _folders.FirstOrDefault(candidate => CanWrite(candidate, allocation + EntryOverhead))
                : _folders.FirstOrDefault(candidate => candidate.Id == folderId && CanWrite(candidate, allocation));
            if (folder is null) return false;
            using var exists = Command("SELECT 1 FROM Blocks WHERE Key=$key AND Offset=$offset", ("$key", identity.Key), ("$offset", offset));
            if (exists.ExecuteScalar() is not null) return true;

            var directory = EntryPath(folder, identity.Key);
            Directory.CreateDirectory(directory);
            if (folderId is null)
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"),
                    JsonSerializer.Serialize(new Manifest(1, identity)), cancellationToken).ConfigureAwait(false);
                Execute("INSERT INTO Entries(Key,Folder,Length,Bytes,Access,Dirty) VALUES($key,$folder,$length,$bytes,$access,1)",
                    ("$key", identity.Key), ("$folder", folder.Id), ("$length", identity.Length),
                    ("$bytes", EntryOverhead), ("$access", DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300));
            }
            else Execute("UPDATE Entries SET Dirty=1 WHERE Key=$key", ("$key", identity.Key));

            // Reserve before disk IO. Failed writes deliberately retain their reservation until
            // reconciliation/eviction, so interrupted writes cannot silently exceed the quota.
            Execute("UPDATE Entries SET Bytes=Bytes+$bytes WHERE Key=$key", ("$bytes", allocation), ("$key", identity.Key));

            var hash = SHA256.HashData(data.Span);
            await using (var stream = new FileStream(DataPath(folder, identity.Key), FileMode.OpenOrCreate,
                FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess))
            {
                stream.Position = offset;
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            // The colocated journal makes explicit scans/imports possible without the local catalogue.
            await using (var journal = new FileStream(Path.Combine(directory, "ranges.journal"), FileMode.Append,
                FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous))
            {
                var record = JsonSerializer.SerializeToUtf8Bytes(new JournalBlock(offset, data.Length, Convert.ToHexString(hash)));
                await journal.WriteAsync(record, cancellationToken).ConfigureAwait(false);
                await journal.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                journal.Flush(flushToDisk: true);
            }
            using var transaction = _database.BeginTransaction();
            using (var insert = Command("INSERT INTO Blocks(Key,Offset,Count,Hash) VALUES($key,$offset,$count,$hash)",
                ("$key", identity.Key), ("$offset", offset), ("$count", data.Length), ("$hash", hash)))
            {
                insert.Transaction = transaction;
                insert.ExecuteNonQuery();
            }
            using (var update = Command("UPDATE Entries SET Dirty=0 WHERE Key=$key", ("$key", identity.Key)))
            {
                update.Transaction = transaction;
                update.ExecuteNonQuery();
            }
            transaction.Commit();
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<NativeCacheFolderStatus>> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = new List<NativeCacheFolderStatus>();
            foreach (var folder in _folders)
            {
                using var command = Command("SELECT COALESCE(SUM(Bytes),0),COUNT(*) FROM Entries WHERE Folder=$folder", ("$folder", folder.Id));
                using var reader = command.ExecuteReader();
                reader.Read();
                var online = Directory.Exists(folder.Path);
                result.Add(new(folder.Id, online, online && _owners.ContainsKey(folder.Id), reader.GetInt64(0), reader.GetInt64(1),
                    online ? null : "Folder is unavailable; no fallback directory will be created."));
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<long> GetCoverageAsync(NativeCacheIdentity identity, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = Command("SELECT COALESCE(SUM(Count),0) FROM Blocks WHERE Key=$key", ("$key", identity.Key));
            return (long)command.ExecuteScalar()!;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Explicit, streaming reconciliation; never enumerates the NAS during normal startup.</summary>
    public async Task<int> ScanAsync(string folderId, CancellationToken cancellationToken = default)
    {
        var folder = _folders.FirstOrDefault(folder => folder.Id == folderId && folder.Enabled)
            ?? throw new ArgumentException("Unknown or disabled native cache folder.", nameof(folderId));
        var root = Path.Combine(folder.Path, "v1");
        if (!Directory.Exists(root) || new DirectoryInfo(root).LinkTarget is not null) return 0;
        var imported = 0;
        var buffer = new byte[BlockSize];
        foreach (var shard in Directory.EnumerateDirectories(root))
        {
            if (new DirectoryInfo(shard).LinkTarget is not null) continue;
            foreach (var directory in Directory.EnumerateDirectories(shard))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (new DirectoryInfo(directory).LinkTarget is not null) continue;
                var manifestPath = Path.Combine(directory, "manifest.json");
                var journalPath = Path.Combine(directory, "ranges.journal");
                var dataPath = Path.Combine(directory, "content.data");
                if (new[] { manifestPath, journalPath, dataPath }.Any(path => new FileInfo(path).LinkTarget is not null)) continue;
                try
                {
                    var info = new FileInfo(manifestPath);
                    if (!info.Exists || info.Length > 64 * 1024) continue;
                    var manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
                    if (manifest is not { Version: 1, Identity: not null } || manifest.Identity.Length <= 0
                        || !string.Equals(directory, EntryPath(folder, manifest.Identity.Key), StringComparison.Ordinal)) continue;
                    using var lease = AcquireLease(manifest.Identity);
                    await using var data = new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                        4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using var journal = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                        4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    using var lines = new StreamReader(journal);
                    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        using var owner = Command("SELECT Folder FROM Entries WHERE Key=$key", ("$key", manifest.Identity.Key));
                        if (owner.ExecuteScalar() is string existingFolder && existingFolder != folder.Id) continue;
                        Execute("INSERT OR IGNORE INTO Entries(Key,Folder,Length,Bytes,Access) VALUES($key,$folder,$length,$bytes,$access)",
                            ("$key", manifest.Identity.Key), ("$folder", folder.Id), ("$length", manifest.Identity.Length),
                            ("$bytes", EntryOverhead), ("$access", DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300));
                    }
                    finally { _gate.Release(); }
                    while (await ReadJournalLineAsync(lines, cancellationToken).ConfigureAwait(false) is { } line)
                    {
                        JournalBlock? block;
                        try { block = JsonSerializer.Deserialize<JournalBlock>(line); }
                        catch (JsonException) { continue; } // Includes incomplete trailing writes.
                        if (block is null || block.Offset < 0 || block.Offset >= manifest.Identity.Length
                            || block.Offset % BlockSize != 0 || block.Count != Math.Min(BlockSize, manifest.Identity.Length - block.Offset)
                            || block.Hash is not { Length: 64 }) continue;
                        data.Position = block.Offset;
                        try { await data.ReadExactlyAsync(buffer.AsMemory(0, block.Count), cancellationToken).ConfigureAwait(false); }
                        catch (EndOfStreamException) { continue; }
                        var hash = SHA256.HashData(buffer.AsSpan(0, block.Count));
                        if (!string.Equals(Convert.ToHexString(hash), block.Hash, StringComparison.Ordinal)) continue;
                        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            using var transaction = _database.BeginTransaction();
                            using var insert = Command("INSERT OR IGNORE INTO Blocks(Key,Offset,Count,Hash) VALUES($key,$offset,$count,$hash)",
                                ("$key", manifest.Identity.Key), ("$offset", block.Offset), ("$count", block.Count), ("$hash", hash));
                            insert.Transaction = transaction;
                            if (insert.ExecuteNonQuery() > 0)
                            {
                                using var update = Command("UPDATE Entries SET Bytes=Bytes+$bytes WHERE Key=$key",
                                    ("$key", manifest.Identity.Key), ("$bytes", RoundAllocation(block.Count)));
                                update.Transaction = transaction;
                                update.ExecuteNonQuery();
                            }
                            transaction.Commit();
                        }
                        finally { _gate.Release(); }
                    }
                    imported++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (JsonException) { }
            }
        }
        return imported;
    }

    private static async Task<string?> ReadJournalLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        // A corrupt journal cannot make ReadLine allocate an unbounded string.
        var line = new char[256];
        var count = 0;
        while (count < line.Length)
        {
            var read = await reader.ReadAsync(line.AsMemory(count, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0) return count == 0 ? null : new string(line, 0, count);
            if (line[count] == '\n') return new string(line, 0, count);
            count++;
        }
        throw new InvalidDataException("Oversized native-cache journal record.");
    }

    private bool CanWrite(NativeCacheFolder folder, long requested)
    {
        if (!folder.Enabled || folder.ReadOnly || !_owners.ContainsKey(folder.Id)
            || !File.Exists(Path.Combine(folder.Path, ".infinidysk-owner"))) return false;
        using var command = Command("SELECT Bytes FROM FolderTotals WHERE Folder=$folder", ("$folder", folder.Id));
        if ((command.ExecuteScalar() as long? ?? 0) + requested > folder.MaxBytes) return false;
        try
        {
            var drive = DriveInfo.GetDrives().Where(drive => folder.Path == drive.Name.TrimEnd(Path.DirectorySeparatorChar)
                || folder.Path.StartsWith(drive.Name.EndsWith(Path.DirectorySeparatorChar) ? drive.Name : drive.Name + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal)).MaxBy(drive => drive.Name.Length);
            return drive is not null && drive.AvailableFreeSpace >= requested + folder.MinFreeBytes;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private async Task InvalidateBlockAsync(string key, long offset, byte[] hash, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Execute("DELETE FROM Blocks WHERE Key=$key AND Offset=$offset AND Hash=$hash",
                ("$key", key), ("$offset", offset), ("$hash", hash));
            // Allocated bytes remain reserved until whole-file eviction/reconciliation.
        }
        finally { _gate.Release(); }
    }

    private static long RoundAllocation(int bytes) => ((bytes + 65535L) / 65536 * 65536) + 4096;

    public IDisposable AcquireLease(NativeCacheIdentity identity)
    {
        var key = identity.Key;
        lock (_leaseLock) _leases[key] = _leases.GetValueOrDefault(key) + 1;
        return new ActivityLease(this, key);
    }

    public async Task SetPinnedAsync(NativeCacheIdentity identity, bool pinned, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { Execute("UPDATE Entries SET Pinned=$pinned WHERE Key=$key", ("$pinned", pinned ? 1 : 0), ("$key", identity.Key)); }
        finally { _gate.Release(); }
    }

    /// <summary>Deletes only indexed application-owned entries, in bounded pages. Active and pinned entries are retained.</summary>
    public async Task<int> EvictAsync(string folderId, bool clear = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var folder = _folders.FirstOrDefault(folder => folder.Id == folderId);
            if (folder is null || folder.ReadOnly || !_owners.ContainsKey(folder.Id)) return 0;
            var cutoff = DateTimeOffset.UtcNow.AddDays(-folder.MaxAgeDays).ToUnixTimeSeconds() / 300;
            using var query = Command("SELECT Key FROM Entries WHERE Folder=$folder AND Pinned=0 AND ($clear=1 OR Access<$cutoff) ORDER BY Access LIMIT 64",
                ("$folder", folderId), ("$clear", clear ? 1 : 0), ("$cutoff", cutoff));
            using var reader = query.ExecuteReader();
            var keys = new List<string>(64);
            while (reader.Read()) keys.Add(reader.GetString(0));
            reader.Close();
            var deleted = 0;
            foreach (var key in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_leaseLock)
                {
                    if (_leases.ContainsKey(key)) continue;
                    var directory = EntryPath(folder, key);
                    if (new DirectoryInfo(folder.Path).LinkTarget is not null
                        || new DirectoryInfo(Path.Combine(folder.Path, "v1")).LinkTarget is not null
                        || new DirectoryInfo(Path.Combine(folder.Path, "v1", key[..2])).LinkTarget is not null
                        || new DirectoryInfo(directory).LinkTarget is not null) continue;
                    // No arbitrary recursive deletion: remove our three known files only.
                    try
                    {
                        if (Directory.Exists(directory))
                        {
                            File.Delete(Path.Combine(directory, "content.data"));
                            File.Delete(Path.Combine(directory, "ranges.journal"));
                            File.Delete(Path.Combine(directory, "manifest.json"));
                            Directory.Delete(directory);
                        }
                        Execute("DELETE FROM Entries WHERE Key=$key", ("$key", key));
                        deleted++;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            return deleted;
        }
        finally { _gate.Release(); }
    }

    private sealed class ActivityLease(NativeCacheStore store, string key) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (store._leaseLock)
            {
                if (store._leases[key] == 1) store._leases.Remove(key);
                else store._leases[key]--;
            }
        }
    }
    private static string EntryPath(NativeCacheFolder folder, string key) => Path.Combine(folder.Path, "v1", key[..2], key);
    private static string DataPath(NativeCacheFolder folder, string key) => Path.Combine(EntryPath(folder, key), "content.data");
    private static void ValidateOffset(NativeCacheIdentity identity, long offset)
    {
        if (identity.Length <= 0 || offset < 0 || offset >= identity.Length || offset % BlockSize != 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Native cache offsets must be aligned and inside the file.");
    }

    private SqliteCommand Command(string sql, params (string Name, object Value)[] parameters)
    {
        var command = _database.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.ExecuteNonQuery();
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed)) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var owner in _owners.Values) await owner.DisposeAsync().ConfigureAwait(false);
            await _database.DisposeAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        // Do not dispose the managed-only gate: already queued operations must wake
        // and observe _disposed, and repeated disposal is supported. No WaitHandle is used.
    }

    private sealed record Manifest(int Version, NativeCacheIdentity Identity);
    private sealed record JournalBlock(long Offset, int Count, string Hash);
}
