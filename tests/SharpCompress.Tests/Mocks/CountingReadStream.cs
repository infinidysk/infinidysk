using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SharpCompress.Test.Mocks;

/// <summary>
/// Counts bytes read from an underlying stream for dispose/drain assertions.
/// </summary>
public sealed class CountingReadStream : Stream
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private bool _isDisposed;

    public CountingReadStream(Stream stream, bool leaveOpen = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _leaveOpen = leaveOpen;
    }

    public long BytesRead { get; private set; }

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

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _stream.Read(buffer, offset, count);
        BytesRead += read;
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = _stream.Read(buffer);
        BytesRead += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        var read = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        BytesRead += read;
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
            _isDisposed = true;
            if (disposing && !_leaveOpen)
            {
                _stream.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            if (!_leaveOpen)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
