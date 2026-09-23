using System.Diagnostics.CodeAnalysis;
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
    // FlushAsync only flushes managed buffers; publication requires durable fsync.
    private static void FlushToDisk(FileStream stream) => stream.Flush(flushToDisk: true);
    private const string LocalSqliteReason = "Microsoft.Data.Sqlite async APIs execute synchronously; bounded local catalogue operations intentionally remain under the short catalogue gate.";
    private readonly SqliteConnection _database;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Managed-only semaphore: no WaitHandle is created. Retained so queued waiters can observe disposal and release safely.")]
    private readonly SemaphoreSlim _gate = new(1, 1);
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Managed-only semaphore: no WaitHandle is created. Retained so queued waiters can observe disposal and release safely.")]
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly NativeCacheFolder[] _folders;
    private readonly Dictionary<string, SemaphoreSlim> _folderWriters = new(StringComparer.Ordinal);
    private SemaphoreSlim Writer(string folder) => _folderWriters.GetValueOrDefault(folder, _writer);

    [SuppressMessage("Performance", "CA1849", Justification = LocalSqliteReason)]
    private async Task<SemaphoreSlim> EntryWriterAsync(string key, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var query = Command("SELECT Folder FROM Entries WHERE Key=$key", ("$key", key));
            return query.ExecuteScalar() is string folder ? Writer(folder) : _writer;
        }
        finally { _gate.Release(); }
    }

    [SuppressMessage("Performance", "CA1849", Justification = LocalSqliteReason)]
    private async Task<(NativeCacheFolder Folder, SemaphoreSlim Writer)?> SelectWriterAsync(
        NativeCacheIdentity identity, long required, bool wait, CancellationToken ct)
    {
        NativeCacheFolder[] candidates;
        string? existing;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var query = Command("SELECT Folder FROM Entries WHERE Key=$key", ("$key", identity.Key));
            existing = query.ExecuteScalar() as string;
            string? reserved;
            lock (_leaseLock) reserved = _reservations.GetValueOrDefault(identity.Key)?.Folder;
            using var retiredQuery = Command("SELECT Folder FROM RetiredEntries WHERE Key=$key", ("$key", identity.Key));
            using var retiredReader = retiredQuery.ExecuteReader();
            var retired = new HashSet<string>(StringComparer.Ordinal);
            while (retiredReader.Read()) retired.Add(retiredReader.GetString(0));
            retiredReader.Close();
            candidates = _folders.Where(folder => !retired.Contains(folder.Id) && (existing is null || folder.Id == existing)
                && (reserved is null || folder.Id == reserved) && HasQuota(folder, required + (existing is null ? EntryOverhead : 0), identity.Key)).ToArray();
        }
        finally { _gate.Release(); }
        foreach (var folder in candidates)
        {
            var writer = Writer(folder.Id);
            if (!await writer.WaitAsync(0, ct).ConfigureAwait(false))
            {
                if (!wait || existing is null) continue;
                await writer.WaitAsync(ct).ConfigureAwait(false);
            }
            try
            {
                long freeRequired;
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try { freeRequired = FreeSpaceRequirement(folder, required + (existing is null ? EntryOverhead : 0), identity.Key); }
                finally { _gate.Release(); }
                if (CanUseFolder(folder, freeRequired)) return (folder, writer);
            }
            catch { writer.Release(); throw; }
            writer.Release();
        }
        return null;
    }
    private readonly Dictionary<string, FileStream> _owners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NativeFileSystem.PinnedDirectory> _roots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _volumes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _leases = new(StringComparer.Ordinal);
    private readonly HashSet<string> _evicting = new(StringComparer.Ordinal);
    private readonly HashSet<string> _scanning = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Folder, bool Clear, bool Pressure), (long Access, string Key)> _evictionCursors = [];
    private readonly HashSet<string> _pressuredFolders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pendingCheckpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Key, long Offset), FillState> _fills = [];
    private readonly Dictionary<(string Key, long Offset), Task<bool>> _verifications = [];

    internal Task<bool> VerifyOnceAsync(NativeCacheIdentity identity, long offset, Func<Task<bool>> verify, CancellationToken ct)
    {
        var key = (identity.Key, offset);
        TaskCompletionSource<bool> completion;
        lock (_leaseLock)
        {
            if (_verifications.TryGetValue(key, out var pending)) return pending.WaitAsync(ct);
            if (_verifications.Count >= 64) return verify();
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _verifications.Add(key, completion.Task);
        }
        _ = CompleteVerificationAsync(key, verify, completion);
        return completion.Task;
    }

    private async Task CompleteVerificationAsync((string Key, long Offset) key, Func<Task<bool>> verify, TaskCompletionSource<bool> completion)
    {
        try
        {
            var valid = await verify().ConfigureAwait(false);
            lock (_leaseLock) { _verifications.Remove(key); completion.TrySetResult(valid); }
        }
        catch (Exception exception)
        {
            lock (_leaseLock) { _verifications.Remove(key); completion.TrySetException(exception); }
        }
    }

    private readonly Dictionary<string, WarmReservation> _reservations = new(StringComparer.Ordinal);
    private readonly Lock _leaseLock = new();
    private bool _disposed;
    private readonly Dictionary<string, Task<bool>> _statusProbes = [];
    private readonly Dictionary<string, long> _statusFreeBytes = [];
    internal TimeSpan StatusProbeTimeout { get; init; } = TimeSpan.FromSeconds(1);
    internal Func<string, bool>? StatusProbeOverride { get; init; }

    private async Task<bool?> StatusOnlineAsync(NativeCacheFolder folder, CancellationToken ct)
    {
        Task<bool> probe;
        lock (_leaseLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_statusProbes.TryGetValue(folder.Id, out probe!) || probe.IsCompleted)
                _statusProbes[folder.Id] = probe = Task.Run(() =>
                {
                    var online = StatusProbeOverride?.Invoke(folder.Id) ?? IsVolumeCurrent(folder);
                    var available = online && _roots.TryGetValue(folder.Id, out var root)
                        ? AvailableBytesOverride?.Invoke(folder.Id) ?? root.GetAvailableBytes() : 0;
                    lock (_leaseLock) _statusFreeBytes[folder.Id] = available;
                    return online;
                });
        }
        try { return await probe.WaitAsync(StatusProbeTimeout, ct).ConfigureAwait(false); }
        catch (TimeoutException) { return null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    internal Func<string, CancellationToken, Task>? BeforeScanReadAsync { get; init; }
    internal Func<string, long>? AvailableBytesOverride { get; init; }
    internal Func<NativeFileSystem.PinnedDirectory, string, CancellationToken, Task>? BeforeProbeReadAsync { get; init; }
    internal Func<string, CancellationToken, Task>? CheckpointPhaseAsync { get; init; }
    internal Func<CancellationToken, Task>? BeforeWriteReserveAsync { get; init; }
    internal int PendingCheckpointCount { get { lock (_leaseLock) return _pendingCheckpoints.Count; } }

    private void QueueCheckpoint(string key, string folder)
    {
        lock (_leaseLock)
            if (_pendingCheckpoints.Count < 128) _pendingCheckpoints.TryAdd(key, folder);
    }

    public Task<int> ProcessOneCheckpointAsync(CancellationToken cancellationToken = default)
        => ProcessOneCheckpointAsync(null, cancellationToken);

    public async Task<int> ProcessOneCheckpointAsync(string? folderId, CancellationToken cancellationToken)
    {
        KeyValuePair<string, string> next;
        lock (_leaseLock)
        {
            if (_pendingCheckpoints.Count == 0) return 0;
            next = _pendingCheckpoints.FirstOrDefault(pair => folderId is null || pair.Value == folderId);
            if (next.Key is null) return 0;
            _pendingCheckpoints.Remove(next.Key);
        }
        return await ScanCoreAsync(next.Value, next.Key, cancellationToken).ConfigureAwait(false);
    }

    private static long JournalSoftLimit(long length)
        => (long)Math.Min(long.MaxValue - 65536m, Math.Max(4096m, decimal.Ceiling((decimal)length / BlockSize) * 256));

    [SuppressMessage("Performance", "CA1849", Justification = LocalSqliteReason)]
    internal async Task<DateTime> ModificationTimeAsync(string itemId, string generation, DateTime created, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var query = Command("SELECT Generation,Seconds FROM ModificationTimes WHERE ItemId=$item", ("$item", itemId));
            using var reader = query.ExecuteReader();
            var seconds = new DateTimeOffset(created == default ? DateTime.UnixEpoch : created.ToUniversalTime()).ToUnixTimeSeconds();
            if (reader.Read())
            {
                var previous = reader.GetInt64(1);
                if (reader.GetString(0) == generation) return DateTimeOffset.FromUnixTimeSeconds(previous).UtcDateTime;
                seconds = Math.Max(previous + 1, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
            reader.Close();
            Execute("INSERT INTO ModificationTimes(ItemId,Generation,Seconds) VALUES($item,$generation,$seconds) ON CONFLICT(ItemId) DO UPDATE SET Generation=excluded.Generation,Seconds=excluded.Seconds",
                ("$item", itemId), ("$generation", generation), ("$seconds", seconds));
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }
        finally { _gate.Release(); }
    }

    public async Task<NativeCacheProbeResult> ProbeAsync(string folderId, CancellationToken cancellationToken = default)
    {
        var folder = _folders.FirstOrDefault(candidate => candidate.Id == folderId && candidate.Enabled)
            ?? throw new ArgumentException("Unknown or disabled native cache folder.", nameof(folderId));
        var result = new NativeCacheProbeResult("unknown", "unknown", false, false, false, 0, null);
        await Writer(folderId).WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsVolumeCurrent(folder)) return result with { Error = "Registered storage is unavailable or its identity changed." };
            var root = _roots[folder.Id];
            var filesystem = root.GetFileSystem();
            result = result with { FileSystem = filesystem.FileSystem, Capability = filesystem.Capability, Readable = true,
                AvailableBytes = AvailableBytesOverride?.Invoke(folder.Id) ?? root.GetAvailableBytes() };
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
        finally { Writer(folderId).Release(); }
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
            FlushToDisk(file);
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
            CREATE TABLE IF NOT EXISTS ModificationTimes(ItemId TEXT PRIMARY KEY, Generation TEXT NOT NULL, Seconds INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS RetiredEntries(Key TEXT NOT NULL, Folder TEXT NOT NULL, Bytes INTEGER NOT NULL,
                PRIMARY KEY(Key,Folder));
            CREATE TABLE IF NOT EXISTS FolderTotals(Folder TEXT PRIMARY KEY, Bytes INTEGER NOT NULL DEFAULT 0);
            CREATE TRIGGER IF NOT EXISTS RetiredAdded AFTER INSERT ON RetiredEntries BEGIN
                INSERT INTO FolderTotals(Folder,Bytes) VALUES(NEW.Folder,NEW.Bytes)
                    ON CONFLICT(Folder) DO UPDATE SET Bytes=Bytes+NEW.Bytes;
            END;
            CREATE TRIGGER IF NOT EXISTS RetiredRemoved AFTER DELETE ON RetiredEntries BEGIN
                UPDATE FolderTotals SET Bytes=Bytes-OLD.Bytes WHERE Folder=OLD.Folder;
            END;
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
        var devices = new Dictionary<string, SemaphoreSlim>(StringComparer.Ordinal);
        foreach (var folder in _folders)
        {
            var device = _roots.TryGetValue(folder.Id, out var root) ? root.DeviceIdentity.ToString() : folder.Id;
            if (!devices.TryGetValue(device, out var writer)) devices[device] = writer = new SemaphoreSlim(1, 1);
            _folderWriters[folder.Id] = writer;
        }
    }

    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = LocalSqliteReason)]
    public async Task<int> ReadBlockAsync(NativeCacheIdentity identity, long offset, Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        ValidateOffset(identity, offset);
        using var lease = AcquireLease(identity);
        lock (_leaseLock) if (_evicting.Contains(identity.Key)) return 0;
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
            await using var data = directory.OpenFile("content.data", FileMode.Open, FileAccess.Read);
            data.Position = offset;
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
        catch (UnauthorizedAccessException) { return 0; }

    }

    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = LocalSqliteReason)]
    public async Task<bool> WriteBlockAsync(NativeCacheIdentity identity, long offset, ReadOnlyMemory<byte> data,
        bool waitForWriter = false, CancellationToken cancellationToken = default)
    {
        ValidateOffset(identity, offset);
        if (data.Length != Math.Min(BlockSize, identity.Length - offset))
            throw new ArgumentException("Only complete integrity blocks may be published.", nameof(data));
        using var lease = AcquireLease(identity);
        using var placement = await AcquireFillAsync(identity, -1, cancellationToken).ConfigureAwait(false);
        var selectedWriter = await SelectWriterAsync(identity, RoundAllocation(data.Length), waitForWriter, cancellationToken).ConfigureAwait(false);
        if (selectedWriter is not { } selection) return false;
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
            var candidates = _folders.Where(candidate => candidate.Id == selection.Folder.Id && (folderId is null || candidate.Id == folderId)
                && (reservedFolder is null || candidate.Id == reservedFolder)
                && HasQuota(candidate, required, identity.Key))
                .Select(candidate => (Folder: candidate, FreeRequired: FreeSpaceRequirement(candidate, required, identity.Key))).ToArray();
            _gate.Release();
            catalogueHeld = false;
            var folder = candidates.FirstOrDefault(candidate => CanUseFolder(candidate.Folder, candidate.FreeRequired)).Folder;
            if (folder is null) return false;

            if (BeforeWriteReserveAsync is { } beforeReserve) await beforeReserve(cancellationToken).ConfigureAwait(false);

            using var directory = OpenEntry(folder, identity.Key, create: true);
            if (folderId is null)
            {
                await using (var manifest = directory.OpenFile("manifest.json", FileMode.Create, FileAccess.Write))
                {
                    await manifest.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new Manifest(1, identity)), cancellationToken).ConfigureAwait(false);
                    FlushToDisk(manifest);
                }
            }
            await using var journal = directory.OpenFile("ranges.journal", FileMode.OpenOrCreate, FileAccess.ReadWrite);
            await RepairJournalTailAsync(journal, cancellationToken).ConfigureAwait(false);
            var softLimit = JournalSoftLimit(identity.Length);
            if (journal.Length >= softLimit) QueueCheckpoint(identity.Key, folder.Id);
            if (journal.Length > softLimit + 65536 - 256) return false;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            catalogueHeld = true;
            // Writers/eviction are serialized separately; readers need only this
            // short local transaction, even while another folder is stalled.
            if (folderId is null)
            {
                Execute("INSERT INTO Entries(Key,Folder,Length,Bytes,Access,Dirty,ItemId,Generation) VALUES($key,$folder,$length,$bytes,$access,1,$item,$generation)",
                    ("$key", identity.Key), ("$folder", folder.Id), ("$length", identity.Length),
                    ("$bytes", EntryOverhead), ("$access", DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300), ("$item", identity.ItemId),
                    ("$generation", identity.Generation));
            }
            else Execute("UPDATE Entries SET Dirty=1,Generation=$generation WHERE Key=$key", ("$key", identity.Key), ("$generation", identity.Generation));

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
                FlushToDisk(stream);
            }
            // The colocated journal makes explicit scans/imports possible without the local catalogue.
            var record = JsonSerializer.SerializeToUtf8Bytes(new JournalBlock(offset, data.Length, Convert.ToHexString(hash)));
            await journal.WriteAsync(record, cancellationToken).ConfigureAwait(false);
            await journal.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            FlushToDisk(journal);
            if (journal.Length >= softLimit) QueueCheckpoint(identity.Key, folder.Id);
            directory.Flush();
            using (var shard = _roots[folder.Id].OpenDirectory($"v1/{identity.Key[..2]}")) shard.Flush();
            using (var version = _roots[folder.Id].OpenDirectory("v1")) version.Flush();
            _roots[folder.Id].Flush();
            var allocated = PhysicalEntryBytes(directory);
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
            selection.Writer.Release();
        }
    }

    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = LocalSqliteReason)]
    public async Task<IReadOnlyList<NativeCacheFolderStatus>> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<NativeCacheFolderStatus>();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var folder in _folders)
            {
                using var command = Command("SELECT COALESCE((SELECT SUM(Bytes) FROM Entries WHERE Folder=$folder),0)+COALESCE((SELECT SUM(Bytes) FROM RetiredEntries WHERE Folder=$folder),0), (SELECT COUNT(*) FROM Entries WHERE Folder=$folder)+(SELECT COUNT(*) FROM RetiredEntries WHERE Folder=$folder)", ("$folder", folder.Id));
                using var reader = command.ExecuteReader();
                reader.Read();
                result.Add(new(folder.Id, false, false, reader.GetInt64(0), reader.GetInt64(1), null));
            }
        }
        finally { _gate.Release(); }
        return await Task.WhenAll(result.Select(async status =>
        {
            var folder = _folders.First(folder => folder.Id == status.Id);
            var online = await StatusOnlineAsync(folder, cancellationToken).ConfigureAwait(false);
            return status with { Online = online == true, Writable = online == true && _owners.ContainsKey(folder.Id),
                Error = online is null ? "Storage status is unknown: the filesystem check exceeded its deadline."
                    : online.Value ? null : "Folder is unavailable or its storage identity changed; restore the original mount before retrying." };
        })).ConfigureAwait(false);
    }

    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = LocalSqliteReason)]
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

    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = LocalSqliteReason)]
    public async Task<IReadOnlyList<NativeCacheEntry>> ListEntriesAsync(string folderId, string? after, int limit, CancellationToken ct = default)
    {
        if (!_folders.Any(folder => folder.Id == folderId) || limit is < 1 or > 200 || after?.Length > 64)
            throw new ArgumentException("Select a configured folder and a page size between 1 and 200.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var command = Command("SELECT Key,Folder,ItemId,Length,Bytes,VerifiedBytes,Pinned,Generation FROM Entries WHERE Folder=$folder AND Key>$after ORDER BY Key LIMIT $limit",
                ("$folder", folderId), ("$after", after ?? ""), ("$limit", limit));
            using var reader = command.ExecuteReader();
            var entries = new List<NativeCacheEntry>();
            while (reader.Read()) entries.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6) != 0,
                reader.IsDBNull(7) ? null : reader.GetString(7)));
            return entries;
        }
        finally { _gate.Release(); }
    }

    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = LocalSqliteReason)]
    public async Task<IReadOnlyList<NativeCacheVerifiedRange>> ListVerifiedRangesAsync(string key, long afterOffset, int limit,
        CancellationToken ct = default)
    {
        if (key is not { Length: 64 } || !key.All(char.IsAsciiHexDigit) || afterOffset < -1 || limit is < 1 or > 100)
            throw new ArgumentException("Select a valid entry key, offset cursor and page size between 1 and 100.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var command = Command("SELECT Offset,Count FROM Blocks WHERE Key=$key AND Offset>$after ORDER BY Offset LIMIT $limit",
                ("$key", key), ("$after", afterOffset), ("$limit", limit));
            using var reader = command.ExecuteReader();
            var ranges = new List<NativeCacheVerifiedRange>();
            while (reader.Read()) ranges.Add(new(reader.GetInt64(0), reader.GetInt32(1)));
            return ranges;
        }
        finally { _gate.Release(); }
    }

    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = LocalSqliteReason)]
    public async Task SetPinnedKeyAsync(string key, bool pinned, CancellationToken ct = default)
    {
        if (key.Length != 64 || !key.All(char.IsAsciiHexDigit)) throw new ArgumentException("Invalid cache entry key.");
        using var placement = await AcquireFillKeyAsync(key, -1, ct).ConfigureAwait(false);
        var writer = await EntryWriterAsync(key, ct).ConfigureAwait(false);
        await writer.WaitAsync(ct).ConfigureAwait(false);
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
        finally { writer.Release(); }
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
        if (!columns.Contains("Generation")) Execute("ALTER TABLE Entries ADD COLUMN Generation TEXT");
        if (!columns.Contains("VerifiedBytes"))
        {
            Execute("ALTER TABLE Entries ADD COLUMN VerifiedBytes INTEGER NOT NULL DEFAULT 0");
            Execute("UPDATE Entries SET VerifiedBytes=(SELECT COALESCE(SUM(Count),0) FROM Blocks WHERE Blocks.Key=Entries.Key)");
        }
    }

    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = LocalSqliteReason)]
    public async Task<IDisposable?> ReserveWarmAsync(NativeCacheIdentity identity, long bytesToFetch, CancellationToken cancellationToken = default)
    {
        if (bytesToFetch < 0 || bytesToFetch > identity.Length) throw new ArgumentOutOfRangeException(nameof(bytesToFetch));
        using var placement = await AcquireFillAsync(identity, -1, cancellationToken).ConfigureAwait(false);
        var reservationBytes = checked(bytesToFetch + ((bytesToFetch + BlockSize - 1) / BlockSize) * (65536 + 4096));
        var selectedWriter = await SelectWriterAsync(identity, reservationBytes, true, cancellationToken).ConfigureAwait(false);
        if (selectedWriter is not { } selection) return null;
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
                candidates = _folders.Where(folder => folder.Id == selection.Folder.Id && (existing is null || folder.Id == existing) && HasQuota(folder, required))
                    .Select(folder => (folder, FreeSpaceRequirement(folder, required))).ToArray();
            }
            finally { _gate.Release(); }
            var selected = candidates.FirstOrDefault(candidate => CanUseFolder(candidate.Folder, candidate.FreeRequired)).Folder;
            if (selected is null) return null;
            IDisposable? lease = null;
            try
            {
                lease = AcquireLease(identity);
                var reservation = new WarmReservation(this, identity.Key, selected.Id, required, lease);
                lock (_leaseLock) _reservations.Add(identity.Key, reservation);
                lease = null; // The published reservation now owns the activity lease.
                return reservation;
            }
            finally { lease?.Dispose(); }
        }
        finally { selection.Writer.Release(); }
    }

    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = LocalSqliteReason)]
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

    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = LocalSqliteReason)]
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

    /// <summary>Restarts a partial warm on another root without merging replicas or losing allocation debt.</summary>
    [SuppressMessage("Performance", "CA1849", Justification = LocalSqliteReason)]
    internal async Task<bool> RestartPartialWarmAsync(NativeCacheIdentity identity, long missing, CancellationToken ct)
    {
        if (missing <= 0) return false;
        using var placement = await AcquireFillAsync(identity, -1, ct).ConfigureAwait(false);
        string? oldFolder;
        long allocated;
        bool quota;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var query = Command("SELECT Folder,Bytes,Pinned FROM Entries WHERE Key=$key", ("$key", identity.Key));
            using var reader = query.ExecuteReader();
            if (!reader.Read() || reader.GetInt64(2) != 0) return false;
            oldFolder = reader.GetString(0);
            allocated = reader.GetInt64(1);
            reader.Close();
            var folder = _folders.FirstOrDefault(folder => folder.Id == oldFolder);
            if (folder is null || folder.ReadOnly) return false;
            lock (_leaseLock) if (_reservations.ContainsKey(identity.Key) || _scanning.Contains(identity.Key)) return false;
            quota = HasQuota(folder, checked(missing + ((missing + BlockSize - 1) / BlockSize) * (65536 + 4096)));
        }
        finally { _gate.Release(); }
        var original = _folders.First(folder => folder.Id == oldFolder);
        if (quota && await StatusOnlineAsync(original, ct).ConfigureAwait(false) == true) return false;
        var oldWriter = Writer(oldFolder);
        if (!await oldWriter.WaitAsync(0, ct).ConfigureAwait(false)) return false;
        try
        {
            foreach (var target in _folders.Where(folder => folder.Id != oldFolder && folder.Enabled && !folder.ReadOnly))
            {
                if (await StatusOnlineAsync(target, ct).ConfigureAwait(false) != true) continue;
                var targetWriter = Writer(target.Id);
                var distinct = !ReferenceEquals(oldWriter, targetWriter);
                if (distinct && !await targetWriter.WaitAsync(0, ct).ConfigureAwait(false)) continue;
                try
                {
                    await _gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        using var retired = Command("SELECT 1 FROM RetiredEntries WHERE Key=$key AND Folder=$folder", ("$key", identity.Key), ("$folder", target.Id));
                        var required = checked(identity.Length + EntryOverhead + ((identity.Length + BlockSize - 1) / BlockSize) * (65536 + 4096));
                        if (retired.ExecuteScalar() is not null || !HasQuota(target, required)) continue;
                        var freeRequired = FreeSpaceRequirement(target, required);
                        lock (_leaseLock) if (_statusFreeBytes.GetValueOrDefault(target.Id) < freeRequired) continue;
                        lock (_leaseLock) if (_scanning.Contains(identity.Key) || _reservations.ContainsKey(identity.Key)) return false;
                        using var current = Command("SELECT Bytes FROM Entries WHERE Key=$key AND Folder=$folder AND Pinned=0", ("$key", identity.Key), ("$folder", oldFolder));
                        if (current.ExecuteScalar() is not long currentBytes) return false;
                        allocated = currentBytes;
                        using var transaction = _database.BeginTransaction();
                        using var remember = Command("INSERT INTO RetiredEntries(Key,Folder,Bytes) VALUES($key,$folder,$bytes)",
                            ("$key", identity.Key), ("$folder", oldFolder), ("$bytes", allocated));
                        remember.Transaction = transaction;
                        remember.ExecuteNonQuery();
                        using var remove = Command("DELETE FROM Entries WHERE Key=$key", ("$key", identity.Key));
                        remove.Transaction = transaction;
                        remove.ExecuteNonQuery();
                        transaction.Commit();
                        return true;
                    }
                    finally { _gate.Release(); }
                }
                finally { if (distinct) targetWriter.Release(); }
            }
            return false;
        }
        finally { oldWriter.Release(); }
    }

    [SuppressMessage("Performance", "CA1849", Justification = LocalSqliteReason)]
    internal async Task<int> ReclaimRetiredAsync(string folderId, bool clear = false, CancellationToken ct = default)
    {
        var folder = _folders.First(folder => folder.Id == folderId);
        if (folder.ReadOnly || !_owners.ContainsKey(folderId)) return 0;
        var reclaimed = 0;
        var writer = Writer(folderId);
        await writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsVolumeCurrent(folder)) return reclaimed;
            List<string> keys = [];
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var query = Command("SELECT r.Key FROM RetiredEntries r LEFT JOIN Entries e ON e.Key=r.Key WHERE r.Folder=$folder AND ($clear=1 OR e.VerifiedBytes=e.Length) LIMIT 32", ("$folder", folderId), ("$clear", clear ? 1 : 0));
                using var reader = query.ExecuteReader();
                while (reader.Read()) keys.Add(reader.GetString(0));
            }
            finally { _gate.Release(); }
            foreach (var key in keys)
            {
                lock (_leaseLock) { if (_leases.ContainsKey(key)) continue; _evicting.Add(key); }
                try
                {
                    try
                    {
#pragma warning disable CA2000 // The using declaration owns the anchored shard through unlink/flush, including exceptions.
                        using var shard = _roots[folderId].OpenDirectory($"v1/{key[..2]}");
#pragma warning restore CA2000
                        using var directory = shard.OpenDirectory(key);
                        foreach (var name in new[] { "content.data", "ranges.journal", "manifest.json", "ranges.checkpoint.tmp" }) directory.DeleteFile(name);
                        shard.DeleteDirectory(key);
                        shard.Flush();
                    }
                    catch (IOException exception) when (NativeFileSystem.IsMissing(exception)) { }
                    if (!IsVolumeCurrent(folder)) return reclaimed;
                    await _gate.WaitAsync(ct).ConfigureAwait(false);
                    try { Execute("DELETE FROM RetiredEntries WHERE Key=$key AND Folder=$folder", ("$key", key), ("$folder", folderId)); }
                    finally { _gate.Release(); }
                    reclaimed++;
                }
                finally { lock (_leaseLock) _evicting.Remove(key); }
            }
        }
        finally { writer.Release(); }
        return reclaimed;
    }

    /// <summary>Explicit, streaming reconciliation; never enumerates the NAS during normal startup.</summary>
    public Task<int> ScanAsync(string folderId, CancellationToken cancellationToken = default)
        => ScanCoreAsync(folderId, null, cancellationToken);

    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = LocalSqliteReason)]
    private async Task<int> ScanCoreAsync(string folderId, string? onlyKey, CancellationToken cancellationToken)
    {
        var folder = _folders.FirstOrDefault(folder => folder.Id == folderId && folder.Enabled)
            ?? throw new ArgumentException("Unknown or disabled native cache folder.", nameof(folderId));
        if (!IsVolumeCurrent(folder)) return 0;
        NativeFileSystem.PinnedDirectory root;
#pragma warning disable CA2000 // Successful open immediately enters rootLease's using scope; a failed open returns no handle.
        try { root = _roots[folder.Id].OpenDirectory("v1"); }
#pragma warning restore CA2000
        catch (IOException) { return 0; }
        using var rootLease = root;
        var imported = 0;
        var buffer = new byte[BlockSize];
        foreach (var shardName in onlyKey is null ? root.EnumerateDirectoryNames() : [onlyKey[..2]])
        {
            if (shardName.Length != 2 || !shardName.All(char.IsAsciiHexDigit)) continue;
            NativeFileSystem.PinnedDirectory shard;
#pragma warning disable CA2000 // Successful open immediately enters shardLease's per-iteration using scope.
            try { shard = root.OpenDirectory(shardName); }
#pragma warning restore CA2000
            catch (IOException) { continue; }
            using var shardLease = shard;
            foreach (var key in onlyKey is null ? shard.EnumerateDirectoryNames() : [onlyKey])
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
                    await Writer(folderId).WaitAsync(cancellationToken).ConfigureAwait(false);
                    writerHeld = true;
                    lock (_leaseLock)
                    {
                        if (!_scanning.Add(key)) continue;
                        scanning = true;
#pragma warning disable CA2000 // The enclosing per-key finally unconditionally disposes scanLease, including continue/cancellation paths.
                        scanLease = AcquireKeyLease(key);
#pragma warning restore CA2000
                    }
                    Writer(folderId).Release();
                    writerHeld = false;
                    if (BeforeScanReadAsync is { } beforeRead) await beforeRead(key, cancellationToken).ConfigureAwait(false);
#pragma warning disable CA2000 // This using declaration disposes the directory on every exit from the per-key try scope.
                    using var directory = shard.OpenDirectory(key);
#pragma warning restore CA2000
                    await using var manifestStream = directory.OpenFile("manifest.json", FileMode.Open, FileAccess.Read);
                    if (manifestStream.Length > 64 * 1024) continue;
                    var manifestBytes = new byte[checked((int)manifestStream.Length)];
                    await manifestStream.ReadExactlyAsync(manifestBytes, cancellationToken).ConfigureAwait(false);
                    var manifest = JsonSerializer.Deserialize<Manifest>(manifestBytes);
                    if (manifest is not { Version: 1, Identity: { ItemId: not null, Generation: not null } } || manifest.Identity.Length <= 0
                        || !string.Equals(key, manifest.Identity.Key, StringComparison.Ordinal) || !IsVolumeCurrent(folder)) continue;
                    await using var data = directory.OpenFile("content.data", FileMode.Open, FileAccess.Read);
                    await using var journal = directory.OpenFile("ranges.journal", FileMode.Open, FileAccess.Read);
                    var physicalBytes = PhysicalEntryBytes(directory);
                    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        ObjectDisposedException.ThrowIf(_disposed, this);
                        using var retired = Command("SELECT 1 FROM RetiredEntries WHERE Key=$key AND Folder=$folder", ("$key", key), ("$folder", folderId));
                        if (retired.ExecuteScalar() is not null) continue;
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
                        Execute("UPDATE Entries SET Bytes=MAX(Bytes,$bytes),Dirty=1,Generation=$generation WHERE Key=$key",
                            ("$key", key), ("$bytes", physicalBytes), ("$generation", manifest.Identity.Generation));
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
                    if (!folder.ReadOnly)
                        await CheckpointJournalAsync(folder, key, directory, cancellationToken).ConfigureAwait(false);
                    physicalBytes = PhysicalEntryBytes(directory);
                    await Writer(folderId).WaitAsync(cancellationToken).ConfigureAwait(false);
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
                catch (InvalidDataException) { }
                finally
                {
                    lock (_leaseLock)
                    {
                        if (scanning) _scanning.Remove(key);
                        scanLease?.Dispose();
                    }
                    if (writerHeld) Writer(folderId).Release();
                }
            }
        }
        return imported;
    }

    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = LocalSqliteReason)]
    private async Task CheckpointJournalAsync(NativeCacheFolder folder, string key,
        NativeFileSystem.PinnedDirectory directory, CancellationToken cancellationToken)
    {
        const string temporary = "ranges.checkpoint.tmp";
        long scratch;
        long freeRequired;
        await Writer(folder.Id).WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var count = Command("SELECT COUNT(*) FROM Blocks WHERE Key=$key", ("$key", key));
                scratch = RoundAllocation(Math.Max(4096, checked((long)count.ExecuteScalar()! * 256)));
                if (!HasQuota(folder, scratch)) return;
                freeRequired = FreeSpaceRequirement(folder, scratch);
            }
            finally { _gate.Release(); }
            if (!CanUseFolder(folder, freeRequired)) throw new IOException("No unreserved checkpoint scratch space.");
            // Reserve scratch before any filesystem mutation, including across folders
            // sharing this device. Failures retain conservative debt until reconciliation.
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Execute("UPDATE Entries SET Bytes=Bytes+$bytes,PendingBytes=PendingBytes+$bytes WHERE Key=$key",
                    ("$bytes", scratch), ("$key", key));
            }
            finally { _gate.Release(); }
        }
        finally { Writer(folder.Id).Release(); }
        var created = false;
        try
        {
            await using (var output = directory.OpenFile(temporary, FileMode.Create, FileAccess.Write))
            {
                created = true;
                long after = -1;
                while (true)
                {
                    var records = new List<JournalBlock>(128);
                    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        using var page = Command("SELECT Offset,Count,Hash FROM Blocks WHERE Key=$key AND Offset>$after ORDER BY Offset LIMIT 128",
                            ("$key", key), ("$after", after));
                        using var reader = page.ExecuteReader();
                        while (reader.Read()) records.Add(new(reader.GetInt64(0), reader.GetInt32(1), Convert.ToHexString((byte[])reader[2])));
                    }
                    finally { _gate.Release(); }
                    if (records.Count == 0) break;
                    foreach (var record in records)
                    {
                        await output.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(record), cancellationToken).ConfigureAwait(false);
                        await output.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                        after = record.Offset;
                    }
                }
                FlushToDisk(output);
            }
            if (CheckpointPhaseAsync is { } phase) await phase("before-rename", cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsVolumeCurrent(folder)) throw new IOException("Checkpoint storage identity changed.");
            directory.ReplaceFile(temporary, "ranges.journal");
            created = false;
            directory.Flush();
            if (CheckpointPhaseAsync is { } completed) await completed("after-rename", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (created) { directory.DeleteFile(temporary); directory.Flush(); }
        }
    }

    private static long PhysicalEntryBytes(NativeFileSystem.PinnedDirectory directory)
    {
        var bytes = checked(directory.AllocatedBytes("content.data") + directory.AllocatedBytes("manifest.json")
            + directory.AllocatedBytes("ranges.journal") + EntryOverhead);
        try { return checked(bytes + directory.AllocatedBytes("ranges.checkpoint.tmp")); }
        catch (IOException exception) when (NativeFileSystem.IsMissing(exception)) { return bytes; }
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
        FlushToDisk(journal);
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
            return requested <= (AvailableBytesOverride?.Invoke(folder.Id) ?? _roots[folder.Id].GetAvailableBytes());
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

    private static long RoundAllocation(long bytes) => checked((bytes + 65535L) / 65536 * 65536 + 4096);

    public IDisposable AcquireLease(NativeCacheIdentity identity)
        => AcquireKeyLease(identity.Key);

    private ActivityLease AcquireKeyLease(string key)
    {
        lock (_leaseLock) _leases[key] = _leases.GetValueOrDefault(key) + 1;
        return new ActivityLease(this, key);
    }

    public ValueTask<IDisposable> AcquireFillAsync(NativeCacheIdentity identity, long offset, CancellationToken cancellationToken)
        => AcquireFillKeyAsync(identity.Key, offset, cancellationToken);

    private async ValueTask<IDisposable> AcquireFillKeyAsync(string identityKey, long offset, CancellationToken cancellationToken)
    {
        var key = (identityKey, offset);
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
        using var placement = await AcquireFillAsync(identity, -1, cancellationToken).ConfigureAwait(false);
        var writer = await EntryWriterAsync(identity.Key, cancellationToken).ConfigureAwait(false);
        await writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { Execute("UPDATE Entries SET Pinned=$pinned WHERE Key=$key", ("$pinned", pinned ? 1 : 0), ("$key", identity.Key)); }
        finally { _gate.Release(); }
        }
        finally { writer.Release(); }
    }

    /// <summary>Deletes only indexed application-owned entries, in bounded pages. Active and pinned entries are retained.</summary>
    public Task<int> EvictAsync(string folderId, bool clear = false, CancellationToken cancellationToken = default)
        => EvictCoreAsync(folderId, clear, pressure: false, cancellationToken);

    internal bool HasPendingClearPage(string folderId)
    { lock (_leaseLock) return _evictionCursors.ContainsKey((folderId, true, false)); }

    internal void ResetClearCursor(string folderId)
    { lock (_leaseLock) _evictionCursors.Remove((folderId, true, false)); }

    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = LocalSqliteReason)]
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
        await Writer(folderId).WaitAsync(cancellationToken).ConfigureAwait(false);
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
#pragma warning disable CA2000 // This using declaration owns the anchored shard through unlink/flush, including exceptions.
                                using var shard = _roots[folder.Id].OpenDirectory($"v1/{key[..2]}");
#pragma warning restore CA2000
                                using var directory = shard.OpenDirectory(key);
                                directory.DeleteFile("content.data");
                                directory.DeleteFile("ranges.journal");
                                directory.DeleteFile("manifest.json");
                                directory.DeleteFile("ranges.checkpoint.tmp");
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
        finally { Writer(folderId).Release(); }
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

    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = LocalSqliteReason)]
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
            var threshold = GetWatermarkBytes(folder.MaxBytes, lowWater ? folder.LowWaterPercent : folder.HighWaterPercent);
            pressure = (lowWater ? bytes > threshold : bytes >= threshold) || !HasQuota(folder, nextBlock);
            freeRequired = FreeSpaceRequirement(folder, nextBlock);
        }
        finally { _gate.Release(); }
        return pressure || !CanUseFolder(folder, freeRequired);
    }

    internal static decimal GetWatermarkBytes(long maxBytes, int percent) => (decimal)maxBytes * percent / 100;

    private bool RegisterVolume(NativeCacheFolder folder)
    {
        NativeFileSystem.PinnedDirectory? root = null;
        try
        {
            root = NativeFileSystem.PinDirectory(folder.Path);
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
            root = null; // Ownership transferred to the store's root registry.
            return true;
        }
        finally { root?.Dispose(); }
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
        var writers = _folderWriters.Values.Append(_writer).Distinct().ToArray();
        foreach (var writer in writers) await writer.WaitAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            Task<bool>[] probes;
            lock (_leaseLock) { _disposed = true; probes = _statusProbes.Values.ToArray(); }
            // A timed-out request does not stop a syscall; retain root handles until it finishes.
            try { await Task.WhenAll(probes).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            foreach (var owner in _owners.Values) await owner.DisposeAsync().ConfigureAwait(false);
            foreach (var root in _roots.Values) root.Dispose();
            await _database.DisposeAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); foreach (var writer in writers) writer.Release(); }
        // Do not dispose the managed-only gate: already queued operations must wake
        // and observe _disposed, and repeated disposal is supported. No WaitHandle is used.
    }

    private sealed record Manifest(int Version, NativeCacheIdentity Identity);
    private sealed record JournalBlock(long Offset, int Count, string Hash);
}

public sealed record NativeCacheProbeResult(string FileSystem, string Capability, bool Readable, bool Writable,
    bool DurableWriteVerified, long AvailableBytes, string? Error);

public sealed record NativeCacheVerifiedRange(long Offset, int Count);
