using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;

namespace SharpCompress.IO;

public partial class SharpCompressStream
{
    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        if (count == 0)
        {
            return 0;
        }

        // If ring buffer is enabled, use ring buffer logic
        if (_ringBuffer is not null)
        {
            return await ReadWithRingBufferAsync(buffer, offset, count, cancellationToken)
                .ConfigureAwait(false);
        }

        // No buffering - read directly from stream
        int read = await stream
            .ReadAsync(buffer.AsMemory(offset, count), cancellationToken)
            .ConfigureAwait(false);
        streamPosition += read;
        _logicalPosition = streamPosition;
        return read;
    }

    /// <summary>
    /// Async version of ReadWithRingBuffer.
    /// </summary>
    private async ValueTask<int> ReadWithRingBufferAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        int totalRead = 0;

        // If logical position is behind stream position, read from ring buffer first
        while (count > 0 && _logicalPosition < streamPosition)
        {
            long bytesFromEnd = streamPosition - _logicalPosition;

            // Verify data is available in ring buffer
            if (!_ringBuffer!.CanReadFromEnd(bytesFromEnd))
            {
                throw new ArchiveOperationException(
                    $"Ring buffer underflow: trying to read {bytesFromEnd} bytes back, "
                        + $"but buffer only holds {_ringBuffer.Length} bytes."
                );
            }

            int available = _ringBuffer.ReadFromEnd(bytesFromEnd, buffer, offset, count);
            totalRead += available;
            offset += available;
            count -= available;
            _logicalPosition += available;
        }

        TryReleaseRingBuffer();

        // If more data needed and we're caught up, read from underlying stream
        if (count > 0 && _logicalPosition == streamPosition)
        {
            int read = await stream
                .ReadAsync(buffer.AsMemory(offset, count), cancellationToken)
                .ConfigureAwait(false);
            if (read > 0)
            {
                _ringBuffer?.Write(buffer, offset, read);
                streamPosition += read;
                _logicalPosition += read;
                totalRead += read;
            }
        }

        return totalRead;
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        if (buffer.Length == 0)
        {
            return ValueTask.FromResult(0);
        }

        return ReadAsyncCore(buffer, cancellationToken);
    }

    private async ValueTask<int> ReadAsyncCore(
        Memory<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        // If ring buffer is enabled, use ring buffer logic
        if (_ringBuffer is not null)
        {
            return await ReadWithRingBufferAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        // No buffering - read directly from stream
        int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        streamPosition += read;
        _logicalPosition = streamPosition;
        return read;
    }

    /// <summary>
    /// Async version of ReadWithRingBuffer for Memory&lt;byte&gt;.
    /// </summary>
    private async ValueTask<int> ReadWithRingBufferAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        int totalRead = 0;
        int count = buffer.Length;
        int offset = 0;

        // If logical position is behind stream position, read from ring buffer first
        while (count > 0 && _logicalPosition < streamPosition)
        {
            long bytesFromEnd = streamPosition - _logicalPosition;

            // Verify data is available in ring buffer
            if (!_ringBuffer!.CanReadFromEnd(bytesFromEnd))
            {
                throw new ArchiveOperationException(
                    $"Ring buffer underflow: trying to read {bytesFromEnd} bytes back, "
                        + $"but buffer only holds {_ringBuffer.Length} bytes."
                );
            }

            int available = _ringBuffer.ReadFromEnd(bytesFromEnd, buffer.Span.Slice(offset, count));

            totalRead += available;
            offset += available;
            count -= available;
            _logicalPosition += available;
        }

        TryReleaseRingBuffer();

        // If more data needed and we're caught up, read from underlying stream
        if (count > 0 && _logicalPosition == streamPosition)
        {
            int read = await stream
                .ReadAsync(buffer.Slice(offset, count), cancellationToken)
                .ConfigureAwait(false);
            if (read > 0)
            {
                // Span must be re-acquired after await (cannot cross await boundary).
                _ringBuffer?.Write(buffer.Span.Slice(offset, read));
                streamPosition += read;
                _logicalPosition += read;
                totalRead += read;
            }
        }

        return totalRead;
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => throw new NotSupportedException();

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public override async Task CopyToAsync(
        Stream destination,
        int bufferSize,
        CancellationToken cancellationToken
    )
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            int bytesRead;
            while (
                (
                    bytesRead = await ReadAsync(buffer, 0, bufferSize, cancellationToken)
                        .ConfigureAwait(false)
                ) != 0
            )
            {
                await destination
                    .WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (!isDisposed)
        {
            isDisposed = true;
            if (!LeaveStreamOpen)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            _ringBuffer?.Dispose();
            _ringBuffer = null;
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
