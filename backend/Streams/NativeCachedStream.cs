using System.Buffers;
using NzbWebDAV.Services.NativeCache;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

/// <summary>Final-byte, lazy-source read-through stream. Never interprets sparse holes as data.</summary>
public sealed class NativeCachedStream : FastReadOnlyStream, ICacheReadEvidence
{
    private readonly NativeCacheStore _store;
    private readonly NativeCacheIdentity _identity;
    private readonly Func<CancellationToken, Task<Stream>> _openSource;
    private readonly Func<bool> _generationIsCurrent;
    private readonly IDisposable _lease;
    private readonly IDisposable? _bufferAdmission;
    private Stream? _source;
    private byte[]? _buffer;
    private long _bufferStart = -1;
    private int _bufferCount;
    private bool _bufferVerified;
    private long _position;
    private bool _disposed;

    public NativeCachedStream(NativeCacheStore store, NativeCacheIdentity identity,
        Func<CancellationToken, Task<Stream>> openSource, Func<bool> generationIsCurrent,
        IDisposable? bufferAdmission = null)
    {
        _store = store;
        _identity = identity;
        _openSource = openSource;
        _generationIsCurrent = generationIsCurrent;
        _lease = store.AcquireLease(identity);
        _bufferAdmission = bufferAdmission;
    }

    public bool LastReadCacheable { get; private set; }
    public override long Length => _identity.Length;
    public override bool CanSeek => true;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
    public override void Flush() { }

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        LastReadCacheable = false;
        cancellationToken.ThrowIfCancellationRequested();
        if (destination.IsEmpty || _position == Length) return 0;
        var blockStart = _position / NativeCacheStore.BlockSize * NativeCacheStore.BlockSize;
        if (_bufferStart != blockStart)
        {
            _buffer ??= ArrayPool<byte>.Shared.Rent(NativeCacheStore.BlockSize);
            _bufferStart = -1;
            _bufferCount = 0;
            _bufferVerified = false;
            var expected = (int)Math.Min(NativeCacheStore.BlockSize, Length - blockStart);
            if (_generationIsCurrent())
            {
                try
                {
                    _bufferCount = await _store.ReadBlockAsync(_identity, blockStart,
                        _buffer.AsMemory(0, expected), cancellationToken).ConfigureAwait(false);
                    _bufferVerified = _bufferCount == expected;
                }
                catch (IOException) { /* Cache storage failures never prevent source playback. */ }
            }
            if (_bufferCount != expected)
            {
                using var verifiedRead = new NativeCacheReadContext();
                _source ??= await _openSource(cancellationToken).ConfigureAwait(false);
                _source.Position = blockStart;
                _bufferCount = 0;
                _bufferVerified = true;
                while (_bufferCount < expected)
                {
                    var read = await _source.ReadAsync(_buffer.AsMemory(_bufferCount, expected - _bufferCount), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0) throw new EndOfStreamException("Source ended before the declared media length.");
                    _bufferVerified &= _source is ICacheReadEvidence { LastReadCacheable: true };
                    _bufferCount += read;
                }
                if (_bufferVerified && _generationIsCurrent())
                {
                    try
                    {
                        await _store.WriteBlockAsync(_identity, blockStart, _buffer.AsMemory(0, expected), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            _bufferStart = blockStart;
        }
        var bufferOffset = checked((int)(_position - _bufferStart));
        var count = Math.Min(destination.Length, _bufferCount - bufferOffset);
        _buffer!.AsMemory(bufferOffset, count).CopyTo(destination);
        _position += count;
        LastReadCacheable = _bufferVerified && _generationIsCurrent();
        return count;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (position < 0 || position > Length) throw new ArgumentOutOfRangeException(nameof(offset));
        LastReadCacheable = false;
        return _position = position;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try { _source?.Dispose(); }
            finally { Release(); }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_source is not null) await _source.DisposeAsync().ConfigureAwait(false); }
        finally { Release(); }
        GC.SuppressFinalize(this);
    }

    private void Release()
    {
        if (_buffer is not null) ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = null;
        _lease.Dispose();
        _bufferAdmission?.Dispose();
    }
}
