using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Common.Rar.Headers;
using SharpCompress.IO;

namespace SharpCompress.Compressors.Rar;

internal partial class RarStream
{
    /// <summary>
    /// Asynchronously initializes the RAR stream for reading.
    /// </summary>
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (initialized)
        {
            return;
        }

        fetch = true;
        await unpack
            .DoUnpackAsync(fileHeader, readStream, this, cancellationToken)
            .ConfigureAwait(false);
        fetch = false;
        initialized = true;
        _position = 0;
    }

    /// <summary>
    /// Asynchronously reads bytes from the current stream into a buffer.
    /// </summary>
    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => ReadImplAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>
    /// Asynchronously reads bytes from the current stream into a memory buffer.
    /// </summary>
    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    ) => ReadImplAsync(buffer, cancellationToken);

    /// <summary>
    /// Internal async implementation of ReadAsync.
    /// </summary>
    private async ValueTask<int> ReadImplAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        outTotal = 0;
        if (tmpCount > 0)
        {
            var toCopy = tmpCount < buffer.Length ? tmpCount : buffer.Length;
            tmpBuffer.AsSpan(tmpOffset, toCopy).CopyTo(buffer.Span);
            tmpOffset += toCopy;
            tmpCount -= toCopy;
            buffer = buffer.Slice(toCopy);
            outTotal += toCopy;
        }
        if (buffer.Length > 0 && unpack.DestSize > 0)
        {
            outBuffer = buffer;
            fetch = true;
            await unpack.DoUnpackAsync(cancellationToken).ConfigureAwait(false);
            fetch = false;
        }
        _position += outTotal;
        if (buffer.Length > 0 && outTotal == 0 && _position < Length)
        {
            // sanity check, eg if we try to decompress a redir entry
            throw new ArchiveOperationException(
                $"unpacked file size does not match header: expected {Length} found {_position}"
            );
        }
        return outTotal;
    }

    /// <summary>
    /// Asynchronously writes bytes to the current stream.
    /// </summary>
    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }
}
