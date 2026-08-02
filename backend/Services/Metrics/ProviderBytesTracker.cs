using System.Collections.Concurrent;
using System.Diagnostics;

namespace NzbWebDAV.Services.Metrics;

/// <summary>
/// Tracks bytes pulled from each provider in near-real time. The byte count is
/// unknown when a SegmentFetch row is queued because bytes flow lazily through
/// the YencStream after the fetch returns; this service captures them as the
/// stream is read and folds them into ProviderMinute on the next rollup tick.
///
/// Two pieces of state:
///   - _buckets keyed by (minute, providerKey) -> bytes, drained by the rollup service
///   - _lifetime keyed by providerKey -> total bytes, exposed for "all-time" tiles
///
/// providerKey is the stable per-account identity (<c>ProviderId</c>), not the NNTP host.
/// </summary>
public sealed class ProviderBytesTracker
{
    private const long OneMinute = 60_000;

    private readonly ConcurrentDictionary<(long Minute, string ProviderKey), long> _buckets = new();
    private readonly ConcurrentDictionary<string, long> _lifetime = new();
    private long _lifetimeAll;

    private readonly ConcurrentDictionary<string, double> _bytesPerMs = new();
    private readonly ConcurrentDictionary<string, long> _speedRecordedAt = new();
    private readonly Func<long> _timestampProvider;
    private const double SpeedEwmaAlpha = 0.3;

    public ProviderBytesTracker() : this(Stopwatch.GetTimestamp)
    {
    }

    internal ProviderBytesTracker(Func<long> timestampProvider)
    {
        _timestampProvider = timestampProvider;
    }

    public void Add(string providerKey, long bytes)
    {
        if (bytes <= 0 || string.IsNullOrEmpty(providerKey)) return;
        var minute = NowMinute();
        _buckets.AddOrUpdate((minute, providerKey), bytes, (_, prev) => prev + bytes);
        _lifetime.AddOrUpdate(providerKey, bytes, (_, prev) => prev + bytes);
        Interlocked.Add(ref _lifetimeAll, bytes);
    }

    public long LifetimeAll => Interlocked.Read(ref _lifetimeAll);

    public IReadOnlyDictionary<string, long> LifetimeByProvider => _lifetime;

    /// <summary>
    /// Overwrites the in-memory lifetime counter for a provider key. Used at startup to
    /// hydrate from ProviderHourly and after a counter reset to drop back to
    /// zero. Does not touch <see cref="LifetimeAll"/> since that reflects the
    /// total bytes observed by this process; rewriting it on every config
    /// change would make the overview tile jump around for unrelated reasons.
    /// </summary>
    public void SetLifetime(string providerKey, long bytes)
    {
        if (string.IsNullOrEmpty(providerKey)) return;
        _lifetime[providerKey] = Math.Max(0, bytes);
    }

    public long GetLifetime(string providerKey)
    {
        if (string.IsNullOrEmpty(providerKey)) return 0;
        return _lifetime.TryGetValue(providerKey, out var v) ? v : 0;
    }

    public void RecordSegmentThroughput(string providerKey, long bytes, double activeMs)
    {
        if (string.IsNullOrEmpty(providerKey) || bytes <= 0 || activeMs <= 0) return;
        var sample = bytes / activeMs;
        _bytesPerMs.AddOrUpdate(providerKey, sample, (_, prev) => prev + SpeedEwmaAlpha * (sample - prev));
        _speedRecordedAt[providerKey] = _timestampProvider();
    }

    public double GetBytesPerMs(string providerKey)
    {
        if (string.IsNullOrEmpty(providerKey)) return 0;
        return _bytesPerMs.TryGetValue(providerKey, out var v) ? v : 0;
    }

    public double GetRecentBytesPerMs(string providerKey, TimeSpan maxAge)
    {
        if (maxAge <= TimeSpan.Zero ||
            !_speedRecordedAt.TryGetValue(providerKey, out var recordedAt))
            return 0;

        var elapsed = Stopwatch.GetElapsedTime(recordedAt, _timestampProvider());
        return elapsed <= maxAge ? GetBytesPerMs(providerKey) : 0;
    }

    /// <summary>
    /// Pop all buckets whose minute is strictly older than <paramref name="cutoffMinute"/>.
    /// Returned in stable order so callers can apply them transactionally.
    /// </summary>
    public List<(long Minute, string ProviderKey, long Bytes)> DrainClosed(long cutoffMinute)
    {
        var drained = new List<(long, string, long)>();
        foreach (var key in _buckets.Keys)
        {
            if (key.Minute >= cutoffMinute) continue;
            if (_buckets.TryRemove(key, out var bytes))
                drained.Add((key.Minute, key.ProviderKey, bytes));
        }
        return drained;
    }

    /// <summary>
    /// Clears pending minute buckets and all lifetime counters. Used by the
    /// overview-stats reset after usage has been folded into BytesUsedOffset.
    /// Speed EWMAs are kept: they drive failover heuristics, not statistics.
    /// </summary>
    public void ResetCounters()
    {
        _buckets.Clear();
        _lifetime.Clear();
        Interlocked.Exchange(ref _lifetimeAll, 0);
    }

    /// <summary>
    /// Clears one provider's pending buckets and lifetime counter, and deducts
    /// its share from the all-time total. Unlike SetLifetime (config-change
    /// rehydration), this is a deliberate stats reset, so the overview tile
    /// dropping is the intended outcome.
    /// </summary>
    public void ResetProvider(string providerKey)
    {
        if (string.IsNullOrEmpty(providerKey)) return;
        foreach (var key in _buckets.Keys)
            if (key.ProviderKey == providerKey)
                _buckets.TryRemove(key, out _);
        if (_lifetime.TryRemove(providerKey, out var removed))
            Interlocked.Add(ref _lifetimeAll, -removed);
    }

    private static long NowMinute()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return nowMs - (nowMs % OneMinute);
    }
}
