using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SharpCompress;

internal sealed class LazyAsyncReadOnlyCollection<T>(IAsyncEnumerable<T> source)
    : IAsyncEnumerable<T>,
        IDisposable
{
    private readonly List<T> _backing = new();
    private readonly IAsyncEnumerator<T> _source = source.GetAsyncEnumerator();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _fullyLoaded;
    private bool _disposed;

    private class LazyLoader(
        LazyAsyncReadOnlyCollection<T> lazyReadOnlyCollection,
        CancellationToken cancellationToken
    ) : IAsyncEnumerator<T>
    {
        private bool _disposed;
        private int _index = -1;

        // Captured under the collection gate so Current does not race with List.Add/resize.
        private T? _current = default;

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
            }
            return default;
        }

        public async ValueTask<bool> MoveNextAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            await lazyReadOnlyCollection._gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_index + 1 < lazyReadOnlyCollection._backing.Count)
                {
                    _index++;
                    _current = lazyReadOnlyCollection._backing[_index];
                    return true;
                }

                if (
                    !lazyReadOnlyCollection._fullyLoaded
                    && await lazyReadOnlyCollection._source.MoveNextAsync().ConfigureAwait(false)
                )
                {
                    lazyReadOnlyCollection._backing.Add(lazyReadOnlyCollection._source.Current);
                    _index++;
                    _current = lazyReadOnlyCollection._backing[_index];
                    return true;
                }

                // Only mark fully loaded when the source actually reached EOF.
                if (!lazyReadOnlyCollection._fullyLoaded)
                {
                    lazyReadOnlyCollection._fullyLoaded = true;
                }

                return false;
            }
            finally
            {
                lazyReadOnlyCollection._gate.Release();
            }
        }

        #region IEnumerator<T> Members

        public T Current => _current!;

        #endregion

        #region IDisposable Members

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }

        #endregion
    }

    internal async ValueTask EnsureFullyLoaded()
    {
        if (!_fullyLoaded)
        {
            var loader = new LazyLoader(this, CancellationToken.None);
            while (await loader.MoveNextAsync().ConfigureAwait(false))
            {
                // Intentionally empty
            }
            _fullyLoaded = true;
        }
    }

    internal IEnumerable<T> GetLoaded() => _backing;

    #region ICollection<T> Members

    public void Add(T item) => throw new NotSupportedException();

    public void Clear() => throw new NotSupportedException();

    public bool IsReadOnly => true;

    public bool Remove(T item) => throw new NotSupportedException();

    #endregion

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new LazyLoader(this, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }
}
