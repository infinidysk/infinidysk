namespace NzbWebDAV.Services.Catalog;

/// <summary>
/// A produced segment that is being streamed to a response. The lease keeps the cache entry
/// alive (and un-evictable) until the response finishes, and deletes transient segments —
/// those produced while the cache was full of active readers — on release.
/// </summary>
public sealed class CatalogSegmentLease : IDisposable
{
    private readonly Action _onRelease;
    private int _released;

    internal CatalogSegmentLease(string filePath, Action onRelease)
    {
        FilePath = filePath;
        _onRelease = onRelease;
    }

    public string FilePath { get; }

    public FileStream OpenRead()
        => new(
            FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            _onRelease();
    }
}
