namespace NzbWebDAV.Streams;

/// <summary>
/// Emits format-native discard markers for a permanently unavailable segment.
/// The markers are intentionally dense so a demuxer that recovers partway through
/// the gap can find another valid boundary. Unsupported formats retain zero-fill.
/// </summary>
internal sealed class ContainerAwareFillStream : Stream
{
    private enum FillFormat
    {
        TransportStream188,
        TransportStream192,
    }

    private readonly long _length;
    private readonly long _fileOffset;
    private readonly FillFormat _format;
    private long _position;
    private bool _disposed;

    private ContainerAwareFillStream(long length, long fileOffset, FillFormat format)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfNegative(fileOffset);
        _length = length;
        _fileOffset = fileOffset;
        _format = format;
    }

    public static Stream Create(string? fileName, long length, long? fileOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var extension = Path.GetExtension(fileName)?.ToLowerInvariant() ?? string.Empty;
        return extension switch
        {
            ".ts" when fileOffset is >= 0 =>
                new ContainerAwareFillStream(length, fileOffset.Value, FillFormat.TransportStream188),
            ".m2ts" or ".mts" when fileOffset is >= 0 =>
                new ContainerAwareFillStream(length, fileOffset.Value, FillFormat.TransportStream192),
            _ => new ZeroStream(length),
        };
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            return _length;
        }
    }

    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            return _position;
        }
        set
        {
            ThrowIfDisposed();
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override void Flush() => ThrowIfDisposed();

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        var remaining = _length - _position;
        if (remaining <= 0 || buffer.IsEmpty) return 0;

        var toRead = (int)Math.Min(buffer.Length, remaining);
        var destination = buffer[..toRead];
        switch (_format)
        {
            case FillFormat.TransportStream188:
                FillTransportStream(destination, packetSize: 188, transportHeaderOffset: 0);
                break;
            case FillFormat.TransportStream192:
                FillTransportStream(destination, packetSize: 192, transportHeaderOffset: 4);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        _position += toRead;
        return toRead;
    }

    public override Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read(buffer, offset, count));
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<int>(Read(buffer.Span));
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        var next = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (next < 0)
            throw new IOException("An attempt was made to move the position before the beginning of the stream.");

        _position = next;
        return _position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }

    private void FillTransportStream(
        Span<byte> destination,
        int packetSize,
        int transportHeaderOffset)
    {
        destination.Clear();
        var gapEnd = checked(_fileOffset + _length);
        for (var i = 0; i < destination.Length; i++)
        {
            var absolutePosition = _fileOffset + _position + i;
            var packetStart = absolutePosition - absolutePosition % packetSize;

            // Never mark a partial packet as null: doing so could make the demuxer
            // discard valid bytes that resume after the gap.
            if (packetStart < _fileOffset || packetStart + packetSize > gapEnd)
                continue;

            var packetPosition = (int)(absolutePosition - packetStart);
            if (transportHeaderOffset == 4 && packetPosition < transportHeaderOffset)
            {
                destination[i] = 0x00; // M2TS arrival timestamp prefix
                continue;
            }

            var transportPosition = packetPosition - transportHeaderOffset;
            destination[i] = transportPosition switch
            {
                0 => 0x47,
                1 => 0x1F,
                2 => 0xFF,
                3 => 0x10,
                _ => 0xFF,
            };
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
