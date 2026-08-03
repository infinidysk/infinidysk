namespace NzbWebDAV.Streams;

/// <summary>
/// Thread-safe counters tracking <see cref="PooledBufferStream"/> ownership and
/// bucket rounding. The shared instance describes production traffic; tests and
/// benchmarks can inject isolated instances.
/// </summary>
public sealed class BufferPoolDiagnostics
{
    public static BufferPoolDiagnostics Shared { get; } = new();

    private long _rents;
    private long _returns;
    private long _growths;
    private long _checkedOutBytes;
    private long _requestedBytes;
    private long _rentedBytes;
    private long _bucketWasteBytes;

    internal void RecordRent(int requested, int actualCapacity)
    {
        Interlocked.Increment(ref _rents);
        Interlocked.Add(ref _checkedOutBytes, actualCapacity);
        Interlocked.Add(ref _requestedBytes, requested);
        Interlocked.Add(ref _rentedBytes, actualCapacity);
        if (actualCapacity > requested)
            Interlocked.Add(ref _bucketWasteBytes, actualCapacity - requested);
    }

    internal void RecordReturn(int capacity)
    {
        Interlocked.Increment(ref _returns);
        Interlocked.Add(ref _checkedOutBytes, -capacity);
    }

    internal void RecordGrowth(int requested, int oldCapacity, int newCapacity)
    {
        Interlocked.Increment(ref _growths);
        RecordRent(requested, newCapacity);
        if (oldCapacity > 0)
            RecordReturn(oldCapacity);
    }

    public BufferPoolSnapshot Snapshot() => new(
        Interlocked.Read(ref _rents),
        Interlocked.Read(ref _returns),
        Interlocked.Read(ref _growths),
        Interlocked.Read(ref _checkedOutBytes),
        Interlocked.Read(ref _requestedBytes),
        Interlocked.Read(ref _rentedBytes),
        Interlocked.Read(ref _bucketWasteBytes));
}

public readonly record struct BufferPoolSnapshot(
    long Rents,
    long Returns,
    long Growths,
    long CheckedOutBytes,
    long RequestedBytes,
    long RentedBytes,
    long BucketWasteBytes);
