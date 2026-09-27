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
    private static readonly AsyncLocal<bool> Current = new();
    private readonly bool _previous = Current.Value;
    public static bool IsActive => Current.Value;
    public NativeCacheReadContext() => Current.Value = true;
    public void Dispose() => Current.Value = _previous;
}
