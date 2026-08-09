using System.Runtime.CompilerServices;

namespace NzbWebDAV.Streams;

/// <summary>
/// Evaluation pool for Usenet segment drains. Uses 256 KiB size classes, strictly
/// bounds idle retention, reclaims across classes, and expires stale buffers.
/// Checked-out buffers are tracked weakly so a caller that leaks a rented buffer
/// costs only that allocation — the pool never roots it. Returning a buffer the
/// pool does not recognize (foreign or already returned) is counted and ignored
/// rather than thrown, matching what production streaming code could tolerate.
/// </summary>
public sealed class SegmentBufferPool : ISegmentBufferPool
{
    internal const int SizeClassGranularity = 256 * 1024;
    private const int DefaultMaxBuffersPerClass = 64;
    private static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromMinutes(2);
    private static readonly object CheckedOutMarker = new();

    private readonly object _gate = new();
    private readonly long _maxIdleBytes;
    private readonly int _maxBuffersPerClass;
    private readonly TimeSpan _staleAfter;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<int, Queue<IdleBuffer>> _buckets = [];
    private readonly ConditionalWeakTable<byte[], object> _checkedOut = new();

    private long _idleBytes;
    private long _trimmedBytes;
    private long _checkedOutBytes;
    private long _rentCount;
    private long _returnCount;
    private long _rejectedReturnCount;
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
        ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);
        if (minimumLength == 0) return [];
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minimumLength, Array.MaxLength);

        var sizeClass = RoundToSizeClass(minimumLength);
        var now = _timeProvider.GetUtcNow();

        lock (_gate)
        {
            TrimStaleLocked(now);
            if (_buckets.TryGetValue(sizeClass, out var bucket) && bucket.Count > 0)
            {
                var idle = bucket.Dequeue();
                _idleBytes -= idle.Buffer.Length;
                _checkedOut.Add(idle.Buffer, CheckedOutMarker);
                _checkedOutBytes += idle.Buffer.Length;
                _rentCount++;
                _reuseCount++;
                return idle.Buffer;
            }
        }

        var allocated = new byte[sizeClass];
        lock (_gate)
        {
            _checkedOut.Add(allocated, CheckedOutMarker);
            _checkedOutBytes += allocated.Length;
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
            {
                // Foreign or double-returned buffer. Pooling it anyway would hand
                // the same array to two renters, so drop it — but never throw:
                // a caller bug must not escalate into a stream-crashing exception.
                _rejectedReturnCount++;
                return;
            }

            _checkedOutBytes -= buffer.Length;
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
                .Where(x => x.Value.Count > 0)
                .OrderBy(x => x.Key)
                .Select(x => new SegmentBufferPoolClassSnapshot(
                    x.Key, x.Value.Count, (long)x.Key * x.Value.Count))
                .ToArray();
            return new SegmentBufferPoolSnapshot(
                _idleBytes,
                _trimmedBytes,
                _checkedOutBytes,
                _rentCount,
                _returnCount,
                _rejectedReturnCount,
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
            Queue<IdleBuffer>? oldest = null;
            foreach (var bucket in _buckets.Values)
            {
                if (bucket.Count == 0) continue;
                if (oldest is null ||
                    bucket.Peek().ReturnedAt < oldest.Peek().ReturnedAt)
                {
                    oldest = bucket;
                }
            }

            if (oldest is null) return;
            var evicted = oldest.Dequeue();
            _idleBytes -= evicted.Buffer.Length;
            _trimmedBytes += evicted.Buffer.Length;
        }
    }

    private void TrimStaleLocked(DateTimeOffset now)
    {
        if (_buckets.Count == 0) return;
        var cutoff = now - _staleAfter;
        foreach (var bucket in _buckets.Values)
        {
            while (bucket.Count > 0 && bucket.Peek().ReturnedAt <= cutoff)
            {
                var evicted = bucket.Dequeue();
                _idleBytes -= evicted.Buffer.Length;
                _trimmedBytes += evicted.Buffer.Length;
            }
        }
    }

    private readonly record struct IdleBuffer(byte[] Buffer, DateTimeOffset ReturnedAt);
}

/// <param name="CheckedOutBytes">
/// Bytes rented and not yet returned. Buffers a caller leaked (never returned)
/// remain counted here even after the GC collects them, so sustained growth is
/// a leak signal rather than rooted memory.
/// </param>
/// <param name="RejectedReturnCount">
/// Returns ignored because the buffer was foreign to the pool or already returned.
/// </param>
public readonly record struct SegmentBufferPoolSnapshot(
    long IdleBytes,
    long TrimmedBytes,
    long CheckedOutBytes,
    long RentCount,
    long ReturnCount,
    long RejectedReturnCount,
    long ReuseCount,
    long AllocationCount,
    IReadOnlyList<SegmentBufferPoolClassSnapshot> SizeClasses);

public readonly record struct SegmentBufferPoolClassSnapshot(
    int BufferSize,
    int BufferCount,
    long IdleBytes);
