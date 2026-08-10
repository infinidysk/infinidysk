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
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

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
        string? playerSession = null)
    {
        var key = BuildKey(path, clientKey, playerSession);
        var now = DateTimeOffset.UtcNow;

        while (true)
        {
            if (_keyToId.TryGetValue(key, out var existingId)
                && _entries.TryGetValue(existingId, out var existing))
            {
                existing.LastActivityAt = now;
                if (fileSize is { } size) existing.FileSize = size;
                if (!string.IsNullOrEmpty(clientUserAgent)) existing.ClientUserAgent = clientUserAgent;
                if (!string.IsNullOrEmpty(clientIp)) existing.ClientIp = clientIp;
                return existingId;
            }

            var newId = Guid.NewGuid();
            var newEntry = new Entry
            {
                Id = newId,
                Path = path,
                FileName = fileName,
                FileSize = fileSize,
                ClientKey = clientKey,
                ClientUserAgent = clientUserAgent,
                ClientIp = clientIp,
                PlayerSession = playerSession,
                StartedAt = now,
                LastActivityAt = now,
            };

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
        if (_entries.TryGetValue(id, out var entry))
        {
            entry.LastActivityAt = DateTimeOffset.UtcNow;
            if (bytesRead > 0)
            {
                Interlocked.Add(ref entry.BytesRead, bytesRead);
                Interlocked.Add(ref _totalBytesServed, bytesRead);
            }
            if (currentOffset.HasValue)
                Interlocked.Exchange(ref entry.CurrentOffset, currentOffset.Value);
        }
    }

    public void AddBytesFetched(Guid id, long bytes)
    {
        if (bytes <= 0) return;
        if (_entries.TryGetValue(id, out var entry))
            Interlocked.Add(ref entry.BytesFetched, bytes);
    }

    public void SetEndReason(Guid id, ReadSession.EndReasonCode reason)
    {
        if (_entries.TryGetValue(id, out var entry))
            entry.EndReason = reason;
    }

    public long GetBytesRead(Guid id)
        => _entries.TryGetValue(id, out var entry) ? Interlocked.Read(ref entry.BytesRead) : 0;

    /// <summary>
    /// Update the user-facing metadata on an existing session. Used once the
    /// real filename/size are resolved from the dav store (the path passed to
    /// GetOrCreate is usually an opaque GUID for .ids/-style paths).
    /// </summary>
    public void UpdateInfo(Guid id, string? fileName, long? fileSize)
    {
        if (!_entries.TryGetValue(id, out var entry)) return;
        if (!string.IsNullOrWhiteSpace(fileName)) entry.FileName = fileName;
        if (fileSize is { } size) entry.FileSize = size;
    }

    public IReadOnlyList<Entry> Snapshot()
    {
        var cutoff = DateTimeOffset.UtcNow - ActivityWindow;
        return _entries.Values
            .Where(e => e.LastActivityAt >= cutoff)
            .OrderBy(e => e.StartedAt)
            .ToList();
    }

    /// <summary>
    /// Remove entries that haven't been touched within the activity window.
    /// Returns the pruned entries so callers can clear external bookkeeping
    /// and persist a terminal record of the session.
    /// </summary>
    public IReadOnlyList<Entry> PruneExpired() => PruneExpired(DateTimeOffset.UtcNow);

    // Test-visible overload: lets tests expire entries without waiting out the
    // activity window in real time.
    internal IReadOnlyList<Entry> PruneExpired(DateTimeOffset now)
    {
        var cutoff = now - ActivityWindow;
        var expired = _entries
            .Where(kv => kv.Value.LastActivityAt < cutoff)
            .Select(kv => kv.Value)
            .ToList();
        foreach (var entry in expired)
        {
            // Clear the dedup mapping first, and only if it still points to
            // this expired entry — a fresh session for the same player and
            // file may have already claimed the key, in which case we leave
            // the new mapping intact.
            var key = BuildKey(entry.Path, entry.ClientKey, entry.PlayerSession);
            ((ICollection<KeyValuePair<string, Guid>>)_keyToId)
                .Remove(new KeyValuePair<string, Guid>(key, entry.Id));
            _entries.TryRemove(entry.Id, out _);
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
        public string FileName { get; set; } = "";
        public long? FileSize { get; set; }
        public string ClientKey { get; init; } = "";
        public string? ClientUserAgent { get; set; }
        public string? ClientIp { get; set; }
        public string? PlayerSession { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset LastActivityAt { get; set; }
        public long BytesRead;
        public long BytesFetched;
        public ReadSession.EndReasonCode EndReason { get; set; } = ReadSession.EndReasonCode.Completed;
        /// <summary>
        /// Most recent absolute file offset the player has been served (i.e. the
        /// "where the read head is" position). Updated after every chunk so the
        /// Right Now panel can show genuine playback position, not cumulative
        /// transferred bytes (which over-counts on seek/rewind).
        /// </summary>
        public long CurrentOffset;
    }
}
