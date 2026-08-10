namespace NzbWebDAV.Services;

/// <summary>
/// Serializes writes that can replace indexer/profile config blobs, so a Prowlarr
/// sync cannot overwrite a concurrent Settings save (or vice versa).
/// </summary>
public sealed class IndexerConfigWriteLock : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
