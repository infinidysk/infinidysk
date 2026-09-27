using NzbWebDAV.Services.NativeCache;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

/// <summary>Shares one reserved integrity buffer between readers when normal stream admission is full.</summary>
internal sealed class NativeCacheOverflowStream(NativeCacheStore store, NativeCacheIdentity identity,
    Func<CancellationToken, Task<Stream>> open, Func<bool> current, IDisposable watch,
    SemaphoreSlim slots, NativeCacheStatistics statistics) : FastReadOnlyStream, IStreamGenerationEvidence
{
    private readonly IDisposable _lease = store.AcquireLease(identity);
    private long _position;
    private bool _disposed;
    private Stream? _source;
    private NativeCachedStream? _cachedStream;
    private long _cachedBlock = -1;
    private long _lastSourceBlock = -1;
    public string GenerationIdentity => identity.Key;
    public bool IsSourceCurrent => current();
    public override long Length => identity.Length;
    public override bool CanSeek => true;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
    public override void Flush() { }

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (destination.IsEmpty || _position == Length) return 0;
        if (!current()) throw new IOException("Media source changed during this response. Retry the range.");
        var currentBlock = _position / NativeCacheStore.BlockSize;
        if (_cachedStream is not null && _cachedBlock != currentBlock)
        {
            await _cachedStream.DisposeAsync().ConfigureAwait(false);
            _cachedStream = null;
            _cachedBlock = -1;
        }
        if (_cachedStream is not null)
        {
            _cachedStream.Position = _position;
            var cached = await _cachedStream.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
            if (!current()) throw new IOException("Media source changed during this response. Retry the range.");
            _position += cached;
            await ReleaseCompletedBlockAsync().ConfigureAwait(false);
            return cached;
        }
        // This slot is reserved inside the configured budget. Idle overflow streams
        // retain no buffers, and warming cannot consume the reserved hit capacity.
        if (!await slots.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false))
        {
            _source ??= await open(cancellationToken).ConfigureAwait(false);
            _source.Position = _position;
            var start = _position;
            var read = await _source.ReadAsync(destination[..(int)Math.Min(destination.Length, Length - _position)], cancellationToken).ConfigureAwait(false);
            statistics.SourceBytes(read);
            for (var block = start / NativeCacheStore.BlockSize * NativeCacheStore.BlockSize;
                block < start + read; block += NativeCacheStore.BlockSize)
                if (block != _lastSourceBlock) { statistics.Miss(); _lastSourceBlock = block; }
            if (!current()) throw new IOException("Media source changed during this response. Retry the range.");
            _position += read;
            return read;
        }
        _cachedStream = new NativeCachedStream(store, identity, open, current,
            new SlotLease(slots), statistics: statistics) { Position = _position };
        _cachedBlock = currentBlock;
        var count = await _cachedStream.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
        if (!current()) throw new IOException("Media source changed during this response. Retry the range.");
        _position += count;
        await ReleaseCompletedBlockAsync().ConfigureAwait(false);
        return count;
    }

    private async Task ReleaseCompletedBlockAsync()
    {
        if (_cachedStream is null || (_position < Length && _position / NativeCacheStore.BlockSize == _cachedBlock)) return;
        await _cachedStream.DisposeAsync().ConfigureAwait(false);
        _cachedStream = null;
        _cachedBlock = -1;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var next = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (next < 0 || next > Length) throw new ArgumentOutOfRangeException(nameof(offset));
        return _position = next;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try { _cachedStream?.Dispose(); _source?.Dispose(); }
            finally { try { watch.Dispose(); } finally { _lease.Dispose(); } }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            try
            {
                if (_cachedStream is not null) await _cachedStream.DisposeAsync().ConfigureAwait(false);
                if (_source is not null) await _source.DisposeAsync().ConfigureAwait(false);
            }
            finally { try { watch.Dispose(); } finally { _lease.Dispose(); } }
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class SlotLease(SemaphoreSlim slots) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) slots.Release(); }
    }
}
