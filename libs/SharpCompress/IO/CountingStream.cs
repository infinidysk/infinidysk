using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SharpCompress.IO;

/// <summary>
/// A simple stream wrapper that counts bytes read and written without buffering.
/// </summary>
internal class CountingStream : Stream
{
    private readonly Stream _stream;
    private long _bytesRead;
    private long _bytesWritten;

    public CountingStream(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    internal Stream WrappedStream => _stream;

    /// <summary>
    /// Gets the total number of bytes read from this stream.
    /// </summary>
    public long BytesRead => _bytesRead;

    /// <summary>
    /// Gets the total number of bytes written to this stream.
    /// </summary>
    public long BytesWritten => _bytesWritten;

    public override bool CanRead => _stream.CanRead;

    public override bool CanSeek => _stream.CanSeek;

    public override bool CanWrite => _stream.CanWrite;

    public override long Length => _stream.Length;

    public override long Position
    {
        get => _stream.Position;
        set => _stream.Position = value;
    }

    public override void Flush() => _stream.Flush();

    public override async Task FlushAsync(CancellationToken cancellationToken) =>
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _stream.Read(buffer, offset, count);
        _bytesRead += read;
        return read;
    }

    public override int ReadByte()
    {
        var value = _stream.ReadByte();
        if (value != -1)
        {
            _bytesRead++;
        }

        return value;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = _stream.Read(buffer);
        _bytesRead += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => _stream.Seek(offset, origin);

    public override void SetLength(long value) => _stream.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count)
    {
        _stream.Write(buffer, offset, count);
        _bytesWritten += count;
    }

    public override void WriteByte(byte value)
    {
        _stream.WriteByte(value);
        _bytesWritten++;
    }

    public override async Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        await _stream
            .WriteAsync(buffer.AsMemory(offset, count), cancellationToken)
            .ConfigureAwait(false);
        _bytesWritten += count;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        var read = await _stream
            .ReadAsync(buffer.AsMemory(offset, count), cancellationToken)
            .ConfigureAwait(false);
        _bytesRead += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        var read = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _bytesRead += read;
        return read;
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        await _stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        _bytesWritten += buffer.Length;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stream.Dispose();
        }
        base.Dispose(disposing);
    }
}
