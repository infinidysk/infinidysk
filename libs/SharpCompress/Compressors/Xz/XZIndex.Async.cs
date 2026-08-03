using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.IO;

namespace SharpCompress.Compressors.Xz;

public partial class XZIndex
{
    public static async ValueTask<XZIndex> FromStreamAsync(
        Stream stream,
        bool indexMarkerAlreadyVerified,
        CancellationToken cancellationToken = default
    )
    {
        var index = new XZIndex(
            new BinaryReader(stream, Encoding.UTF8, true),
            indexMarkerAlreadyVerified
        );
        await index.ProcessAsync(cancellationToken).ConfigureAwait(false);
        return index;
    }

    public async ValueTask ProcessAsync(CancellationToken cancellationToken = default)
    {
        using var crcStream = new Crc32TrackingStream(_reader.BaseStream);
        if (_indexMarkerAlreadyVerified)
        {
            // Index indicator byte was already consumed; include it in the CRC.
            Span<byte> indicator = stackalloc byte[1];
            indicator[0] = 0;
            crcStream.Update(indicator);
        }

        using var reader = new BinaryReader(crcStream, Encoding.UTF8, leaveOpen: true);

        if (!_indexMarkerAlreadyVerified)
        {
            await VerifyIndexMarkerAsync(reader, cancellationToken).ConfigureAwait(false);
        }

        NumberOfRecords = await reader
            .ReadXZIntegerAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        for (ulong i = 0; i < NumberOfRecords; i++)
        {
            Records.Add(
                await XZIndexRecord
                    .FromBinaryReaderAsync(reader, cancellationToken)
                    .ConfigureAwait(false)
            );
        }
        await SkipPaddingAsync(reader, cancellationToken).ConfigureAwait(false);
        await VerifyCrc32Async(crcStream, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask VerifyIndexMarkerAsync(
        BinaryReader reader,
        CancellationToken cancellationToken = default
    )
    {
        var marker = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
        if (marker != 0)
        {
            throw new InvalidFormatException("Not an index block");
        }
    }

    private async ValueTask SkipPaddingAsync(
        BinaryReader reader,
        CancellationToken cancellationToken = default
    )
    {
        var bytes = (int)(reader.BaseStream.Position - StreamStartPosition) % 4;
        if (bytes > 0)
        {
            var paddingBytes = await reader
                .ReadBytesAsync(4 - bytes, cancellationToken)
                .ConfigureAwait(false);
            if (paddingBytes.Any(b => b != 0))
            {
                throw new InvalidFormatException("Padding bytes were non-null");
            }
        }
    }

    private async ValueTask VerifyCrc32Async(
        Crc32TrackingStream crcStream,
        CancellationToken cancellationToken = default
    )
    {
        var expected = crcStream.FinalCrc;
        // Read the stored CRC from the underlying stream so it is not included in the hash.
        var crc = await crcStream
            .WrappedStream.ReadLittleEndianUInt32Async(cancellationToken)
            .ConfigureAwait(false);
        if (crc != expected)
        {
            throw new InvalidFormatException("Index corrupt");
        }

        IndexSize = crcStream.WrappedStream.Position - StreamStartPosition;
    }
}
