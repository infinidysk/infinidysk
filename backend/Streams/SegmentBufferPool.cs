namespace NzbWebDAV.Streams;

/// <summary>
/// Evaluation pool for Usenet segment drains. Uses 256 KiB size classes, strictly
/// bounds idle retention, reclaims across classes, and expires stale buffers.
/// </summary>
public sealed class SegmentBufferPool : ISegmentBufferPool
{
    internal const int SizeClassGranularity = 256 * 1024;
    private const int DefaultMaxBuffersPerClass = 64;
    private static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private readonly long _maxIdleBytes;
    private readonly int _maxBuffersPerClass;
    private readonly TimeSpan _staleAfter;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<int, Queue<IdleBuffer>> _buckets = [];
    private readonly HashSet<byte[]> _checkedOut = new(ReferenceEqualityComparer.Instance);

    private long _idleBytes;
    private long _trimmedBytes;
    private long _rentCount;
    private long _returnCount;
    private long _reuseCount;
    private long _allocationCount;

    public SegmentBufferPool(
        long maxIdleBytes,
        TimeSpan? staleAfter = null,
        int maxBuffersPerClass = DefaultMaxBuffersPerClass,
        TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxIdleBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBuffersPerClass, 1);

        _maxIdleBytes = maxIdleBytes;
        _maxBuffersPerClass = maxBuffersPerClass;
        _staleAfter = staleAfter ?? DefaultStaleAfter;
        if (_staleAfter < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(staleAfter));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public byte[] Rent(int minimumLength)
    {
        if (minimumLength < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumLength));
        if (minimumLength == 0) return [];
        if (minimumLength > Array.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(minimumLength));

        var sizeClass = RoundToSizeClass(minimumLength);
        var now = _timeProvider.GetUtcNow();

        lock (_gate)
        {
            TrimStaleLocked(now);
            if (_buckets.TryGetValue(sizeClass, out var bucket) && bucket.Count > 0)
            {
                var idle = bucket.Dequeue();
                _idleBytes -= idle.Buffer.Length;
                if (bucket.Count == 0)
                    _buckets.Remove(sizeClass);
                _checkedOut.Add(idle.Buffer);
                _rentCount++;
                _reuseCount++;
                return idle.Buffer;
            }
        }

        var allocated = new byte[sizeClass];
        lock (_gate)
        {
            _checkedOut.Add(allocated);
            _rentCount++;
            _allocationCount++;
        }
        return allocated;
    }

    public void Return(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Length == 0) return;

        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (!_checkedOut.Remove(buffer))
                throw new InvalidOperationException(
                    "The segment buffer was not rented from this pool or was already returned.");

            _returnCount++;
            TrimStaleLocked(now);

            if (buffer.Length > _maxIdleBytes)
            {
                _trimmedBytes += buffer.Length;
                return;
            }

            if (_buckets.TryGetValue(buffer.Length, out var existingBucket) &&
                existingBucket.Count >= _maxBuffersPerClass)
            {
                _trimmedBytes += buffer.Length;
                return;
            }

            ReclaimForLocked(buffer.Length);
            if (_idleBytes + buffer.Length > _maxIdleBytes)
            {
                _trimmedBytes += buffer.Length;
                return;
            }

            var bucket = GetOrCreateBucketLocked(buffer.Length);
            bucket.Enqueue(new IdleBuffer(buffer, now));
            _idleBytes += buffer.Length;
        }
    }

    public SegmentBufferPoolSnapshot Snapshot()
    {
        lock (_gate)
        {
            var classes = _buckets
                .OrderBy(x => x.Key)
                .Select(x => new SegmentBufferPoolClassSnapshot(
                    x.Key, x.Value.Count, (long)x.Key * x.Value.Count))
                .ToArray();
            return new SegmentBufferPoolSnapshot(
                _idleBytes,
                _trimmedBytes,
                _checkedOut.Sum(x => (long)x.Length),
                _rentCount,
                _returnCount,
                _reuseCount,
                _allocationCount,
                classes);
        }
    }

    internal static int RoundToSizeClass(int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, Array.MaxLength);

        var rounded = ((long)size + SizeClassGranularity - 1)
                      / SizeClassGranularity
                      * SizeClassGranularity;
        return rounded <= Array.MaxLength ? (int)rounded : size;
    }

    private Queue<IdleBuffer> GetOrCreateBucketLocked(int sizeClass)
    {
        if (_buckets.TryGetValue(sizeClass, out var bucket)) return bucket;
        bucket = new Queue<IdleBuffer>();
        _buckets.Add(sizeClass, bucket);
        return bucket;
    }

    private void ReclaimForLocked(int incomingBytes)
    {
        while (_idleBytes + incomingBytes > _maxIdleBytes)
        {
            var oldest = _buckets
                .Where(x => x.Value.Count > 0)
                .OrderBy(x => x.Value.Peek().ReturnedAt)
                .FirstOrDefault();
            if (oldest.Value is null) return;

            var evicted = oldest.Value.Dequeue();
            _idleBytes -= evicted.Buffer.Length;
            _trimmedBytes += evicted.Buffer.Length;
            if (oldest.Value.Count == 0)
                _buckets.Remove(oldest.Key);
        }
    }

    private void TrimStaleLocked(DateTimeOffset now)
    {
        if (_buckets.Count == 0) return;
        var cutoff = now - _staleAfter;
        foreach (var (sizeClass, bucket) in _buckets.ToArray())
        {
            while (bucket.Count > 0 && bucket.Peek().ReturnedAt <= cutoff)
            {
                var evicted = bucket.Dequeue();
                _idleBytes -= evicted.Buffer.Length;
                _trimmedBytes += evicted.Buffer.Length;
            }

            if (bucket.Count == 0)
                _buckets.Remove(sizeClass);
        }
    }

    private sealed record IdleBuffer(byte[] Buffer, DateTimeOffset ReturnedAt);
}

public readonly record struct SegmentBufferPoolSnapshot(
    long IdleBytes,
    long TrimmedBytes,
    long CheckedOutBytes,
    long RentCount,
    long ReturnCount,
    long ReuseCount,
    long AllocationCount,
    IReadOnlyList<SegmentBufferPoolClassSnapshot> SizeClasses);

public readonly record struct SegmentBufferPoolClassSnapshot(
    int BufferSize,
    int BufferCount,
    long IdleBytes);
