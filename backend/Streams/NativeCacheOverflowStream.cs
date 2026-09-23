using NzbWebDAV.Services.NativeCache;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

/// <summary>Shares one reserved integrity buffer between readers when normal stream admission is full.</summary>
internal sealed class NativeCacheOverflowStream(NativeCacheStore store, NativeCacheIdentity identity,
    Func<CancellationToken, Task<Stream>> open, Func<bool> current, IDisposable watch,
    SemaphoreSlim slots, NativeCacheStatistics statistics) : FastReadOnlyStream, IStreamGenerationEvidence
{
    private long _position;
    private bool _disposed;
    private Stream? _source;
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
        // This slot is reserved inside the configured budget. Idle overflow streams
        // retain no buffers, and warming cannot consume the reserved hit capacity.
        if (!await slots.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false))
        {
            _source ??= await open(cancellationToken).ConfigureAwait(false);
            _source.Position = _position;
            var read = await _source.ReadAsync(destination[..(int)Math.Min(destination.Length, Length - _position)], cancellationToken).ConfigureAwait(false);
            if (!current()) throw new IOException("Media source changed during this response. Retry the range.");
            _position += read;
            return read;
        }
        await using var stream = new NativeCachedStream(store, identity, open, current,
            new SlotLease(slots), statistics: statistics) { Position = _position };
        var count = await stream.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
        if (!current()) throw new IOException("Media source changed during this response. Retry the range.");
        _position += count;
        return count;
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
        if (disposing && !_disposed) { _disposed = true; try { _source?.Dispose(); } finally { watch.Dispose(); } }
        base.Dispose(disposing);
    }

    private sealed class SlotLease(SemaphoreSlim slots) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) slots.Release(); }
    }
}
