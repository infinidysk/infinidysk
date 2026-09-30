namespace NzbWebDAV.Streams;

/// <summary>
/// Evidence for the bytes returned by the last successful asynchronous read.
/// Absence means unknown, never trusted. Implementations must exclude synthetic
/// padding and bytes whose integrity will only be checked on a subsequent read.
/// </summary>
public interface ICacheReadEvidence
{
    bool LastReadCacheable { get; }
}

internal sealed class NativeCacheReadContext : IDisposable
{
    private static readonly AsyncLocal<long?> Current = new();
    private readonly long? _previous = Current.Value;
    public static bool IsActive => Current.Value is not null;
    public static long? ReadBudget => Current.Value;
    public NativeCacheReadContext(long readBudget = 4L * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(readBudget);
        Current.Value = readBudget;
    }
    public void Dispose() => Current.Value = _previous;
}
