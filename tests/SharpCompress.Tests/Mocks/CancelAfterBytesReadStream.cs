using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SharpCompress.Test.Mocks;

/// <summary>
/// Cancels a <see cref="CancellationTokenSource"/> after the underlying stream has
/// asynchronously read more than a configured number of bytes.
/// Sync <see cref="Read"/> is unsupported so callers exercise async cancellation paths.
/// </summary>
public sealed class CancelAfterBytesReadStream : Stream
{
    private readonly Stream _stream;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly long _cancelAfterBytes;
    private readonly bool _leaveOpen;
    private long _bytesRead;
    private bool _isDisposed;

    public CancelAfterBytesReadStream(
        Stream stream,
        CancellationTokenSource cancellationTokenSource,
        long cancelAfterBytes,
        bool leaveOpen = false
    )
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _cancellationTokenSource =
            cancellationTokenSource
            ?? throw new ArgumentNullException(nameof(cancellationTokenSource));
        ArgumentOutOfRangeException.ThrowIfNegative(cancelAfterBytes);

        _cancelAfterBytes = cancelAfterBytes;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => !_isDisposed && _stream.CanRead;
    public override bool CanSeek => !_isDisposed && _stream.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _stream.Length;

    public override long Position
    {
        get => _stream.Position;
        set => _stream.Position = value;
    }

    public override void Flush() => _stream.Flush();

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Use async reads for this test stream.");

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var read = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _bytesRead += read;
        if (_bytesRead > _cancelAfterBytes)
        {
            _cancellationTokenSource.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }

        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => _stream.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing && !_leaveOpen)
            {
                _stream.Dispose();
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
                await _stream.DisposeAsync().ConfigureAwait(false);
            }

            _isDisposed = true;
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(CancelAfterBytesReadStream));
        }
    }
}
