using System.Collections.Concurrent;
using NzbWebDAV.Database.Models.Metrics;

namespace NzbWebDAV.Services;

/// <summary>
/// In-memory list of currently active WebDAV read sessions, used to surface
/// "what's being read right now and from which backbone" in the UI. No persistence:
/// entries live only while a client is actively pulling bytes.
/// </summary>
public class ActiveReadRegistry
{
    private static readonly TimeSpan ActivityWindow = TimeSpan.FromSeconds(15);

    // Two indexes. _keyToId dedupes successive range requests from the same
    // (path, clientKey) onto a single session id while the session is active.
    // Each new session gets a fresh Guid so its terminal ReadSession row never
    // collides with a previously-pruned session for the same player and file —
    // a hash-derived id would re-insert a duplicate primary key the second
    // time the same player opens the same file and trip the SQLite UNIQUE
    // constraint on the metrics flush.
    private readonly ConcurrentDictionary<string, Guid> _keyToId = new();
    private readonly ConcurrentDictionary<Guid, EntryState> _entries = new();

    // Process-lifetime monotonic counter of every byte served downstream. The
    // broadcaster samples this on a fixed tick to compute a rolling rate, so
    // active (not-yet-pruned) reads still show up in throughput.
    private long _totalBytesServed;
    public long TotalBytesServed => Interlocked.Read(ref _totalBytesServed);

    public Guid GetOrCreate(
        string path,
        string clientKey,
        string fileName,
        long? fileSize,
        string? clientUserAgent = null,
        string? clientIp = null,
        string? playerSession = null,
        Guid? davItemId = null)
    {
        var key = BuildKey(path, clientKey, playerSession);
        var now = DateTimeOffset.UtcNow;

        while (true)
        {
            if (_keyToId.TryGetValue(key, out var existingId))
            {
                // The key is published immediately before the entry. If another
                // caller observes that tiny publication window, retry rather than
                // treating the mapping as stale and creating a duplicate session.
                if (!_entries.TryGetValue(existingId, out var existing))
                {
                    Thread.Yield();
                    continue;
                }

                if (existing.TryRefresh(now, fileSize, clientUserAgent, clientIp, davItemId))
                    return existingId;

                // Pruning can mark an entry removed before clearing the dedupe key.
                // Remove only the exact stale mapping and retry so a newer session
                // for the same key is never disturbed.
                ((ICollection<KeyValuePair<string, Guid>>)_keyToId)
                    .Remove(new KeyValuePair<string, Guid>(key, existingId));
                continue;
            }

            var newId = Guid.NewGuid();
            var newEntry = new EntryState(
                newId,
                path,
                fileName,
                fileSize,
                clientKey,
                clientUserAgent,
                clientIp,
                playerSession,
                davItemId,
                now);

            if (_keyToId.TryAdd(key, newId))
            {
                _entries[newId] = newEntry;
                return newId;
            }
            // Lost the race against another GetOrCreate for the same key;
            // loop and reuse whichever id the winner published.
        }
    }

    public void Touch(Guid id, long bytesRead, long? currentOffset = null)
    {
        if (_entries.TryGetValue(id, out var entry)
            && entry.TryTouch(DateTimeOffset.UtcNow, bytesRead, currentOffset)
            && bytesRead > 0)
        {
            Interlocked.Add(ref _totalBytesServed, bytesRead);
        }
    }

    public void AddBytesFetched(Guid id, long bytes)
    {
        if (bytes <= 0) return;
        if (_entries.TryGetValue(id, out var entry))
            entry.TryAddBytesFetched(bytes);
    }

    public void SetEndReason(Guid id, ReadSession.EndReasonCode reason)
    {
        if (_entries.TryGetValue(id, out var entry))
            entry.TrySetEndReason(reason);
    }

    public long GetBytesRead(Guid id)
        => _entries.TryGetValue(id, out var entry) ? entry.GetBytesRead() : 0;

    /// <summary>
    /// Update the user-facing metadata on an existing session. Used once the
    /// real filename/size are resolved from the dav store (the path passed to
    /// GetOrCreate is usually an opaque GUID for .ids/-style paths).
    /// </summary>
    public void UpdateInfo(Guid id, string? fileName, long? fileSize, Guid? davItemId = null)
    {
        if (_entries.TryGetValue(id, out var entry))
            entry.TryUpdateInfo(fileName, fileSize, davItemId);
    }

    public bool TryResolveDavItemIdForPlayerSession(string playerSession, out Guid davItemId)
    {
        davItemId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(playerSession))
            return false;

        var cutoff = DateTimeOffset.UtcNow - ActivityWindow;
        var matches = new HashSet<Guid>();
        foreach (var entry in _entries.Values)
        {
            if (!entry.TryResolveDavItemId(playerSession, cutoff, out var resolvedId))
                continue;

            matches.Add(resolvedId);
            if (matches.Count > 1)
                return false;
        }

        if (matches.Count != 1)
            return false;

        davItemId = matches.Single();
        return true;
    }

    /// <summary>
    /// Returns detached copies of active entries. Mutable registry state is never
    /// exposed to callers, so a snapshot cannot observe metadata changing underneath it.
    /// </summary>
    public IReadOnlyList<Entry> Snapshot()
    {
        var cutoff = DateTimeOffset.UtcNow - ActivityWindow;
        var snapshot = new List<Entry>();
        foreach (var state in _entries.Values)
        {
            if (state.TrySnapshot(cutoff, out var entry))
                snapshot.Add(entry);
        }

        return snapshot
            .OrderBy(entry => entry.StartedAt)
            .ToList();
    }

    /// <summary>
    /// Remove entries that haven't been touched within the activity window.
    /// Returns detached final snapshots so callers can clear external bookkeeping
    /// and persist a terminal record of the session.
    /// </summary>
    public IReadOnlyList<Entry> PruneExpired() => PruneExpired(DateTimeOffset.UtcNow);

    // Test-visible overload: lets tests expire entries without waiting out the
    // activity window in real time.
    internal IReadOnlyList<Entry> PruneExpired(DateTimeOffset now)
    {
        var cutoff = now - ActivityWindow;
        var expired = new List<Entry>();

        foreach (var pair in _entries)
        {
            if (!pair.Value.TryExpire(cutoff, out var entry))
                continue;

            var key = BuildKey(entry.Path, entry.ClientKey, entry.PlayerSession);
            ((ICollection<KeyValuePair<string, Guid>>)_keyToId)
                .Remove(new KeyValuePair<string, Guid>(key, entry.Id));

            if (_entries.TryRemove(pair.Key, out _))
                expired.Add(entry);
        }

        return expired;
    }

    public int Count => _entries.Count;

    // The player-session token joins the dedupe key so two in-app players
    // streaming the same file from the same browser/IP still get distinct
    // sessions; requests without one (external players) dedupe as before.
    private static string BuildKey(string path, string clientKey, string? playerSession)
        => playerSession is null
            ? path + "\n" + clientKey
            : path + "\n" + clientKey + "\nps:" + playerSession;

    public sealed class Entry
    {
        public Guid Id { get; init; }
        public string Path { get; init; } = "";
        public string FileName { get; init; } = "";
        public long? FileSize { get; init; }
        public string ClientKey { get; init; } = "";
        public string? ClientUserAgent { get; init; }
        public string? ClientIp { get; init; }
        public string? PlayerSession { get; init; }
        public Guid? DavItemId { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset LastActivityAt { get; init; }
        public long BytesRead;
        public long BytesFetched;
        public ReadSession.EndReasonCode EndReason { get; init; } = ReadSession.EndReasonCode.Completed;
        /// <summary>
        /// Most recent absolute file offset served by InfiniDysk. This is a
        /// transport/source read head, not an authoritative viewer position: rclone
        /// read-ahead may move it well ahead of an actual media-server player.
        /// </summary>
        public long CurrentOffset;
    }

    private sealed class EntryState
    {
        private readonly object _gate = new();
        private bool _removed;
        private string _fileName;
        private long? _fileSize;
        private string? _clientUserAgent;
        private string? _clientIp;
        private Guid? _davItemId;
        private DateTimeOffset _lastActivityAt;
        private long _bytesRead;
        private long _bytesFetched;
        private long _currentOffset;
        private ReadSession.EndReasonCode _endReason = ReadSession.EndReasonCode.Completed;

        public EntryState(
            Guid id,
            string path,
            string fileName,
            long? fileSize,
            string clientKey,
            string? clientUserAgent,
            string? clientIp,
            string? playerSession,
            Guid? davItemId,
            DateTimeOffset now)
        {
            Id = id;
            Path = path;
            ClientKey = clientKey;
            PlayerSession = playerSession;
            StartedAt = now;
            _fileName = fileName;
            _fileSize = fileSize;
            _clientUserAgent = clientUserAgent;
            _clientIp = clientIp;
            _davItemId = davItemId;
            _lastActivityAt = now;
        }

        private Guid Id { get; }
        private string Path { get; }
        private string ClientKey { get; }
        private string? PlayerSession { get; }
        private DateTimeOffset StartedAt { get; }

        public bool TryRefresh(
            DateTimeOffset now,
            long? fileSize,
            string? clientUserAgent,
            string? clientIp,
            Guid? davItemId)
        {
            lock (_gate)
            {
                if (_removed) return false;
                _lastActivityAt = now;
                if (fileSize is { } size) _fileSize = size;
                if (!string.IsNullOrEmpty(clientUserAgent)) _clientUserAgent = clientUserAgent;
                if (!string.IsNullOrEmpty(clientIp)) _clientIp = clientIp;
                if (davItemId is { } resolvedId) _davItemId = resolvedId;
                return true;
            }
        }

        public bool TryTouch(DateTimeOffset now, long bytesRead, long? currentOffset)
        {
            lock (_gate)
            {
                if (_removed) return false;
                _lastActivityAt = now;
                if (bytesRead > 0) _bytesRead += bytesRead;
                if (currentOffset.HasValue) _currentOffset = currentOffset.Value;
                return true;
            }
        }

        public bool TryAddBytesFetched(long bytes)
        {
            lock (_gate)
            {
                if (_removed) return false;
                _bytesFetched += bytes;
                return true;
            }
        }

        public bool TrySetEndReason(ReadSession.EndReasonCode reason)
        {
            lock (_gate)
            {
                if (_removed) return false;
                _endReason = reason;
                return true;
            }
        }

        public long GetBytesRead()
        {
            lock (_gate)
                return _bytesRead;
        }

        public bool TryUpdateInfo(string? fileName, long? fileSize, Guid? davItemId)
        {
            lock (_gate)
            {
                if (_removed) return false;
                if (!string.IsNullOrWhiteSpace(fileName)) _fileName = fileName;
                if (fileSize is { } size) _fileSize = size;
                if (davItemId is { } resolvedId) _davItemId = resolvedId;
                return true;
            }
        }

        public bool TryResolveDavItemId(string playerSession, DateTimeOffset cutoff, out Guid davItemId)
        {
            lock (_gate)
            {
                davItemId = Guid.Empty;
                if (_removed
                    || _lastActivityAt < cutoff
                    || !_davItemId.HasValue
                    || !string.Equals(PlayerSession, playerSession, StringComparison.Ordinal))
                    return false;

                davItemId = _davItemId.Value;
                return true;
            }
        }

        public bool TrySnapshot(DateTimeOffset cutoff, out Entry entry)
        {
            lock (_gate)
            {
                if (_removed || _lastActivityAt < cutoff)
                {
                    entry = null!;
                    return false;
                }

                entry = CopyLocked();
                return true;
            }
        }

        public bool TryExpire(DateTimeOffset cutoff, out Entry entry)
        {
            lock (_gate)
            {
                if (_removed || _lastActivityAt >= cutoff)
                {
                    entry = null!;
                    return false;
                }

                _removed = true;
                entry = CopyLocked();
                return true;
            }
        }

        private Entry CopyLocked() => new()
        {
            Id = Id,
            Path = Path,
            FileName = _fileName,
            FileSize = _fileSize,
            ClientKey = ClientKey,
            ClientUserAgent = _clientUserAgent,
            ClientIp = _clientIp,
            PlayerSession = PlayerSession,
            DavItemId = _davItemId,
            StartedAt = StartedAt,
            LastActivityAt = _lastActivityAt,
            BytesRead = _bytesRead,
            BytesFetched = _bytesFetched,
            EndReason = _endReason,
            CurrentOffset = _currentOffset,
        };
    }
}