namespace NzbWebDAV.Par2Recovery;

internal sealed class Par2MemoryBudget(long limit)
{
    private readonly object _sync = new();
    public long Limit { get; } = limit;
    public long ReservedBytes { get; private set; }
    public long PeakBytes { get; private set; }

    public IDisposable Reserve(long bytes)
    {
        Charge(bytes);
        return new Reservation(this, bytes);
    }

    public void Charge(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (_sync)
        {
            if (bytes > Limit - ReservedBytes)
                throw new Par2BudgetExceededException($"PAR2 memory reservation exceeds the {Limit}-byte repair cap.");
            ReservedBytes += bytes;
            PeakBytes = Math.Max(PeakBytes, ReservedBytes);
        }
    }

    public void Release(long bytes)
    {
        lock (_sync)
            ReservedBytes -= bytes;
    }

    private sealed class Reservation(Par2MemoryBudget budget, long bytes) : IDisposable
    {
        private long _bytes = bytes;
        public void Dispose() => budget.Release(Interlocked.Exchange(ref _bytes, 0));
    }
}

internal sealed class Par2BudgetExceededException(string message) : Exception(message);