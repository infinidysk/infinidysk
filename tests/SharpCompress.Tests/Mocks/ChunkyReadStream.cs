using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SharpCompress.Test.Mocks;

/// <summary>
/// A stream wrapper that returns at most a configured number of bytes per read.
/// Simulates network/multipart streams that legally return fewer bytes than requested.
/// </summary>
public sealed class ChunkyReadStream : Stream
{
    private readonly Stream _inner;
    private readonly int _chunkSize;
    private readonly int _firstReadSize;
    private readonly bool _leaveOpen;
    private bool _firstReadDone;
    private bool _isDisposed;

    public ChunkyReadStream(
        Stream inner,
        int chunkSize,
        int? firstReadSize = null,
        bool leaveOpen = false
    )
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (chunkSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "Chunk size must be >= 1.");
        }

        _chunkSize = chunkSize;
        _firstReadSize = firstReadSize ?? chunkSize;
        if (_firstReadSize < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(firstReadSize),
                "First read size must be >= 1."
            );
        }

        _leaveOpen = leaveOpen;
    }

    /// <summary>
    /// Creates a non-seekable chunky stream over a byte array (matches ZIP short-read patterns).
    /// </summary>
    public static ChunkyReadStream FromBytes(
        byte[] bytes,
        int chunkSize,
        int? firstReadSize = null
    ) =>
        new(new MemoryStream(bytes, writable: false), chunkSize, firstReadSize, leaveOpen: false)
        {
            ForceNonSeekable = true,
        };

    /// <summary>
    /// When true, reports <see cref="CanSeek"/> as false even if the inner stream seeks.
    /// </summary>
    public bool ForceNonSeekable { get; init; }

    public override bool CanRead => !_isDisposed && _inner.CanRead;
    public override bool CanSeek => !ForceNonSeekable && !_isDisposed && _inner.CanSeek;
    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            if (ForceNonSeekable || !_inner.CanSeek)
            {
                throw new NotSupportedException();
            }

            return _inner.Length;
        }
    }

    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            if (ForceNonSeekable || !_inner.CanSeek)
            {
                throw new NotSupportedException();
            }

            return _inner.Position;
        }
        set
        {
            ThrowIfDisposed();
            if (ForceNonSeekable || !_inner.CanSeek)
            {
                throw new NotSupportedException();
            }

            _inner.Position = value;
        }
    }

    public override void Flush()
    {
        ThrowIfDisposed();
        _inner.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ThrowIfDisposed();
        var limit = NextReadLimit();
        var toRead = Math.Min(count, limit);
        return _inner.Read(buffer, offset, toRead);
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var limit = NextReadLimit();
        var sliced = buffer[..Math.Min(buffer.Length, limit)];
        return await _inner.ReadAsync(sliced, cancellationToken).ConfigureAwait(false);
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        if (ForceNonSeekable || !_inner.CanSeek)
        {
            throw new NotSupportedException();
        }

        return _inner.Seek(offset, origin);
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing && !_leaveOpen)
            {
                _inner.Dispose();
            }

            _isDisposed = true;
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_isDisposed)
        {
            if (!_leaveOpen)
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }

            _isDisposed = true;
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    private int NextReadLimit()
    {
        var limit = !_firstReadDone ? _firstReadSize : _chunkSize;
        _firstReadDone = true;
        return limit;
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(ChunkyReadStream));
        }
    }
}
