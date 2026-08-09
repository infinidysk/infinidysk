using System.Collections.Concurrent;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Services;

public class NzbFetchCoalescer
{
    private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan HardFetchCap = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<FetchKey, Lazy<Task<byte[]?>>> _inFlight = new();
    private readonly ConcurrentDictionary<FetchKey, Entry> _cache = new();
    private readonly object _cacheLock = new();
    private readonly long _maxCacheBytes;
    private readonly int _maxCacheEntries;
    private readonly TimeSpan _cacheTtl;
    private long _currentBytes;
    private DateTimeOffset _lastSweep = DateTimeOffset.UtcNow;

    public NzbFetchCoalescer(
        long? maxCacheBytes = null,
        int? maxCacheEntries = null,
        TimeSpan? cacheTtl = null)
    {
        _maxCacheBytes = maxCacheBytes ?? NzbFetchLimits.CoalescerMaxCacheBytes;
        _maxCacheEntries = maxCacheEntries ?? NzbFetchLimits.CoalescerMaxCacheEntries;
        _cacheTtl = cacheTtl ?? DefaultCacheTtl;
        if (_maxCacheBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxCacheBytes));
        if (_maxCacheEntries < 1) throw new ArgumentOutOfRangeException(nameof(maxCacheEntries));
        if (_cacheTtl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(cacheTtl));
    }

    internal long CurrentCachedBytes
    {
        get { lock (_cacheLock) return _currentBytes; }
    }

    internal int CachedEntryCount => _cache.Count;

    public async Task<byte[]?> GetOrFetchAsync(
        string url,
        string? proxyUrl,
        bool skipTlsVerification,
        Func<CancellationToken, Task<byte[]?>> fetch,
        CancellationToken ct)
    {
        var key = new FetchKey(url, proxyUrl ?? "", skipTlsVerification);
        if (TryGetCached(key, out var cached)) return cached;

        var lazy = _inFlight.GetOrAdd(key, _ => new Lazy<Task<byte[]?>>(() => RunSharedAsync(key, fetch)));
        return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<byte[]?> RunSharedAsync(FetchKey key, Func<CancellationToken, Task<byte[]?>> fetch)
    {
        try
        {
            using var cts = new CancellationTokenSource(HardFetchCap);
            var bytes = await fetch(cts.Token).ConfigureAwait(false);
            if (bytes is not null)
                TryAdmit(key, bytes);
            return bytes;
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    private bool TryGetCached(FetchKey key, out byte[]? bytes)
    {
        bytes = null;
        if (!_cache.TryGetValue(key, out var e)) return false;
        if (DateTimeOffset.UtcNow > e.ExpiresAt)
        {
            RemoveAccounting(key);
            return false;
        }
        bytes = e.Bytes;
        return true;
    }

    private void TryAdmit(FetchKey key, byte[] bytes)
    {
        lock (_cacheLock)
        {
            SweepExpiredUnlocked(DateTimeOffset.UtcNow);

            if (_cache.TryRemove(key, out var existing))
                _currentBytes -= existing.Bytes.LongLength;

            while ((_cache.Count >= _maxCacheEntries || _currentBytes + bytes.LongLength > _maxCacheBytes)
                   && !_cache.IsEmpty)
            {
                var oldest = default(KeyValuePair<FetchKey, Entry>);
                var found = false;
                foreach (var kv in _cache)
                {
                    if (!found || kv.Value.ExpiresAt < oldest.Value.ExpiresAt)
                    {
                        oldest = kv;
                        found = true;
                    }
                }

                if (!found) break;
                if (_cache.TryRemove(oldest.Key, out var removed))
                    _currentBytes -= removed.Bytes.LongLength;
            }

            if (_cache.Count >= _maxCacheEntries || _currentBytes + bytes.LongLength > _maxCacheBytes)
                return;

            _cache[key] = new Entry(bytes, DateTimeOffset.UtcNow + _cacheTtl);
            _currentBytes += bytes.LongLength;
            MaybeMarkSweep(DateTimeOffset.UtcNow);
        }
    }

    private void RemoveAccounting(FetchKey key)
    {
        lock (_cacheLock)
        {
            if (_cache.TryRemove(key, out var removed))
                _currentBytes -= removed.Bytes.LongLength;
        }
    }

    private void SweepExpiredUnlocked(DateTimeOffset now)
    {
        foreach (var kv in _cache)
        {
            if (now > kv.Value.ExpiresAt && _cache.TryRemove(kv.Key, out var removed))
                _currentBytes -= removed.Bytes.LongLength;
        }
    }

    private void MaybeMarkSweep(DateTimeOffset now)
    {
        if (now - _lastSweep < SweepInterval) return;
        SweepExpiredUnlocked(now);
        _lastSweep = now;
    }

    private readonly record struct Entry(byte[] Bytes, DateTimeOffset ExpiresAt);
    private readonly record struct FetchKey(string Url, string ProxyUrl, bool SkipTlsVerification);
}
