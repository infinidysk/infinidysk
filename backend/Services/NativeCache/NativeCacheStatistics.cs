namespace NzbWebDAV.Services.NativeCache;

/// <summary>Constant-space, identity-free process counters; snapshots are approximate under concurrent reads.</summary>
public sealed class NativeCacheStatistics
{
    private long _hitBlocks, _hitBytes, _missBlocks, _committedBytes, _fallbacks, _ioTimeouts;
    public void Hit(long bytes) { Interlocked.Increment(ref _hitBlocks); Interlocked.Add(ref _hitBytes, bytes); }
    public void Miss() => Interlocked.Increment(ref _missBlocks);
    public void Committed(long bytes) => Interlocked.Add(ref _committedBytes, bytes);
    public void Fallback(bool timeout = false)
    {
        Interlocked.Increment(ref _fallbacks);
        if (timeout) Interlocked.Increment(ref _ioTimeouts);
    }
    public NativeCacheSnapshot Snapshot() => new(Interlocked.Read(ref _hitBlocks), Interlocked.Read(ref _hitBytes),
        Interlocked.Read(ref _missBlocks), Interlocked.Read(ref _committedBytes), Interlocked.Read(ref _fallbacks), Interlocked.Read(ref _ioTimeouts));
}

public sealed record NativeCacheSnapshot(long HitBlocks, long HitBytes, long MissBlocks, long CommittedBytes, long Fallbacks, long IoTimeouts);
