using System.Collections.Concurrent;
using System.Diagnostics;

namespace NzbWebDAV.Streams;

/// <summary>
/// Byte-bounded buffer pool with 256 KiB size classes designed for Usenet segment
/// drains (~750 KiB typical). Reduces bucket waste versus <see cref="System.Buffers.ArrayPool{T}.Shared"/>
/// power-of-two buckets while bounding total idle retention in bytes.
/// </summary>
public sealed class SegmentBufferPool : ISegmentBufferPool
{
    private const int SizeClassGranularity = 256 * 1024;
    private const int MaxBuffersPerClass = 64;

    private readonly long _maxIdleBytes;
    private readonly ConcurrentDictionary<int, ConcurrentBag<byte[]>> _buckets = new();
    private long _idleBytes;
    private long _trimTimestamp;

    /// <param name="maxIdleBytes">
    /// Maximum bytes to retain idle across all size classes. When exceeded, the oldest
    /// class with the most waste is trimmed on the next return.
    /// </param>
    public SegmentBufferPool(long maxIdleBytes)
    {
        _maxIdleBytes = Math.Max(SizeClassGranularity, maxIdleBytes);
    }

    public long IdleBytes => Interlocked.Read(ref _idleBytes);
    public int ActiveSizeClasses => _buckets.Count(kv => !kv.Value.IsEmpty);

    public byte[] Rent(int minimumLength)
    {
        if (minimumLength <= 0) return [];

        var sizeClass = RoundToSizeClass(minimumLength);
        if (_buckets.TryGetValue(sizeClass, out var bag) && bag.TryTake(out var buffer))
        {
            Interlocked.Add(ref _idleBytes, -buffer.Length);
            return buffer;
        }

        return new byte[sizeClass];
    }

    public void Return(byte[] buffer)
    {
        if (buffer.Length == 0) return;

        var sizeClass = buffer.Length;
        var bag = _buckets.GetOrAdd(sizeClass, _ => new ConcurrentBag<byte[]>());

        if (bag.Count >= MaxBuffersPerClass)
            return;

        bag.Add(buffer);
        var idle = Interlocked.Add(ref _idleBytes, buffer.Length);

        if (idle > _maxIdleBytes)
            TrimExcess();
    }

    private void TrimExcess()
    {
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _trimTimestamp);
        if (Stopwatch.GetElapsedTime(last, now) < TimeSpan.FromSeconds(1)) return;
        if (Interlocked.CompareExchange(ref _trimTimestamp, now, last) != last) return;

        foreach (var (_, bag) in _buckets)
        {
            while (Interlocked.Read(ref _idleBytes) > _maxIdleBytes && bag.TryTake(out var evicted))
                Interlocked.Add(ref _idleBytes, -evicted.Length);
        }
    }

    internal static int RoundToSizeClass(int size) =>
        ((size + SizeClassGranularity - 1) / SizeClassGranularity) * SizeClassGranularity;
}
