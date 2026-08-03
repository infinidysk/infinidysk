using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SharpCompress.Compressors.Rar;

internal sealed partial class EncryptedStoredRarEntryStream
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
            return 0;
        }

        var totalRead = 0;
        while (totalRead < buffer.Length && _position < _length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var partIndex = FindPartIndex(_position);
            var part = _parts[partIndex];
            var intraPart = _position - _cumOffsets[partIndex];
            var remainingInPart = _cumOffsets[partIndex + 1] - _position;
            var remainingInEntry = _length - _position;
            var toRead = (int)
                Math.Min(buffer.Length - totalRead, Math.Min(remainingInPart, remainingInEntry));

            DecryptPlaintext(part, intraPart, buffer.Span.Slice(totalRead, toRead));
            _position += toRead;
            totalRead += toRead;
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
