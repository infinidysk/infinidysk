using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SharpCompress.Compressors.Rar;

internal sealed partial class StoredRarEntryStream
{
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.Length == 0)
        {
            return 0;
        }

        if (_position >= _length)
        {
            ValidateCrcAtEof(requestedCount: buffer.Length);
            return 0;
        }

        var totalRead = 0;
        while (totalRead < buffer.Length && _position < _length)
        {
            var partIndex = FindPartIndex(_position);
            var part = _parts[partIndex];
            var intraPart = _position - _cumOffsets[partIndex];
            var remainingInPart = part.FileHeader.CompressedSize - intraPart;
            var remainingInEntry = _length - _position;
            var toRead = (int)
                Math.Min(buffer.Length - totalRead, Math.Min(remainingInPart, remainingInEntry));

            var volume = part.VolumeStream;
            volume.Position = part.FileHeader.DataStartPosition + intraPart;
            var read = await volume
                .ReadAsync(buffer.Slice(totalRead, toRead), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new Common.IncompleteArchiveException(
                    "Unexpected end of volume while reading stored RAR entry data."
                );
            }

            if (!_crcDisabled)
            {
                _currentCrc = RarCRC.CheckCrc(
                    _currentCrc,
                    buffer.Span.Slice(totalRead, read),
                    0,
                    read
                );
            }

            _position += read;
            totalRead += read;
        }

        if (totalRead == 0)
        {
            ValidateCrcAtEof(requestedCount: buffer.Length);
        }

        return totalRead;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        ValidateBufferArgs(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
