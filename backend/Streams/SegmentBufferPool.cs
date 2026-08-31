using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using NzbWebDAV.Services.Diagnostics;

namespace NzbWebDAV.Streams;

internal enum SegmentBufferRetentionPolicy
{
    Legacy,
    CapacityOnly,
}

internal delegate void SegmentBufferAllocationFailureLogger(
    OutOfMemoryException exception,
    int requestedBytes,
    int roundedBytes,
    SegmentBufferPoolOomSnapshot snapshot);

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
    private readonly SegmentBufferRetentionPolicy _retentionPolicy;
    private readonly int _maxBuffersPerClass;
    private readonly TimeSpan _staleAfter;
    private readonly TimeProvider _timeProvider;
    private readonly Func<int, byte[]> _allocator;
    private readonly SegmentBufferAllocationFailureLogger _onAllocationFailure;
    private readonly Dictionary<int, Queue<IdleBuffer>> _buckets = [];
    private readonly Dictionary<int, LifetimeClassStats> _lifetimeClasses = [];
    private readonly ConditionalWeakTable<byte[], object> _checkedOut = new();

    private long _idleBytes;
    private long _trimmedBytes;
    private long _staleExpiredBytes;
    private long _classLimitDroppedBytes;
    private long _capacityEvictedBytes;
    private long _droppedTooLargeBytes;
    private long _checkedOutBytes;
    private long _rentCount;
    private long _returnCount;
    private long _rejectedReturnCount;
    private long _reuseCount;
    private long _allocationCount;
    private long _allocationAttemptCount;
    private long _allocationFailureCount;
    private long _returnSequence;

    public SegmentBufferPool(
        long maxIdleBytes,
        TimeSpan? staleAfter = null,
        int maxBuffersPerClass = DefaultMaxBuffersPerClass,
        TimeProvider? timeProvider = null)
        : this(
            maxIdleBytes,
            SegmentBufferRetentionPolicy.Legacy,
            staleAfter,
            maxBuffersPerClass,
            timeProvider,
            allocator: null)
    {
    }

    internal SegmentBufferPool(
        long maxIdleBytes,
        SegmentBufferRetentionPolicy retentionPolicy,
        TimeSpan? staleAfter = null,
        int maxBuffersPerClass = DefaultMaxBuffersPerClass,
        TimeProvider? timeProvider = null,
        Func<int, byte[]>? allocator = null,
        SegmentBufferAllocationFailureLogger? onAllocationFailure = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxIdleBytes);
        if (retentionPolicy is not (
            SegmentBufferRetentionPolicy.Legacy or SegmentBufferRetentionPolicy.CapacityOnly))
        {
            throw new ArgumentOutOfRangeException(nameof(retentionPolicy));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxBuffersPerClass, 1);
        _maxIdleBytes = maxIdleBytes;
        _retentionPolicy = retentionPolicy;
        _maxBuffersPerClass = maxBuffersPerClass;
        _staleAfter = staleAfter ?? DefaultStaleAfter;
        if (_staleAfter < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(staleAfter));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _allocator = allocator ?? (static length => new byte[length]);
        _onAllocationFailure = onAllocationFailure ?? LogAllocationFailureDefault;
    }

    public byte[] Rent(int minimumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);
        if (minimumLength == 0) return [];
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minimumLength, Array.MaxLength);

        var sizeClass = RoundToSizeClass(minimumLength);
        if (TryRentIdle(sizeClass, out var reused))
            return reused;

        RecordAllocationAttempt(sizeClass);
        byte[] allocated;
        try
        {
            allocated = _allocator(sizeClass);
        }
        catch (OutOfMemoryException oom)
        {
            var snapshot = RecordAllocationFailureAndSnapshotForOom(sizeClass);
            TryLogAllocationFailure(oom, minimumLength, sizeClass, snapshot);
            throw;
        }

        lock (_gate)
        {
            _checkedOut.Add(allocated, CheckedOutMarker);
            _checkedOutBytes += allocated.Length;
            _rentCount++;
            _allocationCount++;
            if (TryGetPoolableLifetimeLocked(sizeClass) is { } lifetime)
            {
                lifetime.RentCount++;
                lifetime.AllocationCount++;
            }
        }

        return allocated;
    }

    public void Return(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Length == 0) return;

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
            if (TryGetPoolableLifetimeLocked(buffer.Length) is { } lifetime)
                lifetime.ReturnCount++;

            if (_retentionPolicy == SegmentBufferRetentionPolicy.Legacy)
            {
                var returnedAt = _timeProvider.GetUtcNow();
                TrimStaleLocked(returnedAt);
                ReturnIdleLocked(buffer, returnedAt);
                return;
            }

            ReturnIdleLocked(buffer, returnedAt: default);
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
            var lifetime = _lifetimeClasses
                .OrderBy(x => x.Key)
                .Select(x => new SegmentBufferPoolLifetimeClassSnapshot(
                    x.Key,
                    x.Value.RentCount,
                    x.Value.ReturnCount,
                    x.Value.ReuseCount,
                    x.Value.AllocationAttemptCount,
                    x.Value.AllocationCount,
                    x.Value.AllocationFailureCount,
                    x.Value.StaleExpiredBytes,
                    x.Value.ClassLimitDroppedBytes,
                    x.Value.CapacityEvictedBytes))
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
                classes)
            {
                MaxIdleBytes = _maxIdleBytes,
                StaleExpiredBytes = _staleExpiredBytes,
                ClassLimitDroppedBytes = _classLimitDroppedBytes,
                CapacityEvictedBytes = _capacityEvictedBytes,
                DroppedTooLargeBytes = _droppedTooLargeBytes,
                AllocationAttemptCount = _allocationAttemptCount,
                AllocationFailureCount = _allocationFailureCount,
                LifetimeSizeClasses = lifetime,
            };
        }
    }

    /// <summary>
    /// Allocation-free aggregate ownership counters for frequent attribution
    /// sampling. Per-size-class diagnostics remain available from <see cref="Snapshot"/>.
    /// </summary>
    public SegmentBufferPoolMemorySnapshot MemorySnapshot()
    {
        lock (_gate)
        {
            return new SegmentBufferPoolMemorySnapshot(
                _retentionPolicy == SegmentBufferRetentionPolicy.Legacy
                    ? SegmentBufferPoolSelector.BoundedLegacyValue
                    : SegmentBufferPoolSelector.BoundedCapacityValue,
                _checkedOutBytes,
                _idleBytes,
                _maxIdleBytes,
                _rentCount,
                _returnCount,
                _rejectedReturnCount);
        }
    }

    internal SegmentBufferPoolOomSnapshot SnapshotForOom()
    {
        lock (_gate)
            return CaptureOomSnapshotLocked();
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

    private bool TryRentIdle(int sizeClass, out byte[] buffer)
    {
        lock (_gate)
        {
            if (_retentionPolicy == SegmentBufferRetentionPolicy.Legacy)
                TrimStaleLocked(_timeProvider.GetUtcNow());

            if (_buckets.TryGetValue(sizeClass, out var bucket) && bucket.Count > 0)
            {
                var idle = bucket.Dequeue();
                _idleBytes -= idle.Buffer.Length;
                _checkedOut.Add(idle.Buffer, CheckedOutMarker);
                _checkedOutBytes += idle.Buffer.Length;
                _rentCount++;
                _reuseCount++;
                if (TryGetPoolableLifetimeLocked(sizeClass) is { } lifetime)
                {
                    lifetime.RentCount++;
                    lifetime.ReuseCount++;
                }

                buffer = idle.Buffer;
                return true;
            }
        }

        buffer = null!;
        return false;
    }

    private void ReturnIdleLocked(byte[] buffer, DateTimeOffset returnedAt)
    {
        if (buffer.Length > _maxIdleBytes)
        {
            RecordDroppedTooLargeLocked(buffer.Length);
            return;
        }

        if (_retentionPolicy == SegmentBufferRetentionPolicy.Legacy &&
            IsClassAtLegacyLimitLocked(buffer.Length))
        {
            RecordClassLimitDroppedLocked(buffer.Length);
            return;
        }

        ReclaimForLocked(buffer.Length);
        var returnSequence = ++_returnSequence;
        GetOrCreateBucketLocked(buffer.Length)
            .Enqueue(new IdleBuffer(buffer, returnSequence, returnedAt));
        _idleBytes += buffer.Length;
    }

    private Queue<IdleBuffer> GetOrCreateBucketLocked(int sizeClass)
    {
        if (_buckets.TryGetValue(sizeClass, out var bucket)) return bucket;
        bucket = new Queue<IdleBuffer>();
        _buckets.Add(sizeClass, bucket);
        return bucket;
    }

    private bool IsClassAtLegacyLimitLocked(int bufferLength) =>
        _buckets.TryGetValue(bufferLength, out var existingBucket) &&
        existingBucket.Count >= _maxBuffersPerClass;

    private void ReclaimForLocked(int incomingBytes)
    {
        while (_idleBytes > 0 && incomingBytes > _maxIdleBytes - _idleBytes)
        {
            Queue<IdleBuffer>? oldest = null;
            foreach (var bucket in _buckets.Values)
            {
                if (bucket.Count == 0) continue;
                if (oldest is null ||
                    bucket.Peek().ReturnSequence < oldest.Peek().ReturnSequence)
                {
                    oldest = bucket;
                }
            }

            if (oldest is null) return;
            var evicted = oldest.Dequeue();
            _idleBytes -= evicted.Buffer.Length;
            RecordCapacityEvictedLocked(evicted.Buffer.Length);
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
                RecordStaleExpiredLocked(evicted.Buffer.Length);
            }
        }
    }

    private void RecordAllocationAttempt(int sizeClass)
    {
        lock (_gate)
        {
            _allocationAttemptCount++;
            if (TryGetPoolableLifetimeLocked(sizeClass) is { } lifetime)
                lifetime.AllocationAttemptCount++;
        }
    }

    private SegmentBufferPoolOomSnapshot RecordAllocationFailureAndSnapshotForOom(int sizeClass)
    {
        lock (_gate)
        {
            _allocationFailureCount++;
            if (TryGetPoolableLifetimeLocked(sizeClass) is { } lifetime)
                lifetime.AllocationFailureCount++;
            return CaptureOomSnapshotLocked();
        }
    }

    private SegmentBufferPoolOomSnapshot CaptureOomSnapshotLocked() =>
        new(
            _idleBytes,
            _trimmedBytes,
            _checkedOutBytes,
            _rentCount,
            _returnCount,
            _rejectedReturnCount,
            _reuseCount,
            _allocationCount,
            _allocationAttemptCount,
            _allocationFailureCount,
            _maxIdleBytes,
            _staleExpiredBytes,
            _classLimitDroppedBytes,
            _capacityEvictedBytes,
            _droppedTooLargeBytes);

    private LifetimeClassStats? TryGetPoolableLifetimeLocked(int sizeClass)
    {
        if (sizeClass > _maxIdleBytes) return null;
        if (_lifetimeClasses.TryGetValue(sizeClass, out var stats))
            return stats;

        stats = new LifetimeClassStats();
        _lifetimeClasses.Add(sizeClass, stats);
        return stats;
    }

    private void RecordStaleExpiredLocked(int bytes)
    {
        _trimmedBytes += bytes;
        _staleExpiredBytes += bytes;
        if (TryGetPoolableLifetimeLocked(bytes) is { } lifetime)
            lifetime.StaleExpiredBytes += bytes;
    }

    private void RecordClassLimitDroppedLocked(int bytes)
    {
        _trimmedBytes += bytes;
        _classLimitDroppedBytes += bytes;
        if (TryGetPoolableLifetimeLocked(bytes) is { } lifetime)
            lifetime.ClassLimitDroppedBytes += bytes;
    }

    private void RecordCapacityEvictedLocked(int bytes)
    {
        _trimmedBytes += bytes;
        _capacityEvictedBytes += bytes;
        if (TryGetPoolableLifetimeLocked(bytes) is { } lifetime)
            lifetime.CapacityEvictedBytes += bytes;
    }

    private void RecordDroppedTooLargeLocked(int bytes)
    {
        _trimmedBytes += bytes;
        _droppedTooLargeBytes += bytes;
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Diagnostic logging must not replace the original OutOfMemoryException.")]
    private void TryLogAllocationFailure(
        OutOfMemoryException oom,
        int requestedBytes,
        int roundedBytes,
        SegmentBufferPoolOomSnapshot snapshot)
    {
        try
        {
            _onAllocationFailure(oom, requestedBytes, roundedBytes, snapshot);
        }
        catch
        {
            // Diagnostic preparation must never replace the original OOM.
        }
    }

    private static void LogAllocationFailureDefault(
        OutOfMemoryException oom,
        int requestedBytes,
        int roundedBytes,
        SegmentBufferPoolOomSnapshot snapshot) =>
        OomDiagnostics.LogSegmentBufferAllocationFailure(
            oom, requestedBytes, roundedBytes, snapshot);

    private sealed class LifetimeClassStats
    {
        public long RentCount;
        public long ReturnCount;
        public long ReuseCount;
        public long AllocationAttemptCount;
        public long AllocationCount;
        public long AllocationFailureCount;
        public long StaleExpiredBytes;
        public long ClassLimitDroppedBytes;
        public long CapacityEvictedBytes;
    }

    private readonly record struct IdleBuffer(
        byte[] Buffer,
        long ReturnSequence,
        DateTimeOffset ReturnedAt);
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
    IReadOnlyList<SegmentBufferPoolClassSnapshot> SizeClasses)
{
    public long MaxIdleBytes { get; init; }
    public long StaleExpiredBytes { get; init; }
    public long ClassLimitDroppedBytes { get; init; }
    public long CapacityEvictedBytes { get; init; }
    public long DroppedTooLargeBytes { get; init; }
    public long AllocationAttemptCount { get; init; }
    public long AllocationFailureCount { get; init; }

    private readonly IReadOnlyList<SegmentBufferPoolLifetimeClassSnapshot>? _lifetimeSizeClasses;

    public IReadOnlyList<SegmentBufferPoolLifetimeClassSnapshot> LifetimeSizeClasses
    {
        get => _lifetimeSizeClasses ?? [];
        init => _lifetimeSizeClasses = value;
    }
}

public readonly record struct SegmentBufferPoolClassSnapshot(
    int BufferSize,
    int BufferCount,
    long IdleBytes);

/// <summary>
/// Cheap aggregate ownership snapshot for memory attribution sampling.
/// </summary>
public readonly record struct SegmentBufferPoolMemorySnapshot(
    string Mode,
    long CheckedOutCapacityBytes,
    long IdleCapacityBytes,
    long MaxIdleBytes,
    long RentCount,
    long ReturnCount,
    long RejectedReturnCount);

public readonly record struct SegmentBufferPoolLifetimeClassSnapshot(
    int BufferSize,
    long RentCount,
    long ReturnCount,
    long ReuseCount,
    long AllocationAttemptCount,
    long AllocationCount,
    long AllocationFailureCount,
    long StaleExpiredBytes,
    long ClassLimitDroppedBytes,
    long CapacityEvictedBytes);

internal readonly record struct SegmentBufferPoolOomSnapshot(
    long IdleBytes,
    long TrimmedBytes,
    long CheckedOutBytes,
    long RentCount,
    long ReturnCount,
    long RejectedReturnCount,
    long ReuseCount,
    long AllocationCount,
    long AllocationAttemptCount,
    long AllocationFailureCount,
    long MaxIdleBytes,
    long StaleExpiredBytes,
    long ClassLimitDroppedBytes,
    long CapacityEvictedBytes,
    long DroppedTooLargeBytes);
