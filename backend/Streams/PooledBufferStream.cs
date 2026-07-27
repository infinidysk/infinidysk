using System.Buffers;

namespace NzbWebDAV.Streams;

/// <summary>
/// Seekable read/write stream over an <see cref="ArrayPool{T}"/>-rented array.
/// Logical <see cref="Length"/> is independent of rented capacity so lease accounting
/// and segment alignment stay on decoded byte counts, not pool bucket sizes.
/// </summary>
public sealed class PooledBufferStream : Stream
{
    private byte[]? _buffer;
    private int _length;
    private int _position;
    private bool _disposed;

    public PooledBufferStream(int capacityHint)
    {
        if (capacityHint < 0)
            throw new ArgumentOutOfRangeException(nameof(capacityHint));

        _buffer = capacityHint > 0
            ? ArrayPool<byte>.Shared.Rent(capacityHint)
            : [];
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => !_disposed;
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
            if (value > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(value));
            _position = (int)value;
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
        var available = _length - _position;
        if (available <= 0) return 0;

        var toRead = Math.Min(buffer.Length, available);
        _buffer.AsSpan(_position, toRead).CopyTo(buffer);
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

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ThrowIfDisposed();
        if (buffer.IsEmpty) return;

        var end = checked(_position + buffer.Length);
        EnsureCapacity(end);
        if (_position > _length)
            _buffer.AsSpan(_length, _position - _length).Clear();
        if (end > _length)
            _length = end;

        buffer.CopyTo(_buffer.AsSpan(_position));
        _position = end;
    }

    public override Task WriteAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
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

        if (next < 0 || next > int.MaxValue)
            throw new IOException("An attempt was made to move the position before the beginning of the stream.");

        _position = (int)next;
        return _position;
    }

    public override void SetLength(long value)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        if (value > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value));

        var newLength = (int)value;
        if (newLength > _length)
        {
            EnsureCapacity(newLength);
            // Rented arrays are dirty; MemoryStream would zero here and AlignDrainedSegment
            // depends on that guarantee when padding short bodies.
            _buffer.AsSpan(_length, newLength - _length).Clear();
        }

        _length = newLength;
        if (_position > _length)
            _position = _length;
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;
        ReturnBuffer();
        _disposed = true;
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        ReturnBuffer();
        _disposed = true;
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private void EnsureCapacity(int required)
    {
        var current = _buffer!;
        if (required <= current.Length) return;

        // Rent exactly what is needed so a small overshoot of a good hint does not
        // jump the Shared pool's 1 MiB bucket and lose pooling.
        var next = ArrayPool<byte>.Shared.Rent(required);
        if (_length > 0)
            current.AsSpan(0, _length).CopyTo(next);

        if (current.Length > 0)
            ArrayPool<byte>.Shared.Return(current);

        _buffer = next;
    }

    private void ReturnBuffer()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is { Length: > 0 })
            ArrayPool<byte>.Shared.Return(buffer);

        _length = 0;
        _position = 0;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed || _buffer is null, this);
    }
}
