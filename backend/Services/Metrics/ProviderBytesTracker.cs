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
///   - _quota keyed by providerKey -> exact raw BODY bytes for the current reset epoch
///
/// providerKey is the stable per-account identity (<c>ProviderId</c>), not the NNTP host.
/// </summary>
public sealed class ProviderBytesTracker
{
    private const long OneMinute = 60_000;
    private static readonly TimeSpan MinSampleWindow = TimeSpan.FromMilliseconds(900);

    private readonly ConcurrentDictionary<(long Minute, string ProviderKey), long> _buckets = new();
    private readonly ConcurrentDictionary<string, long> _lifetime = new();
    private long _lifetimeAll;
    private readonly ConcurrentDictionary<string, QuotaCounter> _quota = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, double> _bytesPerMs = new();
    private readonly ConcurrentDictionary<string, long> _speedRecordedAt = new();
    private readonly Func<long> _timestampProvider;
    private const double SpeedEwmaAlpha = 0.3;

    // minute -> highest sampled aggregate fetch rate (bytes/sec); drained by the rollup service.
    private readonly ConcurrentDictionary<long, long> _peakByMinute = new();
    private readonly Lock _sampleGate = new();
    internal SemaphoreSlim PeakPersistenceGate { get; } = new(1, 1);
    private long _lastSampleBytes;
    private long _lastSampleTimestamp;
    private bool _hasSample;

    private sealed class QuotaCounter(long resetAt, long bytesUsed)
    {
        private readonly Lock _gate = new();
        private long _resetAt = resetAt;
        private long _bytesUsed = Math.Max(0, bytesUsed);

        public void Add(long bytes)
        {
            lock (_gate) _bytesUsed = checked(_bytesUsed + bytes);
        }

        public void Reset(long resetAt)
        {
            lock (_gate)
            {
                if (resetAt < _resetAt) return;
                _resetAt = resetAt;
                _bytesUsed = 0;
            }
        }

        public ProviderQuotaSnapshot Snapshot(string provider)
        {
            lock (_gate)
                return new ProviderQuotaSnapshot(provider, _bytesUsed, _resetAt);
        }
    }

    public readonly record struct ProviderQuotaSnapshot(string Provider, long BytesUsed, long ResetAt);

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
        _quota.GetOrAdd(providerKey, static _ => new QuotaCounter(0, 0)).Add(bytes);
    }

    public long LifetimeAll => Interlocked.Read(ref _lifetimeAll);

    public IReadOnlyDictionary<string, long> LifetimeByProvider => _lifetime;

    /// <summary>
    /// Records one aggregate fetch-rate sample by differencing <see cref="LifetimeAll"/>
    /// against the previous call. Called at ~1 Hz off the hot path; keeps only the
    /// per-minute maximum so nothing is stored per sample.
    /// </summary>
    public void SampleFetchRate(long nowMs)
    {
        var timestamp = _timestampProvider();
        lock (_sampleGate)
        {
            var bytes = LifetimeAll;
            if (_hasSample)
            {
                var delta = bytes - _lastSampleBytes;
                var elapsed = Stopwatch.GetElapsedTime(_lastSampleTimestamp, timestamp);
                // Negative delta means the counters were reset between samples; rebaseline only.
                if (delta > 0 && elapsed < MinSampleWindow)
                    return;
                if (delta > 0 && elapsed > TimeSpan.Zero)
                {
                    var rate = (long)Math.Round(delta / elapsed.TotalSeconds);
                    var minute = nowMs - (nowMs % OneMinute);
                    _peakByMinute.AddOrUpdate(minute, rate, (_, prev) => Math.Max(prev, rate));
                }
            }

            _lastSampleBytes = bytes;
            _lastSampleTimestamp = timestamp;
            _hasSample = true;
        }
    }

    /// <summary>Highest sampled rate in any not-yet-drained minute at or after <paramref name="minuteInclusive"/>.</summary>
    public long PendingPeakSince(long minuteInclusive)
    {
        long peak = 0;
        foreach (var pair in _peakByMinute.Where(pair => pair.Key >= minuteInclusive && pair.Value > peak))
            peak = pair.Value;
        return peak;
    }

    /// <summary>Pop per-minute peak rates strictly older than <paramref name="cutoffMinute"/>.</summary>
    public List<(long Minute, long PeakBytesPerSec)> DrainClosedPeaks(long cutoffMinute)
    {
        var drained = new List<(long, long)>();
        foreach (var minute in _peakByMinute.Keys.Where(minute => minute < cutoffMinute))
        {
            if (_peakByMinute.TryRemove(minute, out var peak))
                drained.Add((minute, peak));
        }
        drained.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return drained;
    }

    internal void RestorePeaks(IEnumerable<(long Minute, long PeakBytesPerSec)> peaks)
    {
        foreach (var (minute, peak) in peaks)
            _peakByMinute.AddOrUpdate(minute, peak, (_, current) => Math.Max(current, peak));
    }

    public void InitializeQuota(string providerKey, long resetAt, long bytesUsed)
    {
        if (string.IsNullOrEmpty(providerKey)) return;
        if (!_quota.TryAdd(providerKey, new QuotaCounter(resetAt, bytesUsed)))
            throw new InvalidOperationException($"Provider quota {providerKey} was initialized more than once.");
    }

    public void ResetQuota(string providerKey, long resetAt) =>
        _quota.GetOrAdd(providerKey, _ => new QuotaCounter(resetAt, 0)).Reset(resetAt);

    public long GetQuotaBytes(string providerKey)
    {
        if (string.IsNullOrEmpty(providerKey) || !_quota.TryGetValue(providerKey, out var counter))
            return 0;
        return counter.Snapshot(providerKey).BytesUsed;
    }

    public long GetLifetime(string providerKey)
    {
        if (string.IsNullOrEmpty(providerKey)) return 0;
        return _lifetime.TryGetValue(providerKey, out var value) ? value : 0;
    }

    public IReadOnlyList<ProviderQuotaSnapshot> SnapshotQuota() =>
        _quota.Select(pair => pair.Value.Snapshot(pair.Key)).ToArray();

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
    /// overview-stats reset. Quota counters are independent and remain intact.
    /// Speed EWMAs are kept: they drive failover heuristics, not statistics.
    /// </summary>
    public void ResetCounters()
    {
        lock (_sampleGate)
        {
            _buckets.Clear();
            _lifetime.Clear();
            Interlocked.Exchange(ref _lifetimeAll, 0);
            _peakByMinute.Clear();
            _lastSampleBytes = LifetimeAll;
            _lastSampleTimestamp = _timestampProvider();
            _hasSample = true;
        }
    }

    /// <summary>
    /// Clears one provider's pending buckets and lifetime counter, and deducts
    /// its share from the all-time total. Quota counters are independent and remain intact.
    /// </summary>
    public void ResetProvider(string providerKey)
    {
        if (string.IsNullOrEmpty(providerKey)) return;
        lock (_sampleGate)
        {
            foreach (var key in _buckets.Keys.Where(key => key.ProviderKey == providerKey))
                _buckets.TryRemove(key, out _);
            if (_lifetime.TryRemove(providerKey, out var removed))
                Interlocked.Add(ref _lifetimeAll, -removed);
            _lastSampleBytes = LifetimeAll;
            _lastSampleTimestamp = _timestampProvider();
            _hasSample = true;
        }
    }

    private static long NowMinute()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return nowMs - (nowMs % OneMinute);
    }
}
