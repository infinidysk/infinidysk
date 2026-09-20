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
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly NativeCacheFolder[] _folders;
    private readonly Dictionary<string, FileStream> _owners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _volumes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _leases = new(StringComparer.Ordinal);
    private readonly HashSet<string> _evicting = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Key, long Offset), FillState> _fills = [];
    private readonly Dictionary<string, WarmReservation> _reservations = new(StringComparer.Ordinal);
    private readonly Lock _leaseLock = new();
    private bool _disposed;

    public NativeCacheStore(string cataloguePath, IReadOnlyList<NativeCacheFolder> folders)
    {
        NativeCacheFolder.Validate(folders);
        _folders = folders.OrderByDescending(folder => folder.Priority).ToArray();
        NativeFileSystem.RequireLocalMetadata(cataloguePath);
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
                Pinned INTEGER NOT NULL DEFAULT 0, Dirty INTEGER NOT NULL DEFAULT 0,
                ItemId TEXT NOT NULL DEFAULT '',VerifiedBytes INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS EntryEviction ON Entries(Folder, Pinned, Access);
            CREATE INDEX IF NOT EXISTS EntryPage ON Entries(Folder,Key);
            CREATE TABLE IF NOT EXISTS FolderTotals(Folder TEXT PRIMARY KEY, Bytes INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS FolderIdentity(Folder TEXT PRIMARY KEY, Volume TEXT NOT NULL);
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
        EnsureMetadataColumns();
        Execute("""
            CREATE TRIGGER IF NOT EXISTS BlockAdded AFTER INSERT ON Blocks BEGIN
                UPDATE Entries SET VerifiedBytes=VerifiedBytes+NEW.Count WHERE Key=NEW.Key;
            END;
            CREATE TRIGGER IF NOT EXISTS BlockRemoved AFTER DELETE ON Blocks BEGIN
                UPDATE Entries SET VerifiedBytes=VerifiedBytes-OLD.Count WHERE Key=OLD.Key;
            END;
            """);
        foreach (var folder in _folders.Where(folder => folder.Enabled && Directory.Exists(folder.Path)))
        {
            try
            {
                if (!RegisterVolume(folder) || folder.ReadOnly) continue;
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
        lock (_leaseLock) if (_evicting.Contains(identity.Key)) return 0;
        FileStream? data = null;
        NativeCacheFolder? folder = null;
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
            folder = _folders.FirstOrDefault(folder => folder.Id == reader.GetString(0) && folder.Enabled);
            if (folder is null) return 0;
            count = reader.GetInt32(1);
            if (destination.Length < count) throw new ArgumentException("Destination must hold a full integrity block.", nameof(destination));
            expected = (byte[])reader[2];
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

        // Filesystem calls can block on an unavailable NAS. Never hold the local
        // catalogue or lease locks across these calls. This read's lease fences eviction.
        if (!IsVolumeCurrent(folder)) return 0;
        try
        {
            data = new FileStream(DataPath(folder, identity.Key), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
            data.Position = offset;
        }
        catch (IOException)
        {
            await InvalidateBlockAsync(identity.Key, offset, expected, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (UnauthorizedAccessException) { return 0; }

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
        using var lease = AcquireLease(identity);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        var catalogueHeld = false;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            catalogueHeld = true;
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var lookup = Command("SELECT Folder FROM Entries WHERE Key=$key", ("$key", identity.Key));
            var folderId = lookup.ExecuteScalar() as string;
            var allocation = RoundAllocation(data.Length);
            using var exists = Command("SELECT 1 FROM Blocks WHERE Key=$key AND Offset=$offset", ("$key", identity.Key), ("$offset", offset));
            if (exists.ExecuteScalar() is not null) return true;

            var required = allocation + (folderId is null ? EntryOverhead : 0);
            string? reservedFolder;
            lock (_leaseLock) reservedFolder = _reservations.GetValueOrDefault(identity.Key)?.Folder;
            var candidates = _folders.Where(candidate => (folderId is null || candidate.Id == folderId)
                && (reservedFolder is null || candidate.Id == reservedFolder)
                && HasQuota(candidate, required, identity.Key)).ToArray();
            _gate.Release();
            catalogueHeld = false;
            var folder = candidates.FirstOrDefault(candidate => CanUseFolder(candidate, required));
            if (folder is null) return false;

            var directory = EntryPath(folder, identity.Key);
            if (!HasSafeLayout(folder, identity.Key)) return false;
            Directory.CreateDirectory(directory);
            if (folderId is null)
            {
                await using (var manifest = new FileStream(Path.Combine(directory, "manifest.json"),
                    FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous))
                {
                    await manifest.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new Manifest(1, identity)), cancellationToken).ConfigureAwait(false);
                    manifest.Flush(flushToDisk: true);
                }
            }
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            catalogueHeld = true;
            // Writers/eviction are serialized separately; readers need only this
            // short local transaction, even while another folder is stalled.
            if (folderId is null)
            {
                Execute("INSERT INTO Entries(Key,Folder,Length,Bytes,Access,Dirty,ItemId) VALUES($key,$folder,$length,$bytes,$access,1,$item)",
                    ("$key", identity.Key), ("$folder", folder.Id), ("$length", identity.Length),
                    ("$bytes", EntryOverhead), ("$access", DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300), ("$item", identity.ItemId));
            }
            else Execute("UPDATE Entries SET Dirty=1 WHERE Key=$key", ("$key", identity.Key));

            // Reserve before disk IO. Failed writes deliberately retain their reservation until
            // reconciliation/eviction, so interrupted writes cannot silently exceed the quota.
            Execute("UPDATE Entries SET Bytes=Bytes+$bytes WHERE Key=$key", ("$bytes", allocation), ("$key", identity.Key));

            var hash = SHA256.HashData(data.Span);
            // A slow NAS write must not hold the local catalogue gate or stall hits
            // in other folders. The writer gate and activity lease protect publication.
            _gate.Release();
            catalogueHeld = false;
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
            NativeFileSystem.FlushDirectory(directory);
            NativeFileSystem.FlushDirectory(Path.GetDirectoryName(directory)!);
            NativeFileSystem.FlushDirectory(Path.Combine(folder.Path, "v1"));
            NativeFileSystem.FlushDirectory(folder.Path);
            var allocated = checked(NativeFileSystem.GetAllocatedBytes(DataPath(folder, identity.Key))
                + NativeFileSystem.GetAllocatedBytes(Path.Combine(directory, "manifest.json"))
                + NativeFileSystem.GetAllocatedBytes(Path.Combine(directory, "ranges.journal")) + EntryOverhead);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            catalogueHeld = true;
            using var transaction = _database.BeginTransaction();
            using (var insert = Command("INSERT OR IGNORE INTO Blocks(Key,Offset,Count,Hash) VALUES($key,$offset,$count,$hash)",
                ("$key", identity.Key), ("$offset", offset), ("$count", data.Length), ("$hash", hash)))
            {
                insert.Transaction = transaction;
                insert.ExecuteNonQuery();
            }
            using (var update = Command("UPDATE Entries SET Dirty=0,Bytes=$bytes WHERE Key=$key", ("$key", identity.Key), ("$bytes", allocated)))
            {
                update.Transaction = transaction;
                update.ExecuteNonQuery();
            }
            transaction.Commit();
            lock (_leaseLock)
                if (_reservations.TryGetValue(identity.Key, out var reservation))
                    reservation.Remaining = Math.Max(0, reservation.Remaining - required);
            return true;
        }
        finally
        {
            if (catalogueHeld) _gate.Release();
            _writer.Release();
        }
    }

    public async Task<IReadOnlyList<NativeCacheFolderStatus>> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<NativeCacheFolderStatus>();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var folder in _folders)
            {
                using var command = Command("SELECT COALESCE(SUM(Bytes),0),COUNT(*) FROM Entries WHERE Folder=$folder", ("$folder", folder.Id));
                using var reader = command.ExecuteReader();
                reader.Read();
                result.Add(new(folder.Id, false, false, reader.GetInt64(0), reader.GetInt64(1), null));
            }
        }
        finally { _gate.Release(); }
        return result.Select(status =>
        {
            var folder = _folders.First(folder => folder.Id == status.Id);
            var online = IsVolumeCurrent(folder);
            return status with { Online = online, Writable = online && _owners.ContainsKey(folder.Id),
                Error = online ? null : "Folder is unavailable; no fallback directory will be created." };
        }).ToArray();
    }

    public async Task<long> GetCoverageAsync(NativeCacheIdentity identity, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = Command("SELECT VerifiedBytes FROM Entries WHERE Key=$key", ("$key", identity.Key));
            return command.ExecuteScalar() as long? ?? 0;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<NativeCacheEntry>> ListEntriesAsync(string folderId, string? after, int limit, CancellationToken ct = default)
    {
        if (!_folders.Any(folder => folder.Id == folderId) || limit is < 1 or > 200 || after?.Length > 64)
            throw new ArgumentException("Select a configured folder and a page size between 1 and 200.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var command = Command("SELECT Key,Folder,ItemId,Length,Bytes,VerifiedBytes,Pinned FROM Entries WHERE Folder=$folder AND Key>$after ORDER BY Key LIMIT $limit",
                ("$folder", folderId), ("$after", after ?? ""), ("$limit", limit));
            using var reader = command.ExecuteReader();
            var entries = new List<NativeCacheEntry>();
            while (reader.Read()) entries.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6) != 0));
            return entries;
        }
        finally { _gate.Release(); }
    }

    public async Task SetPinnedKeyAsync(string key, bool pinned, CancellationToken ct = default)
    {
        if (key.Length != 64 || !key.All(char.IsAsciiHexDigit)) throw new ArgumentException("Invalid cache entry key.");
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var command = Command("UPDATE Entries SET Pinned=$pinned WHERE Key=$key", ("$pinned", pinned ? 1 : 0), ("$key", key));
                if (command.ExecuteNonQuery() == 0) throw new ArgumentException("Cache entry no longer exists.");
            }
            finally { _gate.Release(); }
        }
        finally { _writer.Release(); }
    }

    private void EnsureMetadataColumns()
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        using (var command = Command("PRAGMA table_info(Entries)"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) columns.Add(reader.GetString(1));
        if (!columns.Contains("ItemId")) Execute("ALTER TABLE Entries ADD COLUMN ItemId TEXT NOT NULL DEFAULT ''");
        if (!columns.Contains("VerifiedBytes"))
        {
            Execute("ALTER TABLE Entries ADD COLUMN VerifiedBytes INTEGER NOT NULL DEFAULT 0");
            Execute("UPDATE Entries SET VerifiedBytes=(SELECT COALESCE(SUM(Count),0) FROM Blocks WHERE Blocks.Key=Entries.Key)");
        }
    }

    public async Task<IDisposable?> ReserveWarmAsync(NativeCacheIdentity identity, long bytesToFetch, CancellationToken cancellationToken = default)
    {
        if (bytesToFetch < 0 || bytesToFetch > identity.Length) throw new ArgumentOutOfRangeException(nameof(bytesToFetch));
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NativeCacheFolder[] candidates;
            long required;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                lock (_leaseLock) if (_reservations.Count >= 4 || _reservations.ContainsKey(identity.Key)) return null;
                using var lookup = Command("SELECT Folder FROM Entries WHERE Key=$key", ("$key", identity.Key));
                var existing = lookup.ExecuteScalar() as string;
                required = checked(bytesToFetch + ((bytesToFetch + BlockSize - 1) / BlockSize) * (65536 + 4096)
                    + (existing is null ? EntryOverhead : 0));
                candidates = _folders.Where(folder => (existing is null || folder.Id == existing) && HasQuota(folder, required)).ToArray();
            }
            finally { _gate.Release(); }
            var selected = candidates.FirstOrDefault(folder => CanUseFolder(folder, required + ReservedBytes(folder.Id)));
            if (selected is null) return null;
            var reservation = new WarmReservation(this, identity.Key, selected.Id, required, AcquireLease(identity));
            lock (_leaseLock) _reservations.Add(identity.Key, reservation);
            return reservation;
        }
        finally { _writer.Release(); }
    }

    public async Task<long> FindNextMissingOffsetAsync(NativeCacheIdentity identity, long start, long end, CancellationToken cancellationToken = default)
    {
        if (start < 0 || end < start || end > identity.Length || start % BlockSize != 0)
            throw new ArgumentOutOfRangeException(nameof(start));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = Command("SELECT Offset,Count FROM Blocks WHERE Key=$key AND Offset>=$start AND Offset<$end ORDER BY Offset",
                ("$key", identity.Key), ("$start", start), ("$end", end));
            using var reader = command.ExecuteReader();
            var position = start;
            while (reader.Read() && reader.GetInt64(0) == position)
            {
                cancellationToken.ThrowIfCancellationRequested();
                position += reader.GetInt32(1);
            }
            return Math.Min(position, end);
        }
        finally { _gate.Release(); }
    }

    private long ReservedBytes(string folder, string? except = null)
    {
        lock (_leaseLock) return _reservations.Where(pair => pair.Key != except && pair.Value.Folder == folder).Sum(pair => pair.Value.Remaining);
    }

    private sealed class WarmReservation(NativeCacheStore store, string key, string folder, long remaining, IDisposable lease) : IDisposable
    {
        public string Folder { get; } = folder;
        public long Remaining { get; set; } = remaining;
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (store._leaseLock) store._reservations.Remove(key);
            lease.Dispose();
        }
    }

    /// <summary>Explicit, streaming reconciliation; never enumerates the NAS during normal startup.</summary>
    public async Task<int> ScanAsync(string folderId, CancellationToken cancellationToken = default)
    {
        var folder = _folders.FirstOrDefault(folder => folder.Id == folderId && folder.Enabled)
            ?? throw new ArgumentException("Unknown or disabled native cache folder.", nameof(folderId));
        var root = Path.Combine(folder.Path, "v1");
        if (!IsVolumeCurrent(folder) || !Directory.Exists(root) || new DirectoryInfo(root).LinkTarget is not null) return 0;
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
                await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                        Execute("INSERT OR IGNORE INTO Entries(Key,Folder,Length,Bytes,Access,ItemId) VALUES($key,$folder,$length,$bytes,$access,$item)",
                            ("$key", manifest.Identity.Key), ("$folder", folder.Id), ("$length", manifest.Identity.Length),
                            ("$bytes", EntryOverhead), ("$access", DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300), ("$item", manifest.Identity.ItemId));
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
                finally { _writer.Release(); }
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

    private bool HasQuota(NativeCacheFolder folder, long requested, string? reservationKey = null)
    {
        if (!folder.Enabled || folder.ReadOnly || !_owners.ContainsKey(folder.Id)) return false;
        using var command = Command("SELECT Bytes FROM FolderTotals WHERE Folder=$folder", ("$folder", folder.Id));
        return requested <= folder.MaxBytes - (command.ExecuteScalar() as long? ?? 0) - ReservedBytes(folder.Id, reservationKey);
    }

    private bool CanUseFolder(NativeCacheFolder folder, long requested)
    {
        if (!folder.Enabled || folder.ReadOnly || !_owners.ContainsKey(folder.Id) || !IsVolumeCurrent(folder)) return false;
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

    public async ValueTask<IDisposable> AcquireFillAsync(NativeCacheIdentity identity, long offset, CancellationToken cancellationToken)
    {
        var key = (identity.Key, offset);
        FillState state;
        lock (_leaseLock)
        {
            if (!_fills.TryGetValue(key, out state!)) _fills[key] = state = new FillState();
            state.References++;
        }
        try { await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch { ReleaseFill(key, state, acquired: false); throw; }
        return new FillLease(this, key, state);
    }

    private void ReleaseFill((string Key, long Offset) key, FillState state, bool acquired)
    {
        if (acquired) state.Gate.Release();
        lock (_leaseLock)
        {
            if (--state.References != 0) return;
            _fills.Remove(key);
            state.Gate.Dispose();
        }
    }

    private sealed class FillState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int References;
    }
    private sealed class FillLease(NativeCacheStore store, (string Key, long Offset) key, FillState state) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) store.ReleaseFill(key, state, acquired: true); }
    }

    public async Task SetPinnedAsync(NativeCacheIdentity identity, bool pinned, CancellationToken cancellationToken = default)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { Execute("UPDATE Entries SET Pinned=$pinned WHERE Key=$key", ("$pinned", pinned ? 1 : 0), ("$key", identity.Key)); }
        finally { _gate.Release(); }
        }
        finally { _writer.Release(); }
    }

    /// <summary>Deletes only indexed application-owned entries, in bounded pages. Active and pinned entries are retained.</summary>
    public async Task<int> EvictAsync(string folderId, bool clear = false, CancellationToken cancellationToken = default)
    {
        var folder = _folders.FirstOrDefault(folder => folder.Id == folderId);
        if (folder is null || folder.ReadOnly || !_owners.ContainsKey(folder.Id) || !IsVolumeCurrent(folder)) return 0;
        if (!clear && folder.MaxAgeDays == 0) return 0;
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-folder.MaxAgeDays).ToUnixTimeSeconds() / 300;
            var keys = new List<string>(64);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
            using var query = Command("SELECT Key FROM Entries WHERE Folder=$folder AND Pinned=0 AND ($clear=1 OR Access<$cutoff) ORDER BY Access LIMIT 64",
                ("$folder", folderId), ("$clear", clear ? 1 : 0), ("$cutoff", cutoff));
            using var reader = query.ExecuteReader();
            while (reader.Read()) keys.Add(reader.GetString(0));
            }
            finally { _gate.Release(); }
            var deleted = 0;
            foreach (var key in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_leaseLock)
                {
                    if (_leases.ContainsKey(key)) continue;
                    _evicting.Add(key);
                }
                try
                {
                    var directory = EntryPath(folder, key);
                    if (!IsVolumeCurrent(folder) || !HasSafeLayout(folder, key)) continue;
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
                        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try { Execute("DELETE FROM Entries WHERE Key=$key", ("$key", key)); }
                        finally { _gate.Release(); }
                        deleted++;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                finally { lock (_leaseLock) _evicting.Remove(key); }
            }
            return deleted;
        }
        finally { _writer.Release(); }
    }

    public async Task<int> EvictPressureAsync(string folderId, CancellationToken cancellationToken = default)
    {
        var folder = _folders.FirstOrDefault(candidate => candidate.Id == folderId);
        if (folder is null) return 0;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool pressure;
        try
        {
            using var total = Command("SELECT Bytes FROM FolderTotals WHERE Folder=$folder", ("$folder", folder.Id));
            var bytes = total.ExecuteScalar() as long? ?? 0;
            pressure = bytes >= folder.MaxBytes * 0.9 || !HasQuota(folder, BlockSize + EntryOverhead);
        }
        finally { _gate.Release(); }
        // One bounded page per maintenance tick; never a full-library sweep.
        pressure |= !CanUseFolder(folder, BlockSize + EntryOverhead);
        return pressure ? await EvictAsync(folderId, clear: true, cancellationToken).ConfigureAwait(false) : 0;
    }

    private bool RegisterVolume(NativeCacheFolder folder)
    {
        if (!HasSafeLayout(folder, new string('0', 64))) return false;
        var path = Path.Combine(folder.Path, ".infinidysk-volume");
        if (new FileInfo(path).LinkTarget is not null) return false;
        using var lookup = Command("SELECT Volume FROM FolderIdentity WHERE Folder=$folder", ("$folder", folder.Id));
        var expected = lookup.ExecuteScalar() as string;
        var actual = ReadVolume(path);
        // An already registered folder must never claim the empty directory left
        // behind when a NAS is unmounted, including after an application restart.
        if (expected is not null && actual != expected) return false;
        if (actual is null)
        {
            if (folder.ReadOnly) return false;
            actual = Guid.NewGuid().ToString("N");
            using var marker = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            marker.Write(System.Text.Encoding.ASCII.GetBytes(actual));
            marker.Flush(flushToDisk: true);
            NativeFileSystem.FlushDirectory(folder.Path);
        }
        Execute("INSERT OR IGNORE INTO FolderIdentity(Folder,Volume) VALUES($folder,$volume)",
            ("$folder", folder.Id), ("$volume", actual));
        _volumes[folder.Id] = actual;
        return true;
    }

    private bool IsVolumeCurrent(NativeCacheFolder folder)
    {
        try
        {
            return _volumes.TryGetValue(folder.Id, out var expected)
                && ReadVolume(Path.Combine(folder.Path, ".infinidysk-volume")) == expected;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string? ReadVolume(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length != 32) return null;
        var text = File.ReadAllText(path);
        return Guid.TryParseExact(text, "N", out _) ? text : null;
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
    private static bool HasSafeLayout(NativeCacheFolder folder, string key)
    {
        for (var current = new DirectoryInfo(EntryPath(folder, key)); current is not null; current = current.Parent)
            if (current.LinkTarget is not null) return false;
        return new[] { "content.data", "manifest.json", "ranges.journal" }
            .All(name => new FileInfo(Path.Combine(EntryPath(folder, key), name)).LinkTarget is null);
    }
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
        await _writer.WaitAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var owner in _owners.Values) await owner.DisposeAsync().ConfigureAwait(false);
            await _database.DisposeAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); _writer.Release(); }
        // Do not dispose the managed-only gate: already queued operations must wake
        // and observe _disposed, and repeated disposal is supported. No WaitHandle is used.
    }

    private sealed record Manifest(int Version, NativeCacheIdentity Identity);
    private sealed record JournalBlock(long Offset, int Count, string Hash);
}
