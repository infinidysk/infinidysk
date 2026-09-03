using Serilog;

namespace NzbWebDAV.Streams;

/// <summary>
/// Seekable read/write stream over an <see cref="ISegmentBufferPool"/>-rented array.
/// Logical <see cref="Length"/> is independent of rented capacity so lease accounting
/// and segment alignment stay on decoded byte counts, not pool bucket sizes.
/// </summary>
public sealed class PooledBufferStream : Stream
{
    private const int RunawayThresholdBytes = 32 * 1024 * 1024;

    /// <summary>
    /// Pool used when no explicit pool is passed. Set once by the composition root
    /// before the server handles traffic and never mutated afterwards; each stream
    /// captures the reference at construction, so buffers always return to the pool
    /// that rented them (per-request pool mutation was rejected in PR #776 review).
    /// </summary>
    internal static ISegmentBufferPool DefaultPool { get; set; } = SharedArrayPoolAdapter.Instance;

    internal static int EstimateDefaultRentedCapacity(int minimumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumLength);
        if (DefaultPool is SegmentBufferPool)
            return SegmentBufferPool.RoundToSizeClass(minimumLength);

        // ArrayPool.Shared uses power-of-two buckets. This is intentionally
        // conservative for the supported shared-pool rollback path.
        var estimate = 16L;
        while (estimate < minimumLength && estimate <= Array.MaxLength / 2L)
            estimate *= 2;
        return estimate >= minimumLength && estimate <= Array.MaxLength
            ? (int)estimate
            : minimumLength;
    }

    private readonly ISegmentBufferPool _pool;
    private readonly BufferPoolDiagnostics _diagnostics;
    private byte[]? _buffer;
    private int _length;
    private int _position;
    private bool _disposed;

    public PooledBufferStream(
        int capacityHint,
        ISegmentBufferPool? pool = null,
        BufferPoolDiagnostics? diagnostics = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacityHint);

        _pool = pool ?? DefaultPool;
        _diagnostics = diagnostics ?? BufferPoolDiagnostics.Shared;

        if (capacityHint > 0)
        {
            WarnIfRunawayCapacity(capacityHint, 0);
            _buffer = RentBuffer(capacityHint);
            _diagnostics.RecordRent(capacityHint, _buffer.Length);
        }
        else
        {
            _buffer = [];
        }
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => !_disposed;
    internal ReadOnlyMemory<byte> WrittenMemory
    {
        get
        {
            ThrowIfDisposed();
            return _buffer.AsMemory(0, _length);
        }
    }
    internal int RentedCapacity
    {
        get
        {
            ThrowIfDisposed();
            return _buffer!.Length;
        }
    }
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
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, int.MaxValue);
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
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, int.MaxValue);

        var newLength = (int)value;
        if (newLength > _length)
        {
            EnsureCapacity(newLength);
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

    public override async ValueTask DisposeAsync()
    {
        ReturnBuffer();
        _disposed = true;
        GC.SuppressFinalize(this);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void EnsureCapacity(int required)
    {
        var current = _buffer!;
        if (required <= current.Length) return;

        WarnIfRunawayCapacity(required, current.Length);
        var target = (int)Math.Min(
            Math.Max((long)required, current.Length + (long)current.Length / 2),
            Array.MaxLength);
        var next = RentBuffer(target);
        if (_length > 0)
            current.AsSpan(0, _length).CopyTo(next);

        if (current.Length > 0)
            _pool.Return(current);

        _diagnostics.RecordGrowth(required, current.Length, next.Length);
        _buffer = next;
    }

    private static void WarnIfRunawayCapacity(int required, int previous)
    {
        if (required <= RunawayThresholdBytes || previous > RunawayThresholdBytes) return;

        Log.Warning(
            "Segment buffer allocation or growth exceeded {ThresholdMB} MB. Required={Required:N0} Previous={Previous:N0}. " +
            "The segment read may not be terminating.",
            RunawayThresholdBytes / (1024 * 1024),
            required,
            previous);
    }

    private void ReturnBuffer()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is { Length: > 0 })
        {
            _diagnostics.RecordReturn(buffer.Length);
            _pool.Return(buffer);
        }

        _length = 0;
        _position = 0;
    }

    private byte[] RentBuffer(int minimumLength)
    {
        var buffer = _pool.Rent(minimumLength);
        if (buffer.Length >= minimumLength) return buffer;

        if (buffer.Length > 0)
            _pool.Return(buffer);
        throw new InvalidOperationException(
            $"Segment buffer pool returned {buffer.Length} bytes for a {minimumLength}-byte request.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed || _buffer is null, this);
    }
}
