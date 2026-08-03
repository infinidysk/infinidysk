using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SharpCompress.Test.Mocks;

/// <summary>
/// A stream wrapper that truncates the underlying stream after reading a specified number of bytes.
/// Used for testing error handling when streams end prematurely.
/// </summary>
public class TruncatedStream : Stream
{
    private readonly Stream _baseStream;
    private readonly long _truncateAfterBytes;
    private readonly bool _leaveOpen;
    private long _bytesRead;
    private bool _isDisposed;

    public TruncatedStream(Stream baseStream, long truncateAfterBytes, bool leaveOpen = false)
    {
        _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
        ArgumentOutOfRangeException.ThrowIfNegative(truncateAfterBytes);

        _truncateAfterBytes = truncateAfterBytes;
        _leaveOpen = leaveOpen;
    }

    /// <summary>
    /// Creates a truncated stream that stops after the given percentage of the base stream length.
    /// </summary>
    public static TruncatedStream AtPercent(
        Stream baseStream,
        double percent,
        bool leaveOpen = false
    )
    {
        ArgumentNullException.ThrowIfNull(baseStream);
        if (percent is <= 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percent), "Percent must be in (0, 100].");
        }

        if (!baseStream.CanSeek)
        {
            throw new ArgumentException(
                "Percentage truncation requires a seekable base stream.",
                nameof(baseStream)
            );
        }

        var truncateAfter = (long)(baseStream.Length * (percent / 100.0));
        return new TruncatedStream(baseStream, truncateAfter, leaveOpen);
    }

    public override bool CanRead => !_isDisposed && _baseStream.CanRead;
    public override bool CanSeek => !_isDisposed && _baseStream.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _baseStream.Length;

    public override long Position
    {
        get => _baseStream.Position;
        set => _baseStream.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ThrowIfDisposed();
        if (_bytesRead >= _truncateAfterBytes)
        {
            return 0;
        }

        var maxBytesToRead = (int)Math.Min(count, _truncateAfterBytes - _bytesRead);
        var actualBytesRead = _baseStream.Read(buffer, offset, maxBytesToRead);
        _bytesRead += actualBytesRead;
        return actualBytesRead;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (_bytesRead >= _truncateAfterBytes)
        {
            return 0;
        }

        var maxBytesToRead = (int)Math.Min(buffer.Length, _truncateAfterBytes - _bytesRead);
        var actualBytesRead = await _baseStream
            .ReadAsync(buffer[..maxBytesToRead], cancellationToken)
            .ConfigureAwait(false);
        _bytesRead += actualBytesRead;
        return actualBytesRead;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => _baseStream.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override void Flush() => _baseStream.Flush();

    protected override void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing && !_leaveOpen)
            {
                _baseStream.Dispose();
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
                await _baseStream.DisposeAsync().ConfigureAwait(false);
            }

            _isDisposed = true;
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(TruncatedStream));
        }
    }
}
