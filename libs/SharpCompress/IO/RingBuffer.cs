using System;
using System.Buffers;

namespace SharpCompress.IO;

/// <summary>
/// A circular buffer that keeps the last N bytes written to it.
/// Used for limited backward seeking on forward-only streams.
/// </summary>
internal sealed class RingBuffer : IDisposable
{
    private byte[]? _buffer;
    private readonly int _capacity;
    private int _writePos; // Next write position in circular buffer
    private int _length; // Number of valid bytes (0 to _capacity)
    private bool _isDisposed;

    /// <summary>
    /// Creates a new RingBuffer with the specified capacity.
    /// </summary>
    /// <param name="capacity">Maximum number of bytes to keep in the buffer.</param>
    public RingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }
        _capacity = capacity;
        _buffer = ArrayPool<byte>.Shared.Rent(capacity);
        _writePos = 0;
        _length = 0;
    }

    /// <summary>
    /// Gets the maximum capacity of the buffer.
    /// </summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Gets the number of valid bytes currently in the buffer.
    /// </summary>
    public int Length => _length;

    /// <summary>
    /// Writes data to the buffer. If the data exceeds capacity,
    /// only the last <see cref="Capacity"/> bytes are kept.
    /// </summary>
    /// <param name="data">Source data array.</param>
    /// <param name="offset">Offset in source array.</param>
    /// <param name="count">Number of bytes to write.</param>
    public void Write(byte[] data, int offset, int count) => Write(data.AsSpan(offset, count));

    /// <summary>
    /// Writes data to the buffer. If the data exceeds capacity,
    /// only the last <see cref="Capacity"/> bytes are kept.
    /// </summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();

        if (data.IsEmpty)
        {
            return;
        }

        int count = data.Length;

        // If data is larger than buffer, only keep the last _capacity bytes
        if (count >= _capacity)
        {
            data.Slice(count - _capacity).CopyTo(_buffer.AsSpan(0, _capacity));
            _writePos = 0;
            _length = _capacity;
            return;
        }

        // Write data to circular buffer (may wrap around)
        int firstPart = Math.Min(count, _capacity - _writePos);
        data.Slice(0, firstPart).CopyTo(_buffer.AsSpan(_writePos, firstPart));
        if (firstPart < count)
        {
            // Wrap around
            data.Slice(firstPart).CopyTo(_buffer.AsSpan(0, count - firstPart));
        }

        _writePos = (_writePos + count) % _capacity;
        _length = Math.Min(_length + count, _capacity);
    }

    /// <summary>
    /// Reads data from the buffer at a logical position relative to the end.
    /// </summary>
    /// <param name="bytesFromEnd">How many bytes from the end (most recent write) to start reading.</param>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="offset">Offset in destination buffer.</param>
    /// <param name="count">Maximum bytes to read.</param>
    /// <returns>Number of bytes actually read.</returns>
    /// <exception cref="ArgumentOutOfRangeException">If bytesFromEnd exceeds available data.</exception>
    public int ReadFromEnd(long bytesFromEnd, byte[] buffer, int offset, int count) =>
        ReadFromEnd(bytesFromEnd, buffer.AsSpan(offset, count));

    /// <summary>
    /// Reads data from the buffer at a logical position relative to the end.
    /// </summary>
    public int ReadFromEnd(long bytesFromEnd, Span<byte> destination)
    {
        ThrowIfDisposed();

        if (bytesFromEnd > _length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bytesFromEnd),
                $"Requested position ({bytesFromEnd} bytes from end) is outside buffer range (length={_length})."
            );
        }

        if (bytesFromEnd <= 0 || destination.IsEmpty)
        {
            return 0;
        }

        // Calculate starting index in circular buffer
        // _writePos is where next byte would be written (one past last valid byte)
        int bufferIndex = (int)((_writePos - bytesFromEnd + _capacity) % _capacity);
        int availableFromBuffer = (int)Math.Min(bytesFromEnd, destination.Length);

        // Read from rolling buffer (may wrap around)
        int firstPart = Math.Min(availableFromBuffer, _capacity - bufferIndex);
        _buffer.AsSpan(bufferIndex, firstPart).CopyTo(destination);
        if (firstPart < availableFromBuffer)
        {
            // Wrap around
            _buffer
                .AsSpan(0, availableFromBuffer - firstPart)
                .CopyTo(destination.Slice(firstPart));
        }

        return availableFromBuffer;
    }

    /// <summary>
    /// Checks if a position (as bytes from the end) is within the buffered range.
    /// </summary>
    /// <param name="bytesFromEnd">Position as bytes from end.</param>
    /// <returns>True if the position is available in the buffer.</returns>
    public bool CanReadFromEnd(long bytesFromEnd) => bytesFromEnd >= 0 && bytesFromEnd <= _length;

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            if (_buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = null;
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(RingBuffer));
        }
    }
}
