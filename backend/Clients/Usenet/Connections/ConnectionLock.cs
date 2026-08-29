namespace NzbWebDAV.Clients.Usenet.Connections;

/// <summary>
/// Disposable wrapper that automatically returns a borrowed connection to the
/// originating <see cref="ConnectionPool{T}"/>.
///
/// Note: This class was authored by ChatGPT 3o
/// </summary>
public sealed class ConnectionLock<T> : IDisposable
{
    private readonly Action<T> _syncReturn;
    private readonly Action<T, string?> _syncDestroy;
    private T? _connection;
    private Action? _onDisposed;
    private int _disposed; // 0 == false, 1 == true
    private int _replace; // 0 == false, 1 == true
    private string? _replacementReason;

    internal ConnectionLock
    (
        T connection,
        Action<T> syncReturn,
        Action<T, string?> syncDestroy,
        bool wasReused
    )
    {
        _connection = connection;
        _syncReturn = syncReturn;
        _syncDestroy = syncDestroy;
        WasReused = wasReused;
    }

    public T Connection
        => _connection ?? throw new ObjectDisposedException(nameof(ConnectionLock<T>));

    /// <summary>
    /// True when the connection was taken from the idle pool rather than freshly
    /// created. A reused connection may have been closed server-side while idle,
    /// so its first failure should not count against provider health or retry budgets.
    /// </summary>
    public bool WasReused { get; }

    /// <summary>
    /// Marks the underlying connection to be replaced. When this lock is disposed,
    /// the underlying connection will be destroyed instead of returned to the pool.
    /// </summary>
    public void Replace(string? reason = null)
    {
        _replacementReason ??= reason;
        Volatile.Write(ref _replace, 1);
    }

    /// <summary>
    /// Couples an operation-level admission lease to this physical connection lease.
    /// The callback runs exactly once when the connection is returned or destroyed.
    /// </summary>
    internal void AttachDisposeCallback(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (Interlocked.CompareExchange(ref _onDisposed, callback, null) is not null)
            throw new InvalidOperationException("A dispose callback is already attached.");

        // Defensive race handling: callers attach before publishing the lock, but if a
        // concurrent dispose wins, release the operation lease here instead.
        if (Volatile.Read(ref _disposed) == 1)
            Interlocked.Exchange(ref _onDisposed, null)?.Invoke();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return; // already done
        try
        {
            var conn = Interlocked.Exchange(ref _connection, default);
            if (conn is not null)
            {
                var replace = Volatile.Read(ref _replace) == 1;
                if (replace)
                    _syncDestroy(conn, _replacementReason);
                else
                    _syncReturn(conn);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _onDisposed, null)?.Invoke();
        }

        GC.SuppressFinalize(this);
    }
}
