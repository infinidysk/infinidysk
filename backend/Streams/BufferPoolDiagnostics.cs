namespace NzbWebDAV.Streams;

/// <summary>
/// Process-wide counters tracking <see cref="PooledBufferStream"/> rent/return
/// behavior for the active <see cref="ISegmentBufferPool"/> implementation.
/// Thread-safe; intended for support packs and live diagnostics, not hot-path gating.
/// </summary>
public static class BufferPoolDiagnostics
{
    private static long _rents;
    private static long _returns;
    private static long _growths;
    private static long _activeBytes;
    private static long _wastedBytes;

    public static long Rents => Interlocked.Read(ref _rents);
    public static long Returns => Interlocked.Read(ref _returns);
    public static long Growths => Interlocked.Read(ref _growths);
    public static long ActiveBytes => Interlocked.Read(ref _activeBytes);

    /// <summary>
    /// Cumulative bytes wasted to bucket rounding (rented capacity − requested capacity).
    /// </summary>
    public static long WastedBytes => Interlocked.Read(ref _wastedBytes);

    internal static void RecordRent(int requested, int actualCapacity)
    {
        Interlocked.Increment(ref _rents);
        Interlocked.Add(ref _activeBytes, actualCapacity);
        if (actualCapacity > requested)
            Interlocked.Add(ref _wastedBytes, actualCapacity - requested);
    }

    internal static void RecordReturn(int capacity)
    {
        Interlocked.Increment(ref _returns);
        Interlocked.Add(ref _activeBytes, -capacity);
    }

    internal static void RecordGrowth(int oldCapacity, int newCapacity)
    {
        Interlocked.Increment(ref _growths);
        Interlocked.Add(ref _activeBytes, newCapacity - oldCapacity);
        if (newCapacity > oldCapacity)
            Interlocked.Add(ref _wastedBytes, newCapacity - oldCapacity);
    }

    /// <summary>Snapshot for serialization into support packs or websocket stats.</summary>
    public static BufferPoolSnapshot Snapshot() => new(
        Rents, Returns, Growths, ActiveBytes, WastedBytes);

    internal static void Reset()
    {
        Interlocked.Exchange(ref _rents, 0);
        Interlocked.Exchange(ref _returns, 0);
        Interlocked.Exchange(ref _growths, 0);
        Interlocked.Exchange(ref _activeBytes, 0);
        Interlocked.Exchange(ref _wastedBytes, 0);
    }
}

public readonly record struct BufferPoolSnapshot(
    long Rents,
    long Returns,
    long Growths,
    long ActiveBytes,
    long WastedBytes);
