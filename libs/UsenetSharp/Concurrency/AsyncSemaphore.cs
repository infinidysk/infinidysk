namespace UsenetSharp.Concurrency;

public class AsyncSemaphore : IDisposable
{
    private readonly LinkedList<TaskCompletionSource<bool>> _waiters = new();
    private int _currentCount;
    private bool _disposed;
    private readonly object _lock = new();

    public AsyncSemaphore(int initialCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCount);
        _currentCount = initialCount;
    }

    public bool TryWait()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_currentCount > 0)
            {
                _currentCount--;
                return true;
            }

            return false;
        }
    }

    public Task WaitAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_currentCount > 0)
            {
                _currentCount--;
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var node = _waiters.AddLast(tcs);

            if (cancellationToken.CanBeCanceled)
            {
                var registration = cancellationToken.Register(() =>
                {
                    bool removed = false;
                    lock (_lock)
                    {
                        try
                        {
                            _waiters.Remove(node);
                            removed = true;
                        }
                        catch (InvalidOperationException)
                        {
                            // intentionally left blank
                        }
                    }

                    if (removed)
                        tcs.TrySetCanceled(cancellationToken);
                });

                tcs.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
            }

            return tcs.Task;
        }
    }

    public void Release()
    {
        TaskCompletionSource<bool>? toRelease = null;
        lock (_lock)
        {
            // Tolerate release after disposal so cleanup paths that follow a
            // Release (e.g. connection-ready callbacks) are never skipped.
            if (_disposed)
                return;

            while (_waiters.Count > 0)
            {
                var node = _waiters.First!;
                _waiters.RemoveFirst();

                // Skip canceled tasks
                if (!node.Value.Task.IsCanceled)
                {
                    toRelease = node.Value;
                    break;
                }
            }

            if (toRelease == null)
            {
                _currentCount++;
                return;
            }
        }

        toRelease.TrySetResult(true);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing) return;
        List<TaskCompletionSource<bool>> waitersToCancel;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            waitersToCancel = new List<TaskCompletionSource<bool>>(_waiters);
            _waiters.Clear();
        }

        foreach (var tcs in waitersToCancel)
            tcs.TrySetException(new ObjectDisposedException(nameof(AsyncSemaphore)));
    }
}
