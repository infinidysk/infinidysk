using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace NzbWebDAV.Streams;

internal enum SharedStreamBufferCategory
{
    Ring,
    PumpScratch,
}

/// <summary>
/// Process-wide actual ArrayPool capacity rented by shared-stream rings and pump
/// scratch buffers. Counts <see cref="Array.Length"/>, not requested minima or
/// logical filled bytes. Returning a buffer to the pool does not release those
/// pages to the OS.
/// </summary>
public sealed class SharedStreamRetentionAccount
{
    public static SharedStreamRetentionAccount Instance { get; } = new();

    private long _ringCurrent;
    private long _ringPeak;
    private long _scratchCurrent;
    private long _scratchPeak;

    internal void Add(SharedStreamBufferCategory category, long delta)
    {
        if (delta == 0)
            return;

        if (category == SharedStreamBufferCategory.PumpScratch)
            Add(ref _scratchCurrent, ref _scratchPeak, delta);
        else
            Add(ref _ringCurrent, ref _ringPeak, delta);
    }

    public SharedStreamRetentionSnapshot Snapshot() =>
        new(
            Volatile.Read(ref _ringCurrent),
            Volatile.Read(ref _ringPeak),
            Volatile.Read(ref _scratchCurrent),
            Volatile.Read(ref _scratchPeak));

    internal void Reset()
    {
        Volatile.Write(ref _ringCurrent, 0);
        Volatile.Write(ref _ringPeak, 0);
        Volatile.Write(ref _scratchCurrent, 0);
        Volatile.Write(ref _scratchPeak, 0);
    }

    private static void Add(ref long current, ref long peak, long delta)
    {
        var value = Interlocked.Add(ref current, delta);
        if (value < 0)
            Interlocked.CompareExchange(ref current, 0, value);

        if (delta <= 0)
            return;

        var observed = Volatile.Read(ref peak);
        while (value > observed)
        {
            var previous = Interlocked.CompareExchange(ref peak, value, observed);
            if (previous == observed)
                break;
            observed = previous;
        }
    }
}

public readonly record struct SharedStreamRetentionSnapshot(
    long RingRentedBytes,
    long RingRentedBytesPeak,
    long PumpScratchRentedBytes,
    long PumpScratchRentedBytesPeak);

internal sealed class SharedStreamAccountingPool : ISegmentBufferPool
{
    public static readonly SharedStreamAccountingPool Ring =
        new(SharedStreamBufferCategory.Ring);

    public static readonly SharedStreamAccountingPool PumpScratch =
        new(SharedStreamBufferCategory.PumpScratch);

    private readonly SharedStreamBufferCategory _category;
    private readonly ISegmentBufferPool _inner;
    private readonly SharedStreamRetentionAccount _account;
    private readonly ConcurrentDictionary<byte[], int> _rented = new(ByteArrayRefComparer.Instance);

    public SharedStreamAccountingPool(
        SharedStreamBufferCategory category,
        ISegmentBufferPool? inner = null,
        SharedStreamRetentionAccount? account = null)
    {
        _category = category;
        _inner = inner ?? SharedArrayPoolAdapter.Instance;
        _account = account ?? SharedStreamRetentionAccount.Instance;
    }

    public byte[] Rent(int minimumLength)
    {
        var buffer = _inner.Rent(minimumLength);
        if (_rented.TryAdd(buffer, buffer.Length))
            _account.Add(_category, buffer.Length);
        return buffer;
    }

    public void Return(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!_rented.TryRemove(buffer, out var length))
            return;

        _account.Add(_category, -length);
        _inner.Return(buffer);
    }

    private sealed class ByteArrayRefComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayRefComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y) => ReferenceEquals(x, y);

        public int GetHashCode(byte[] obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
