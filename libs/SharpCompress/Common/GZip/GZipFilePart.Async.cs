using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Compressors;
using SharpCompress.Compressors.Deflate;
using SharpCompress.Providers;

namespace SharpCompress.Common.GZip;

internal sealed partial class GZipFilePart
{
    internal static async ValueTask<GZipFilePart> CreateAsync(
        Stream stream,
        IArchiveEncoding archiveEncoding,
        CompressionProviderRegistry compressionProviders,
        CancellationToken cancellationToken = default
    )
    {
        var part = new GZipFilePart(stream, archiveEncoding, compressionProviders);

        await part.ReadAndValidateGzipHeaderAsync(cancellationToken).ConfigureAwait(false);
        if (stream.CanSeek)
        {
            var position = stream.Position;
            stream.Position = stream.Length - 8;
            await part.ReadTrailerAsync(cancellationToken).ConfigureAwait(false);
            stream.Position = position;
            part.EntryStartPosition = position;
        }
        else
        {
            // For non-seekable streams, we can't read the trailer or track position.
            // Set to 0 since the stream will be read sequentially from its current position.
            part.EntryStartPosition = 0;
        }
        return part;
    }

    private async ValueTask ReadTrailerAsync(CancellationToken cancellationToken = default)
    {
        // Read and potentially verify the GZIP trailer: CRC32 and  size mod 2^32
        var trailer = new byte[8];
        _ = await _stream
            .ReadAtLeastAsync(
                trailer.AsMemory(0, 8),
                8,
                throwOnEndOfStream: false,
                cancellationToken
            )
            .ConfigureAwait(false);

        // Same IO-free core as the sync ReadTrailer; only the fill step above differs.
        ParseTrailer(trailer);
    }

    private async ValueTask ReadAndValidateGzipHeaderAsync(
        CancellationToken cancellationToken = default
    )
    {
        // read the header on the first read
        var header = new byte[10];
        var n = await _stream
            .ReadAsync(header.AsMemory(0, 10), cancellationToken)
            .ConfigureAwait(false);

        // Same IO-free core as the sync ReadAndValidateGzipHeader; only the fill
        // steps (here and below) differ between sync and async.
        if (!ParseFixedHeader(header, n, out var flags))
        {
            return;
        }

        if ((flags & 0x04) == 0x04)
        {
            // read and discard extra field
            var lengthField = new byte[2];
            _ = await _stream
                .ReadAsync(lengthField.AsMemory(0, 2), cancellationToken)
                .ConfigureAwait(false);

            var extraLength = (short)(lengthField[0] + (lengthField[1] * 256));
            var extra = new byte[extraLength];

            if (
                await _stream
                    .ReadAtLeastAsync(
                        extra,
                        extra.Length,
                        throwOnEndOfStream: false,
                        cancellationToken
                    )
                    .ConfigureAwait(false) != extra.Length
            )
            {
                throw new ZlibException("Unexpected end-of-file reading GZIP header.");
            }
        }
        if ((flags & 0x08) == 0x08)
        {
            _name = await ReadZeroTerminatedStringAsync(_stream, cancellationToken)
                .ConfigureAwait(false);
        }
        if ((flags & 0x10) == 0x010)
        {
            await ReadZeroTerminatedStringAsync(_stream, cancellationToken).ConfigureAwait(false);
        }
        if ((flags & 0x02) == 0x02)
        {
            var buf = new byte[1];
            _ = await _stream
                .ReadAsync(buf.AsMemory(0, 1), cancellationToken)
                .ConfigureAwait(false); // CRC16, ignore
        }
    }

    private async ValueTask<string> ReadZeroTerminatedStringAsync(
        Stream stream,
        CancellationToken cancellationToken = default
    )
    {
        var buf1 = new byte[1];
        var list = new List<byte>();
        var done = false;
        do
        {
            // workitem 7740
            var n = await stream
                .ReadAsync(buf1.AsMemory(0, 1), cancellationToken)
                .ConfigureAwait(false);
            if (n != 1)
            {
                throw new ZlibException("Unexpected EOF reading GZIP header.");
            }
            if (buf1[0] == 0)
            {
                done = true;
            }
            else
            {
                list.Add(buf1[0]);
            }
        } while (!done);
        var buffer = list.ToArray();
        return ArchiveEncoding.Decode(buffer);
    }

    internal override async ValueTask<Stream?> GetCompressedStreamAsync(
        CancellationToken cancellationToken = default
    )
    {
        // GZip uses Deflate compression
        return await _compressionProviders
            .CreateDecompressStreamAsync(CompressionType.Deflate, _stream, cancellationToken)
            .ConfigureAwait(false);
    }
}
