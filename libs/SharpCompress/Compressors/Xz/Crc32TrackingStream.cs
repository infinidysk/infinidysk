using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SharpCompress.Compressors.Xz;

/// <summary>
/// Read-only stream wrapper that accumulates a CRC32 over bytes read from an underlying stream.
/// Does not take ownership of the wrapped stream.
/// </summary>
internal sealed class Crc32TrackingStream : Stream
{
    private readonly Stream _stream;
    private uint _crc = Crc32.DefaultSeed;

    public Crc32TrackingStream(Stream stream) =>
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));

    internal Stream WrappedStream => _stream;

    /// <summary>
    /// Current CRC32 hash state (not yet finalized with the final XOR).
    /// </summary>
    internal uint Crc => _crc;

    /// <summary>
    /// Finalized CRC32 value matching <see cref="Crc32.Compute"/> over all tracked bytes.
    /// </summary>
    internal uint FinalCrc => ~_crc;

    /// <summary>
    /// Feeds bytes into the CRC without reading from the underlying stream.
    /// Used when the index indicator byte was already consumed before wrapping.
    /// </summary>
    internal void Update(ReadOnlySpan<byte> bytes) => _crc = Crc32.Update(_crc, bytes);

    public override bool CanRead => _stream.CanRead;

    public override bool CanSeek => _stream.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _stream.Length;

    public override long Position
    {
        get => _stream.Position;
        set => _stream.Position = value;
    }

    public override void Flush() => _stream.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _stream.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _stream.Read(buffer, offset, count);
        if (read > 0)
        {
            _crc = Crc32.Update(_crc, buffer.AsSpan(offset, read));
        }

        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = _stream.Read(buffer);
        if (read > 0)
        {
            _crc = Crc32.Update(_crc, buffer.Slice(0, read));
        }

        return read;
    }

    public override int ReadByte()
    {
        var value = _stream.ReadByte();
        if (value != -1)
        {
            Span<byte> b = stackalloc byte[1];
            b[0] = (byte)value;
            _crc = Crc32.Update(_crc, b);
        }

        return value;
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
        if (read > 0)
        {
            _crc = Crc32.Update(_crc, buffer.AsSpan(offset, read));
        }

        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        var read = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            _crc = Crc32.Update(_crc, buffer.Span.Slice(0, read));
        }

        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => _stream.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // Do not dispose the wrapped stream; callers retain ownership.
        base.Dispose(disposing);
    }
}
