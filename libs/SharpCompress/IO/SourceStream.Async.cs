using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SharpCompress.IO;

public partial class SourceStream
{
    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        if (count <= 0)
        {
            return 0;
        }

        var total = count;
        var r = -1;

        while (count != 0 && r != 0)
        {
            r = await Current
                .ReadAsync(
                    buffer,
                    offset,
                    (int)Math.Min(count, RemainingInCurrentPart()),
                    cancellationToken
                )
                .ConfigureAwait(false);
            count -= r;
            offset += r;

            if (!IsVolumes && count != 0 && Current.Position == _partLengths[_stream])
            {
                if (!SetStream(_stream + 1))
                {
                    break;
                }

                Current.Seek(0, SeekOrigin.Begin);
                r = -1;
            }
        }

        return total - count;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        if (buffer.Length <= 0)
        {
            return 0;
        }

        var total = buffer.Length;
        var count = buffer.Length;
        var offset = 0;
        var r = -1;

        while (count != 0 && r != 0)
        {
            r = await Current
                .ReadAsync(
                    buffer.Slice(offset, (int)Math.Min(count, RemainingInCurrentPart())),
                    cancellationToken
                )
                .ConfigureAwait(false);
            count -= r;
            offset += r;

            if (!IsVolumes && count != 0 && Current.Position == _partLengths[_stream])
            {
                if (!SetStream(_stream + 1))
                {
                    break;
                }

                Current.Seek(0, SeekOrigin.Begin);
                r = -1;
            }
        }

        return total - count;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        if (!ReaderOptions.LeaveStreamOpen)
        {
            foreach (var stream in _streams)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            _streams.Clear();
            _files.Clear();
        }

        _isDisposed = true;
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
