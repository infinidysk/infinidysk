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
    private readonly Dictionary<string, NativeFileSystem.PinnedDirectory> _roots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _volumes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _leases = new(StringComparer.Ordinal);
    private readonly HashSet<string> _evicting = new(StringComparer.Ordinal);
    private readonly HashSet<string> _scanning = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Folder, bool Clear, bool Pressure), (long Access, string Key)> _evictionCursors = [];
    private readonly HashSet<string> _pressuredFolders = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Key, long Offset), FillState> _fills = [];
    private readonly Dictionary<string, WarmReservation> _reservations = new(StringComparer.Ordinal);
    private readonly Lock _leaseLock = new();
    private bool _disposed;
    internal Func<string, CancellationToken, Task>? BeforeScanReadAsync { get; init; }
    internal Func<string, long>? AvailableBytesOverride { get; init; }
    internal Func<NativeFileSystem.PinnedDirectory, string, CancellationToken, Task>? BeforeProbeReadAsync { get; init; }

    public async Task<NativeCacheProbeResult> ProbeAsync(string folderId, CancellationToken cancellationToken = default)
    {
        var folder = _folders.FirstOrDefault(candidate => candidate.Id == folderId && candidate.Enabled)
            ?? throw new ArgumentException("Unknown or disabled native cache folder.", nameof(folderId));
        var result = new NativeCacheProbeResult("unknown", "unknown", false, false, false, 0, null);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsVolumeCurrent(folder)) return result with { Error = "Registered storage is unavailable or its identity changed." };
            var root = _roots[folder.Id];
            var filesystem = root.FileSystem;
            result = result with { FileSystem = filesystem.FileSystem, Capability = filesystem.Capability, Readable = true,
                AvailableBytes = AvailableBytesOverride?.Invoke(folder.Id) ?? root.AvailableBytes };
            if (folder.ReadOnly) return result;
            if (!_owners.ContainsKey(folderId)) return result with { Error = "Folder is not exclusively owned for writes." };
            long freeRequired;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { freeRequired = FreeSpaceRequirement(folder, 4096); }
            finally { _gate.Release(); }
            if (result.AvailableBytes < freeRequired) return result with { Error = "Insufficient unreserved space for a safe write probe." };
            await ProbeRoundTripAsync(root, cancellationToken).ConfigureAwait(false);
            if (!IsVolumeCurrent(folder)) return result with { Readable = false, Error = "Storage identity changed during the probe." };
            return result with { Writable = true, DurableWriteVerified = true };
        }
        catch (IOException) { return result with { Error = "Storage did not complete a verified write, flush, read and cleanup probe." }; }
        catch (UnauthorizedAccessException) { return result with { Error = "Storage permissions prevented the probe." }; }
        finally { _writer.Release(); }
    }

    private async Task ProbeRoundTripAsync(NativeFileSystem.PinnedDirectory root, CancellationToken cancellationToken)
    {
        var name = ".infinidysk-probe-" + Guid.NewGuid().ToString("N");
        var created = false;
        try
        {
            var expected = RandomNumberGenerator.GetBytes(4096);
            await using var file = root.OpenFile(name, FileMode.CreateNew, FileAccess.ReadWrite);
            created = true;
            await file.WriteAsync(expected, cancellationToken).ConfigureAwait(false);
            file.Flush(flushToDisk: true);
            root.Flush();
            if (BeforeProbeReadAsync is { } beforeRead) await beforeRead(root, name, cancellationToken).ConfigureAwait(false);
            file.Position = 0;
            var actual = new byte[expected.Length];
            await file.ReadExactlyAsync(actual, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual)) throw new IOException("Probe readback mismatch.");
        }
        finally
        {
            if (created)
            {
                root.DeleteFile(name);
                root.Flush();
            }
        }
    }

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
            CREATE INDEX IF NOT EXISTS EntryEvictionCursor ON Entries(Folder, Pinned, Access, Key);
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
        Execute("CREATE INDEX IF NOT EXISTS EntryPendingReservation ON Entries(Folder,PendingBytes) WHERE PendingBytes>0");
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
                _owners[folder.Id] = _roots[folder.Id].OpenFile(".infinidysk-owner",
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, exclusive: true);
            }
            catch (IOException) { /* Locked/offline folders remain readable but cannot accept writes. */ }
            catch (UnauthorizedAccessException) { }
            catch (PlatformNotSupportedException) { /* Unsupported platforms fail closed. */ }
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
            using var directory = OpenEntry(folder, identity.Key);
            data = directory.OpenFile("content.data", FileMode.Open, FileAccess.Read);
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
        CancellationToken cancellationToken = default, bool waitForWriter = false)
    {
        ValidateOffset(identity, offset);
        if (data.Length != Math.Min(BlockSize, identity.Length - offset))
            throw new ArgumentException("Only complete integrity blocks may be published.", nameof(data));
        using var lease = AcquireLease(identity);
        if (waitForWriter) await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        else if (!await _writer.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return false;
        var catalogueHeld = false;
        try
        {
            lock (_leaseLock) if (_scanning.Contains(identity.Key)) return false;
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
                && HasQuota(candidate, required, identity.Key))
                .Select(candidate => (Folder: candidate, FreeRequired: FreeSpaceRequirement(candidate, required, identity.Key))).ToArray();
            _gate.Release();
            catalogueHeld = false;
            var folder = candidates.FirstOrDefault(candidate => CanUseFolder(candidate.Folder, candidate.FreeRequired)).Folder;
            if (folder is null) return false;

            using var directory = OpenEntry(folder, identity.Key, create: true);
            if (folderId is null)
            {
                await using (var manifest = directory.OpenFile("manifest.json", FileMode.Create, FileAccess.Write))
                {
                    await manifest.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new Manifest(1, identity)), cancellationToken).ConfigureAwait(false);
                    manifest.Flush(flushToDisk: true);
                }
            }
            await using var journal = directory.OpenFile("ranges.journal", FileMode.OpenOrCreate, FileAccess.ReadWrite);
            await RepairJournalTailAsync(journal, cancellationToken).ConfigureAwait(false);
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
            Execute("UPDATE Entries SET Bytes=Bytes+$bytes,PendingBytes=PendingBytes+$bytes WHERE Key=$key", ("$bytes", allocation), ("$key", identity.Key));

            var hash = SHA256.HashData(data.Span);
            // A slow NAS write must not hold the local catalogue gate or stall hits
            // in other folders. The writer gate and activity lease protect publication.
            _gate.Release();
            catalogueHeld = false;
            await using (var stream = directory.OpenFile("content.data", FileMode.OpenOrCreate, FileAccess.Write))
            {
                stream.Position = offset;
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            // The colocated journal makes explicit scans/imports possible without the local catalogue.
            var record = JsonSerializer.SerializeToUtf8Bytes(new JournalBlock(offset, data.Length, Convert.ToHexString(hash)));
            await journal.WriteAsync(record, cancellationToken).ConfigureAwait(false);
            await journal.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            journal.Flush(flushToDisk: true);
            directory.Flush();
            using (var shard = _roots[folder.Id].OpenDirectory($"v1/{identity.Key[..2]}")) shard.Flush();
            using (var version = _roots[folder.Id].OpenDirectory("v1")) version.Flush();
            _roots[folder.Id].Flush();
            var allocated = checked(directory.AllocatedBytes("content.data")
                + directory.AllocatedBytes("manifest.json") + directory.AllocatedBytes("ranges.journal") + EntryOverhead);
            if (!IsVolumeCurrent(folder)) return false;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            catalogueHeld = true;
            using var transaction = _database.BeginTransaction();
            using (var insert = Command("INSERT OR IGNORE INTO Blocks(Key,Offset,Count,Hash) VALUES($key,$offset,$count,$hash)",
                ("$key", identity.Key), ("$offset", offset), ("$count", data.Length), ("$hash", hash)))
            {
                insert.Transaction = transaction;
                insert.ExecuteNonQuery();
            }
            using (var update = Command("UPDATE Entries SET Dirty=0,PendingBytes=0,Bytes=$bytes WHERE Key=$key", ("$key", identity.Key), ("$bytes", allocated)))
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
        catch (IOException) { return false; }
        catch (InvalidDataException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
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
                Error = online ? null : "Folder is unavailable or its storage identity changed; no fallback directory will be created. Restore the original mount, or explicitly register the verified path with a new folder ID and scan it." };
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
        var identityColumns = new HashSet<string>(StringComparer.Ordinal);
        using (var command = Command("PRAGMA table_info(FolderIdentity)"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) identityColumns.Add(reader.GetString(1));
        if (!identityColumns.Contains("RootIdentity")) Execute("ALTER TABLE FolderIdentity ADD COLUMN RootIdentity TEXT");
        var columns = new HashSet<string>(StringComparer.Ordinal);
        using (var command = Command("PRAGMA table_info(Entries)"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) columns.Add(reader.GetString(1));
        if (!columns.Contains("ItemId")) Execute("ALTER TABLE Entries ADD COLUMN ItemId TEXT NOT NULL DEFAULT ''");
        if (!columns.Contains("PendingBytes")) Execute("ALTER TABLE Entries ADD COLUMN PendingBytes INTEGER NOT NULL DEFAULT 0");
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
            (NativeCacheFolder Folder, long FreeRequired)[] candidates;
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
                candidates = _folders.Where(folder => (existing is null || folder.Id == existing) && HasQuota(folder, required))
                    .Select(folder => (folder, FreeSpaceRequirement(folder, required))).ToArray();
            }
            finally { _gate.Release(); }
            var selected = candidates.FirstOrDefault(candidate => CanUseFolder(candidate.Folder, candidate.FreeRequired)).Folder;
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

    public async Task<long> GetMissingRangeBytesAsync(NativeCacheIdentity identity, long start, long end, CancellationToken cancellationToken = default)
    {
        if (start < 0 || end < start || end > identity.Length || start % BlockSize != 0 || (end != identity.Length && end % BlockSize != 0))
            throw new ArgumentOutOfRangeException(nameof(start), "Missing-byte queries require complete integrity-block bounds.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var query = Command("SELECT COALESCE(SUM(Count),0) FROM Blocks WHERE Key=$key AND Offset>=$start AND Offset<$end",
                ("$key", identity.Key), ("$start", start), ("$end", end));
            return Math.Max(0, end - start - (long)query.ExecuteScalar()!);
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
        if (!IsVolumeCurrent(folder)) return 0;
        NativeFileSystem.PinnedDirectory root;
        try { root = _roots[folder.Id].OpenDirectory("v1"); }
        catch (IOException) { return 0; }
        using var rootLease = root;
        var imported = 0;
        var buffer = new byte[BlockSize];
        foreach (var shardName in root.EnumerateDirectoryNames())
        {
            if (shardName.Length != 2 || !shardName.All(char.IsAsciiHexDigit)) continue;
            NativeFileSystem.PinnedDirectory shard;
            try { shard = root.OpenDirectory(shardName); }
            catch (IOException) { continue; }
            using var shardLease = shard;
            foreach (var key in shard.EnumerateDirectoryNames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (key.Length != 64 || !key.All(char.IsAsciiHexDigit) || !key.StartsWith(shardName, StringComparison.Ordinal)) continue;
                var writerHeld = false;
                IDisposable? scanLease = null;
                var scanning = false;
                try
                {
                    // Admit by the validated directory key before touching manifest/data.
                    // Eviction cannot unlink open-but-not-yet-leased scan handles, and a
                    // first publication cannot race manifest parsing.
                    await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
                    writerHeld = true;
                    lock (_leaseLock)
                    {
                        if (!_scanning.Add(key)) continue;
                        scanning = true;
                        scanLease = AcquireKeyLease(key);
                    }
                    _writer.Release();
                    writerHeld = false;
                    if (BeforeScanReadAsync is { } beforeRead) await beforeRead(key, cancellationToken).ConfigureAwait(false);
                    using var directory = shard.OpenDirectory(key);
                    await using var manifestStream = directory.OpenFile("manifest.json", FileMode.Open, FileAccess.Read);
                    if (manifestStream.Length > 64 * 1024) continue;
                    var manifestBytes = new byte[checked((int)manifestStream.Length)];
                    await manifestStream.ReadExactlyAsync(manifestBytes, cancellationToken).ConfigureAwait(false);
                    var manifest = JsonSerializer.Deserialize<Manifest>(manifestBytes);
                    if (manifest is not { Version: 1, Identity: { ItemId: not null, Generation: not null } } || manifest.Identity.Length <= 0
                        || !string.Equals(key, manifest.Identity.Key, StringComparison.Ordinal) || !IsVolumeCurrent(folder)) continue;
                    await using var data = directory.OpenFile("content.data", FileMode.Open, FileAccess.Read);
                    await using var journal = directory.OpenFile("ranges.journal", FileMode.Open, FileAccess.Read);
                    var physicalBytes = checked(directory.AllocatedBytes("content.data") + directory.AllocatedBytes("manifest.json")
                        + directory.AllocatedBytes("ranges.journal") + EntryOverhead);
                    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        ObjectDisposedException.ThrowIf(_disposed, this);
                        using var owner = Command("SELECT Folder FROM Entries WHERE Key=$key", ("$key", manifest.Identity.Key));
                        if (owner.ExecuteScalar() is string existingFolder && existingFolder != folder.Id)
                        {
                            if (_folders.Any(candidate => candidate.Id == existingFolder)) continue;
                            // Only an explicit scan may reassign an absent configuration owner.
                            // Drop its local coverage before verifying the newly registered root.
                            Execute("DELETE FROM Entries WHERE Key=$key", ("$key", key));
                        }
                        Execute("INSERT OR IGNORE INTO Entries(Key,Folder,Length,Bytes,Access,ItemId,Dirty) VALUES($key,$folder,$length,$bytes,$access,$item,1)",
                            ("$key", manifest.Identity.Key), ("$folder", folder.Id), ("$length", manifest.Identity.Length),
                            ("$bytes", physicalBytes), ("$access", DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300), ("$item", manifest.Identity.ItemId));
                        Execute("UPDATE Entries SET Bytes=MAX(Bytes,$bytes),Dirty=1 WHERE Key=$key", ("$key", key), ("$bytes", physicalBytes));
                        // Rebuild coverage from this scan's verified bytes; stale catalogue
                        // blocks must not survive a failed checksum or a truncated data file.
                        Execute("DELETE FROM Blocks WHERE Key=$key", ("$key", key));
                    }
                    finally { _gate.Release(); }
                    using var lines = new StreamReader(journal);
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
                        if (!IsVolumeCurrent(folder)) break;
                        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            ObjectDisposedException.ThrowIf(_disposed, this);
                            using var insert = Command("INSERT OR IGNORE INTO Blocks(Key,Offset,Count,Hash) VALUES($key,$offset,$count,$hash)",
                                ("$key", manifest.Identity.Key), ("$offset", block.Offset), ("$count", block.Count), ("$hash", hash));
                            insert.ExecuteNonQuery();
                        }
                        finally { _gate.Release(); }
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsVolumeCurrent(folder)) continue;
                    physicalBytes = checked(directory.AllocatedBytes("content.data") + directory.AllocatedBytes("manifest.json")
                        + directory.AllocatedBytes("ranges.journal") + EntryOverhead);
                    await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
                    writerHeld = true;
                    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        ObjectDisposedException.ThrowIf(_disposed, this);
                        Execute("UPDATE Entries SET Bytes=$bytes,Dirty=0,PendingBytes=0 WHERE Key=$key AND Folder=$folder",
                            ("$bytes", physicalBytes), ("$key", key), ("$folder", folder.Id));
                    }
                    finally { _gate.Release(); }
                    imported++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (JsonException) { }
                finally
                {
                    lock (_leaseLock)
                    {
                        if (scanning) _scanning.Remove(key);
                        scanLease?.Dispose();
                    }
                    if (writerHeld) _writer.Release();
                }
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
            if (read == 0) return null; // Only newline-terminated records crossed the journal commit boundary.
            if (line[count] == '\n') return new string(line, 0, count);
            count++;
        }
        throw new InvalidDataException("Oversized native-cache journal record.");
    }

    private static async Task RepairJournalTailAsync(FileStream journal, CancellationToken cancellationToken)
    {
        var length = journal.Length;
        if (length == 0) return;
        journal.Position = length - 1;
        var last = new byte[1];
        await journal.ReadExactlyAsync(last, cancellationToken).ConfigureAwait(false);
        if (last[0] == '\n') return;
        // Recovery belongs off foreground IO when corruption exceeds this window.
        // Never scan an arbitrarily large journal backwards during a cache fill.
        var count = (int)Math.Min(length, 64 * 1024);
        var tail = new byte[count];
        var start = length - count;
        journal.Position = start;
        await journal.ReadExactlyAsync(tail, cancellationToken).ConfigureAwait(false);
        var newline = tail.AsSpan().LastIndexOf((byte)'\n');
        if (newline < 0 && start > 0) throw new InvalidDataException("Native cache journal tail exceeds bounded recovery.");
        var committedLength = start + newline + 1;
        journal.SetLength(committedLength);
        journal.Flush(flushToDisk: true);
        journal.Position = committedLength;
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
            return requested <= (AvailableBytesOverride?.Invoke(folder.Id) ?? _roots[folder.Id].AvailableBytes);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    // Called under the local catalogue gate. Free space is already net of physical
    // files, so only warm intent and uncertain pending allocation are added.
    // Never charge an entire physically allocated file again just because it is dirty.
    private long FreeSpaceRequirement(NativeCacheFolder folder, long requested, string? reservationKey = null)
    {
        var device = _roots[folder.Id].DeviceIdentity;
        var peers = _folders.Where(candidate => candidate.Enabled && _roots.TryGetValue(candidate.Id, out var root)
            && root.DeviceIdentity == device).ToArray();
        var ids = peers.Select(candidate => candidate.Id).ToHashSet(StringComparer.Ordinal);
        var required = (UInt128)(ulong)requested + (ulong)peers.Max(candidate => candidate.MinFreeBytes);
        lock (_leaseLock)
            foreach (var reservation in _reservations)
                if (reservation.Key != reservationKey && ids.Contains(reservation.Value.Folder)) required += (ulong)reservation.Value.Remaining;
        foreach (var id in ids)
        {
            using var dirty = Command("SELECT COALESCE(SUM(PendingBytes),0) FROM Entries WHERE PendingBytes>0 AND Folder=$folder", ("$folder", id));
            required += (ulong)(long)dirty.ExecuteScalar()!;
        }
        return required >= long.MaxValue ? long.MaxValue : (long)required;
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
        => AcquireKeyLease(identity.Key);

    private IDisposable AcquireKeyLease(string key)
    {
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
    public Task<int> EvictAsync(string folderId, bool clear = false, CancellationToken cancellationToken = default)
        => EvictCoreAsync(folderId, clear, pressure: false, cancellationToken);

    internal bool HasPendingClearPage(string folderId)
    { lock (_leaseLock) return _evictionCursors.ContainsKey((folderId, true, false)); }

    internal void ResetClearCursor(string folderId)
    { lock (_leaseLock) _evictionCursors.Remove((folderId, true, false)); }

    private async Task<int> EvictCoreAsync(string folderId, bool clear, bool pressure, CancellationToken cancellationToken)
    {
        var cursorId = (folderId, clear, pressure);
        var folder = _folders.FirstOrDefault(folder => folder.Id == folderId);
        if (folder is null || folder.ReadOnly || !_owners.ContainsKey(folder.Id) || !IsVolumeCurrent(folder)
            || (!clear && folder.MaxAgeDays == 0))
        {
            lock (_leaseLock) _evictionCursors.Remove(cursorId);
            return 0;
        }
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-folder.MaxAgeDays).ToUnixTimeSeconds() / 300;
            var deleted = 0;
            (long Access, string Key) cursor;
            lock (_leaseLock) cursor = _evictionCursors.GetValueOrDefault(cursorId, (long.MinValue, ""));
            var afterAccess = cursor.Access;
            var afterKey = cursor.Key;
            var examined = 0;
            while (deleted < 64 && examined < 256)
            {
                var keys = new List<(string Key, long Access)>(64);
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    using var query = Command("""
                        SELECT Key,Access FROM Entries WHERE Folder=$folder AND Pinned=0 AND ($clear=1 OR Access<$cutoff)
                        AND (Access>$afterAccess OR (Access=$afterAccess AND Key>$afterKey)) ORDER BY Access,Key LIMIT 64
                        """, ("$folder", folderId), ("$clear", clear ? 1 : 0), ("$cutoff", cutoff),
                        ("$afterAccess", afterAccess), ("$afterKey", afterKey));
                    using var reader = query.ExecuteReader();
                    while (reader.Read()) keys.Add((reader.GetString(0), reader.GetInt64(1)));
                }
                finally { _gate.Release(); }
                if (keys.Count == 0)
                {
                    lock (_leaseLock) _evictionCursors.Remove(cursorId);
                    break;
                }
                foreach (var candidate in keys)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (pressure && !await NeedsPressureAsync(folder, lowWater: true, cancellationToken).ConfigureAwait(false))
                    {
                        lock (_leaseLock)
                        {
                            _evictionCursors.Remove(cursorId);
                            _pressuredFolders.Remove(folderId);
                        }
                        return deleted;
                    }
                    examined++;
                    afterAccess = candidate.Access;
                    afterKey = candidate.Key;
                    lock (_leaseLock) _evictionCursors[cursorId] = (afterAccess, afterKey);
                    var key = candidate.Key;
                    lock (_leaseLock)
                    {
                        if (_leases.ContainsKey(key)) continue;
                        _evicting.Add(key);
                    }
                    try
                    {
                        if (!IsVolumeCurrent(folder))
                        {
                            lock (_leaseLock) _evictionCursors.Remove(cursorId);
                            return deleted;
                        }
                        // No arbitrary recursive deletion: remove our three known files only.
                        try
                        {
                            try
                            {
                                using var shard = _roots[folder.Id].OpenDirectory($"v1/{key[..2]}");
                                using var directory = shard.OpenDirectory(key);
                                directory.DeleteFile("content.data");
                                directory.DeleteFile("ranges.journal");
                                directory.DeleteFile("manifest.json");
                                shard.DeleteDirectory(key);
                                shard.Flush();
                            }
                            catch (IOException exception) when (NativeFileSystem.IsMissing(exception))
                            { /* Anchored payload was already removed; reclaim only while its root remains current. */ }
                            if (!IsVolumeCurrent(folder))
                            {
                                lock (_leaseLock) _evictionCursors.Remove(cursorId);
                                return deleted;
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
                    if (deleted == 64) break;
                }
                if (keys.Count < 64 && deleted < 64)
                {
                    lock (_leaseLock) _evictionCursors.Remove(cursorId);
                    break;
                }
            }
            return deleted;
        }
        finally { _writer.Release(); }
    }

    public async Task<int> EvictPressureAsync(string folderId, CancellationToken cancellationToken = default)
    {
        var folder = _folders.FirstOrDefault(candidate => candidate.Id == folderId);
        if (folder is null || !_owners.ContainsKey(folder.Id)) return 0;
        bool continuing;
        lock (_leaseLock) continuing = _pressuredFolders.Contains(folderId);
        if (await NeedsPressureAsync(folder, lowWater: continuing, cancellationToken).ConfigureAwait(false))
        {
            lock (_leaseLock) _pressuredFolders.Add(folderId);
            return await EvictCoreAsync(folderId, clear: true, pressure: true, cancellationToken).ConfigureAwait(false);
        }
        lock (_leaseLock)
        {
            _evictionCursors.Remove((folderId, true, true));
            _pressuredFolders.Remove(folderId);
        }
        return 0;
    }

    private async Task<bool> NeedsPressureAsync(NativeCacheFolder folder, bool lowWater, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool pressure;
        long freeRequired;
        try
        {
            using var total = Command("SELECT Bytes FROM FolderTotals WHERE Folder=$folder", ("$folder", folder.Id));
            var bytes = (decimal)(total.ExecuteScalar() as long? ?? 0) + ReservedBytes(folder.Id);
            var nextBlock = Math.Min(folder.MaxBytes, BlockSize + EntryOverhead);
            pressure = (lowWater ? bytes > folder.MaxBytes * 0.8m : bytes >= folder.MaxBytes * 0.9m) || !HasQuota(folder, nextBlock);
            freeRequired = FreeSpaceRequirement(folder, nextBlock);
        }
        finally { _gate.Release(); }
        return pressure || !CanUseFolder(folder, freeRequired);
    }

    private bool RegisterVolume(NativeCacheFolder folder)
    {
        var root = NativeFileSystem.PinDirectory(folder.Path);
        var registered = false;
        try
        {
            using var lookup = Command("SELECT Volume FROM FolderIdentity WHERE Folder=$folder", ("$folder", folder.Id));
            var expected = lookup.ExecuteScalar() as string;
            using var identityLookup = Command("SELECT RootIdentity FROM FolderIdentity WHERE Folder=$folder", ("$folder", folder.Id));
            var registeredIdentity = identityLookup.ExecuteScalar() as string;
            var actual = ReadVolume(root);
            // An already registered folder must never claim the empty directory left
            // behind when a NAS is unmounted, including after an application restart.
            if (expected is not null && actual != expected) return false;
            if (registeredIdentity is not null && registeredIdentity != root.RegistrationIdentity) return false;
            if (actual is null)
            {
                if (folder.ReadOnly) return false;
                actual = Guid.NewGuid().ToString("N");
                using var marker = root.OpenFile(".infinidysk-volume", FileMode.CreateNew, FileAccess.Write);
                marker.Write(System.Text.Encoding.ASCII.GetBytes(actual));
                marker.Flush(flushToDisk: true);
                root.Flush();
            }
            if (!root.IsCurrent(folder.Path)) return false;
            Execute("INSERT OR IGNORE INTO FolderIdentity(Folder,Volume) VALUES($folder,$volume)",
                ("$folder", folder.Id), ("$volume", actual));
            Execute("UPDATE FolderIdentity SET RootIdentity=$identity WHERE Folder=$folder AND RootIdentity IS NULL",
                ("$folder", folder.Id), ("$identity", root.RegistrationIdentity));
            _volumes[folder.Id] = actual;
            _roots[folder.Id] = root;
            registered = true;
            return true;
        }
        finally { if (!registered) root.Dispose(); }
    }

    private bool IsVolumeCurrent(NativeCacheFolder folder)
    {
        try
        {
            return _volumes.TryGetValue(folder.Id, out var expected)
                && _roots.TryGetValue(folder.Id, out var root) && root.IsCurrent(folder.Path)
                && ReadVolume(root) == expected;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string? ReadVolume(NativeFileSystem.PinnedDirectory root)
    {
        try
        {
            using var marker = root.OpenFile(".infinidysk-volume", FileMode.Open, FileAccess.Read);
            if (marker.Length != 32) return null;
            Span<byte> bytes = stackalloc byte[32];
            marker.ReadExactly(bytes);
            var text = System.Text.Encoding.ASCII.GetString(bytes);
            return Guid.TryParseExact(text, "N", out _) ? text : null;
        }
        catch (IOException) { return null; }
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
    private NativeFileSystem.PinnedDirectory OpenEntry(NativeCacheFolder folder, string key, bool create = false)
        => _roots[folder.Id].OpenDirectory($"v1/{key[..2]}/{key}", create);
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
            foreach (var root in _roots.Values) root.Dispose();
            await _database.DisposeAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); _writer.Release(); }
        // Do not dispose the managed-only gate: already queued operations must wake
        // and observe _disposed, and repeated disposal is supported. No WaitHandle is used.
    }

    private sealed record Manifest(int Version, NativeCacheIdentity Identity);
    private sealed record JournalBlock(long Offset, int Count, string Hash);
}

public sealed record NativeCacheProbeResult(string FileSystem, string Capability, bool Readable, bool Writable,
    bool DurableWriteVerified, long AvailableBytes, string? Error);
