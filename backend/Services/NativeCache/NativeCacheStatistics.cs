namespace NzbWebDAV.Services.NativeCache;

/// <summary>Process counters plus bounded five-minute deltas. No per-block database writes.</summary>
public sealed class NativeCacheStatistics
{
    private long _hitBlocks, _hitBytes, _missBlocks, _committedBytes, _fallbacks, _ioTimeouts;
    private readonly Lock _windowGate = new();
    private readonly Dictionary<long, NativeCacheTrafficBucket> _pending = [];
    private readonly Dictionary<long, NativeCacheTransfer> _transfers = [];
    private long _nextTransferId;
    public void Hit(long bytes)
    {
        Interlocked.Increment(ref _hitBlocks); Interlocked.Add(ref _hitBytes, bytes);
        RecordWindow(hits: 1, hitBytes: bytes);
    }
    public void Miss(long bytes = 0)
    {
        Interlocked.Increment(ref _missBlocks);
        RecordWindow(misses: 1, missBytes: bytes);
    }
    public void SourceBytes(long bytes)
    {
        if (bytes > 0) RecordWindow(missBytes: bytes);
    }
    public void Committed(long bytes)
    {
        Interlocked.Add(ref _committedBytes, bytes);
        RecordWindow(committed: bytes);
    }
    public void Fallback(bool timeout = false)
    {
        Interlocked.Increment(ref _fallbacks);
        if (timeout) Interlocked.Increment(ref _ioTimeouts);
    }
    public NativeCacheSnapshot Snapshot() => new(Interlocked.Read(ref _hitBlocks), Interlocked.Read(ref _hitBytes),
        Interlocked.Read(ref _missBlocks), Interlocked.Read(ref _committedBytes), Interlocked.Read(ref _fallbacks), Interlocked.Read(ref _ioTimeouts));

    private void RecordWindow(long hits = 0, long hitBytes = 0, long misses = 0, long missBytes = 0, long committed = 0)
    {
        var bucket = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300;
        lock (_windowGate)
        {
            var old = _pending.GetValueOrDefault(bucket) ?? new NativeCacheTrafficBucket(bucket, 0, 0, 0, 0, 0);
            _pending[bucket] = old with { HitBlocks = old.HitBlocks + hits, HitBytes = old.HitBytes + hitBytes,
                MissBlocks = old.MissBlocks + misses, MissBytes = old.MissBytes + missBytes,
                CommittedBytes = old.CommittedBytes + committed };
        }
    }

    public NativeCacheTrafficBucket Pending24h()
    {
        var since = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 86400) / 300;
        lock (_windowGate)
            return _pending.Values.Where(row => row.Bucket >= since).Aggregate(
                new NativeCacheTrafficBucket(since, 0, 0, 0, 0, 0), (sum, row) => sum.Add(row));
    }
    public long? FirstPendingBucket()
    {
        lock (_windowGate) return _pending.Count == 0 ? null : _pending.Keys.Min();
    }
    public IReadOnlyList<NativeCacheTrafficBucket> DrainTraffic()
    {
        lock (_windowGate)
        {
            var result = _pending.Values.ToArray();
            _pending.Clear();
            return result;
        }
    }
    public void RestoreTraffic(IReadOnlyList<NativeCacheTrafficBucket> rows)
    {
        lock (_windowGate)
            foreach (var row in rows)
                _pending[row.Bucket] = (_pending.GetValueOrDefault(row.Bucket) ??
                    new NativeCacheTrafficBucket(row.Bucket, 0, 0, 0, 0, 0)).Add(row);
    }

    public NativeCacheTransfer? BeginTransfer(string itemId, string? name, long length, bool background)
    {
        lock (_windowGate)
        {
            if (_transfers.Count >= 64) return null;
            var id = ++_nextTransferId;
            var transfer = new NativeCacheTransfer(this, id, itemId, name is { Length: > 256 } ? name[..256] : name,
                length, background, DateTimeOffset.UtcNow);
            _transfers[id] = transfer;
            return transfer;
        }
    }
    public IReadOnlyList<NativeCacheTransferSnapshot> ActiveTransfers()
    {
        lock (_windowGate) return _transfers.Values.Where(x => x.CommittedBytes > 0)
            .Select(x => x.Snapshot()).OrderByDescending(x => x.StartedAt).ToArray();
    }

    public sealed class NativeCacheTransfer(NativeCacheStatistics owner, long id, string itemId, string? name,
        long length, bool background, DateTimeOffset startedAt) : IDisposable
    {
        private long _bytes;
        private long _recentBytes;
        private DateTimeOffset _recentAt = DateTimeOffset.UtcNow;
        private bool _disposed;
        public long CommittedBytes => Interlocked.Read(ref _bytes);
        public void Committed(long bytes)
        {
            if (bytes <= 0) return;
            lock (owner._windowGate)
            {
                if (_disposed) return;
                Interlocked.Add(ref _bytes, bytes);
                var now = DateTimeOffset.UtcNow;
                if (now - _recentAt > TimeSpan.FromSeconds(5)) { _recentAt = now; _recentBytes = 0; }
                _recentBytes += bytes;
            }
        }
        internal NativeCacheTransferSnapshot Snapshot()
        {
            var seconds = Math.Max(1, (DateTimeOffset.UtcNow - _recentAt).TotalSeconds);
            return new(itemId, name, length, CommittedBytes, (long)(_recentBytes / seconds), startedAt, background);
        }
        public void Dispose()
        {
            lock (owner._windowGate)
            {
                if (_disposed) return;
                _disposed = true;
                owner._transfers.Remove(id);
            }
        }
    }
}

public sealed record NativeCacheSnapshot(long HitBlocks, long HitBytes, long MissBlocks, long CommittedBytes, long Fallbacks, long IoTimeouts);
public sealed record NativeCacheTrafficBucket(long Bucket, long HitBlocks, long HitBytes, long MissBlocks, long MissBytes, long CommittedBytes)
{
    public NativeCacheTrafficBucket Add(NativeCacheTrafficBucket other) => this with
    { HitBlocks = HitBlocks + other.HitBlocks, HitBytes = HitBytes + other.HitBytes,
      MissBlocks = MissBlocks + other.MissBlocks, MissBytes = MissBytes + other.MissBytes,
      CommittedBytes = CommittedBytes + other.CommittedBytes };
}
public sealed record NativeCacheTransferSnapshot(string ItemId, string? DisplayName, long Length,
    long CommittedBytes, long SpeedBytesPerSecond, DateTimeOffset StartedAt, bool Background);
