using System.Collections.Concurrent;
using NzbWebDAV.Config;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Services;

public class PreflightCache
{
    private readonly ConfigManager _configManager;
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly object _cacheLock = new();
    private readonly long _maxCacheBytes;
    private readonly int _maxCacheEntries;
    private long _currentBytes;
    private DateTimeOffset _lastSweep = DateTimeOffset.UtcNow;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    public PreflightCache(
        ConfigManager configManager,
        long? maxCacheBytes = null,
        int? maxCacheEntries = null)
    {
        _configManager = configManager;
        _maxCacheBytes = maxCacheBytes ?? NzbFetchLimits.PreflightMaxCacheBytes;
        _maxCacheEntries = maxCacheEntries ?? NzbFetchLimits.PreflightMaxCacheEntries;
        if (_maxCacheBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxCacheBytes));
        if (_maxCacheEntries < 1) throw new ArgumentOutOfRangeException(nameof(maxCacheEntries));
    }

    internal long CurrentCachedBytes
    {
        get { lock (_cacheLock) return _currentBytes; }
    }

    internal int CachedEntryCount => _entries.Count;

    public Entry? Get(string nzbUrl)
    {
        if (!_entries.TryGetValue(nzbUrl, out var entry)) return null;
        if (DateTimeOffset.UtcNow > entry.ExpiresAt)
        {
            RemoveAccounting(nzbUrl);
            return null;
        }
        return entry;
    }

    public void SetVerified(string nzbUrl, byte[]? nzbBytes, PlaybackFastVerifier.Verdict verdict, string? responderHost)
    {
        var ttl = TimeSpan.FromSeconds(_configManager.GetPreflightTtlSeconds());
        var entry = new Entry
        {
            NzbBytes = nzbBytes,
            Verdict = verdict,
            ResponderHost = responderHost,
            PreparedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow + ttl,
        };
        Admit(nzbUrl, entry);
    }

    public void Invalidate(string nzbUrl) => RemoveAccounting(nzbUrl);

    private void Admit(string nzbUrl, Entry entry)
    {
        var bytes = ByteLength(entry.NzbBytes);
        lock (_cacheLock)
        {
            SweepExpiredUnlocked(DateTimeOffset.UtcNow);

            if (_entries.TryRemove(nzbUrl, out var existing))
                _currentBytes -= ByteLength(existing.NzbBytes);

            while ((_entries.Count >= _maxCacheEntries || _currentBytes + bytes > _maxCacheBytes)
                   && !_entries.IsEmpty)
            {
                var oldest = default(KeyValuePair<string, Entry>);
                var found = false;
                foreach (var kv in _entries)
                {
                    if (!found || kv.Value.ExpiresAt < oldest.Value.ExpiresAt)
                    {
                        oldest = kv;
                        found = true;
                    }
                }

                if (!found) break;
                if (_entries.TryRemove(oldest.Key, out var removed))
                    _currentBytes -= ByteLength(removed.NzbBytes);
            }

            if (_entries.Count >= _maxCacheEntries || _currentBytes + bytes > _maxCacheBytes)
                return;

            _entries[nzbUrl] = entry;
            _currentBytes += bytes;
            MaybeMarkSweep(DateTimeOffset.UtcNow);
        }
    }

    private void RemoveAccounting(string nzbUrl)
    {
        lock (_cacheLock)
        {
            if (_entries.TryRemove(nzbUrl, out var removed))
                _currentBytes -= ByteLength(removed.NzbBytes);
        }
    }

    private void SweepExpiredUnlocked(DateTimeOffset now)
    {
        foreach (var kv in _entries)
        {
            if (now > kv.Value.ExpiresAt && _entries.TryRemove(kv.Key, out var removed))
                _currentBytes -= ByteLength(removed.NzbBytes);
        }
    }

    private void MaybeMarkSweep(DateTimeOffset now)
    {
        if (now - _lastSweep < SweepInterval) return;
        SweepExpiredUnlocked(now);
        _lastSweep = now;
    }

    private static long ByteLength(byte[]? bytes) => bytes?.LongLength ?? 0;

    public class Entry
    {
        public required byte[]? NzbBytes { get; init; }
        public required PlaybackFastVerifier.Verdict Verdict { get; init; }
        public required string? ResponderHost { get; init; }
        public required DateTimeOffset PreparedAt { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
    }
}
