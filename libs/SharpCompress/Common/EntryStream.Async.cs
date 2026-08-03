using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common.Tar;
using SharpCompress.IO;

namespace SharpCompress.Common;

public partial class EntryStream
{
    /// <summary>
    /// Asynchronously skip the rest of the entry stream.
    /// </summary>
    public async ValueTask SkipEntryAsync(CancellationToken cancellationToken = default)
    {
        if (_stream is TarReadOnlySubStream tarSubStream)
        {
            await tarSubStream.SkipRemainingAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (_stream is ReadOnlySubStream subStream)
        {
            await subStream.SkipRemainingAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await this.SkipAsync(cancellationToken).ConfigureAwait(false);
        }
        _completed = true;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }
        _isDisposed = true;
        if (!(_completed || _reader.Cancelled))
        {
            if (_cancelOnDispose)
            {
                _reader.Cancel();
                if (_stream is TarReadOnlySubStream tarStream)
                {
                    tarStream.AbandonWithoutAdvance();
                }
            }
            else
            {
                await SkipEntryAsync().ConfigureAwait(false);
            }
        }

        // Some decompressors read more of the underlying stream than the entry required.
        // ReturnOverread only rewinds an in-memory buffer, so the sync call is used here and
        // it walks the whole stack (the previous code only inspected one level via BaseStream()).
        if (!_reader.Cancelled && _stream is IStreamStack ss)
        {
            ss.FindImplementing<IOverreadingStream>()?.ReturnOverread();
        }
        await base.DisposeAsync().ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
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
        if (read <= 0 && count > 0)
        {
            _completed = true;
        }
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        var read = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read <= 0 && buffer.Length > 0)
        {
            _completed = true;
        }
        return read;
    }
}
