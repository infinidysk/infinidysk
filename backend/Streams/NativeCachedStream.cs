using System.Buffers;
using Microsoft.Data.Sqlite;
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
    private bool _bufferFromCache;
    private long _position;
    private bool _disposed;
    private bool _bypassFill;

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
    public NativeCacheIdentity Identity => _identity;
    public bool IsSourceCurrent => _generationIsCurrent();
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
        if (_bypassFill) return await ReadSourceRangeAsync(destination, cancellationToken).ConfigureAwait(false);
        var blockStart = _position / NativeCacheStore.BlockSize * NativeCacheStore.BlockSize;
        if (_bufferFromCache && !_generationIsCurrent()) _bufferStart = -1;
        if (_bufferStart != blockStart)
        {
            using var fill = await _store.AcquireFillAsync(_identity, blockStart, cancellationToken).ConfigureAwait(false);
            _buffer ??= ArrayPool<byte>.Shared.Rent(NativeCacheStore.BlockSize);
            _bufferStart = -1;
            _bufferCount = 0;
            _bufferVerified = false;
            _bufferFromCache = false;
            var expected = (int)Math.Min(NativeCacheStore.BlockSize, Length - blockStart);
            if (_generationIsCurrent())
            {
                try
                {
                    _bufferCount = await _store.ReadBlockAsync(_identity, blockStart,
                        _buffer.AsMemory(0, expected), cancellationToken).ConfigureAwait(false);
                    _bufferVerified = _bufferCount == expected;
                    _bufferFromCache = _bufferVerified;
                    if (!_generationIsCurrent()) _bufferCount = 0;
                }
                catch (Exception exception) when (exception is IOException or SqliteException or UnauthorizedAccessException)
                { /* Cache storage failures never prevent source playback. */ }
            }
            if (_bufferCount != expected)
            {
                try
                {
                using var verifiedRead = new NativeCacheReadContext();
                _bufferFromCache = false;
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
                    catch (Exception exception) when (exception is IOException or SqliteException or UnauthorizedAccessException) { }
                }
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
                {
                    // Read-ahead is speculative. A later missing article must not
                    // break a readable prefix/range requested by the player. Reopen
                    // without native proof/read-ahead context and use ordinary reads.
                    if (_source is not null)
                    {
                        try { await _source.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception teardown) when (teardown is not OutOfMemoryException) { }
                    }
                    _source = null;
                    _bufferCount = 0;
                    _bufferVerified = false;
                    _bypassFill = true;
                    return await ReadSourceRangeAsync(destination, cancellationToken).ConfigureAwait(false);
                }
            }
            _bufferStart = blockStart;
        }
        if (_bufferFromCache && !_generationIsCurrent())
        {
            _bufferStart = -1;
            return await ReadAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        var bufferOffset = checked((int)(_position - _bufferStart));
        var count = Math.Min(destination.Length, _bufferCount - bufferOffset);
        _buffer!.AsMemory(bufferOffset, count).CopyTo(destination);
        _position += count;
        LastReadCacheable = _bufferVerified && _generationIsCurrent();
        return count;
    }

    private async ValueTask<int> ReadSourceRangeAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        _source ??= await _openSource(cancellationToken).ConfigureAwait(false);
        if (_source.Position != _position) _source.Position = _position;
        var count = await _source.ReadAsync(destination[..(int)Math.Min(destination.Length, Length - _position)], cancellationToken).ConfigureAwait(false);
        _position += count;
        LastReadCacheable = false;
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
