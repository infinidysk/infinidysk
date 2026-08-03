using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SharpCompress.IO;

internal partial class ReadOnlySubStream
{
    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        if (BytesLeftToRead < count)
        {
            count = (int)BytesLeftToRead;
        }
        var read = await _stream
            .ReadAsync(buffer.AsMemory(offset, count), cancellationToken)
            .ConfigureAwait(false);
        if (read > 0)
        {
            BytesLeftToRead -= read;
            _position += read;
        }
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        var sliceLen = BytesLeftToRead < buffer.Length ? BytesLeftToRead : buffer.Length;
        var read = await _stream
            .ReadAsync(buffer.Slice(0, (int)sliceLen), cancellationToken)
            .ConfigureAwait(false);
        if (read > 0)
        {
            BytesLeftToRead -= read;
            _position += read;
        }
        return read;
    }

    internal async ValueTask SkipRemainingAsync(CancellationToken cancellationToken = default)
    {
        if (BytesLeftToRead <= 0)
        {
            return;
        }

        await _stream.SkipAsync(BytesLeftToRead, cancellationToken).ConfigureAwait(false);
        _position += BytesLeftToRead;
        BytesLeftToRead = 0;
    }
}
