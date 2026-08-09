namespace NzbWebDAV.Services.Benchmark;

/// <summary>
/// Process-wide cancellation for the in-flight speed test. The HTTP request that
/// started the run may be dropped by an intermediary timeout while the test is
/// still going; we must not treat that disconnect as user cancel. Explicit
/// cancel (UI Cancel / modal close) calls <see cref="Cancel"/> instead.
/// </summary>
public sealed class BenchmarkRunControl : IDisposable
{
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    /// <summary>Starts a new run token, cancelling any previous one.</summary>
    public CancellationToken Begin()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            return _cts.Token;
        }
    }

    /// <summary>Cancels the current run, if any.</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _cts?.Cancel();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _cts?.Dispose();
            _cts = null;
        }
        GC.SuppressFinalize(this);
    }

}
