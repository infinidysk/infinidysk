using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.IO;

namespace SharpCompress.Common.Tar.Headers;

internal sealed partial class TarHeader
{
    internal async ValueTask WriteAsync(
        Stream output,
        CancellationToken cancellationToken = default
    )
    {
        switch (WriteFormat)
        {
            case TarHeaderWriteFormat.GNU_TAR_LONG_LINK:
                await WriteGnuTarLongLinkAsync(output, cancellationToken).ConfigureAwait(false);
                break;
            case TarHeaderWriteFormat.USTAR:
                await WriteUstarAsync(output, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArchiveOperationException("This should be impossible...");
        }
    }

    private async ValueTask WriteUstarAsync(Stream output, CancellationToken cancellationToken)
    {
        var buffer = new byte[BLOCK_SIZE];
        FormatUstar(buffer);
        await output
            .WriteAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask WriteGnuTarLongLinkAsync(
        Stream output,
        CancellationToken cancellationToken
    )
    {
        var buffer = new byte[BLOCK_SIZE];
        var nameByteCount = FormatGnuTarLongLinkHeader(buffer);
        await output
            .WriteAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
            .ConfigureAwait(false);

        if (nameByteCount > 100)
        {
            await WriteLongFilenameHeaderAsync(output, cancellationToken).ConfigureAwait(false);
            Name = ArchiveEncoding.Decode(
                ArchiveEncoding.Encode(Name.NotNull("Name is null")),
                0,
                100 - ArchiveEncoding.GetEncoding().GetMaxByteCount(1)
            );
            await WriteGnuTarLongLinkAsync(output, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask WriteLongFilenameHeaderAsync(
        Stream output,
        CancellationToken cancellationToken
    )
    {
        var nameBytes = ArchiveEncoding.Encode(Name.NotNull("Name is null"));
        await output
            .WriteAsync(nameBytes.AsMemory(0, nameBytes.Length), cancellationToken)
            .ConfigureAwait(false);

        var numPaddingBytes = BLOCK_SIZE - (nameBytes.Length % BLOCK_SIZE);
        if (numPaddingBytes == 0)
        {
            numPaddingBytes = BLOCK_SIZE;
        }

        await output
            .WriteAsync(ZeroBlock.Slice(0, numPaddingBytes), cancellationToken)
            .ConfigureAwait(false);
    }

    internal async ValueTask<bool> ReadAsync(
        AsyncBinaryReader reader,
        PaxMetadata? globalPaxMetadata = null,
        CancellationToken cancellationToken = default
    )
    {
        globalPaxMetadata ??= new PaxMetadata();
        var pendingMetadata = globalPaxMetadata.Clone();
        var buffer = ArrayPool<byte>.Shared.Rent(BLOCK_SIZE);
        try
        {
            EntryType entryType;
            while (true)
            {
                await reader
                    .ReadBytesAsync(buffer, 0, BLOCK_SIZE, cancellationToken)
                    .ConfigureAwait(false);
                entryType = ReadEntryType(buffer);

                // LongName and LongLink headers can follow each other and need
                // to apply to the header that follows them.
                if (entryType == EntryType.LongName)
                {
                    pendingMetadata.Name = await ReadLongNameAsync(
                            reader,
                            buffer,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    continue;
                }

                if (entryType == EntryType.LongLink)
                {
                    pendingMetadata.LinkName = await ReadLongNameAsync(
                            reader,
                            buffer,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    continue;
                }

                if (entryType == EntryType.LocalExtendedHeader)
                {
                    await ReadPaxMetadataAsync(reader, buffer, pendingMetadata, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (entryType == EntryType.GlobalExtendedHeader)
                {
                    await ReadPaxMetadataAsync(reader, buffer, globalPaxMetadata, cancellationToken)
                        .ConfigureAwait(false);
                    pendingMetadata = globalPaxMetadata.Clone();
                    continue;
                }

                break;
            }

            // Parse the fully-read block with the shared IO-free core while the
            // rented buffer is still valid (before it is returned to the pool).
            return ParseCore(buffer.AsSpan(0, BLOCK_SIZE), entryType, pendingMetadata);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async ValueTask ReadLengthAsync(
        AsyncBinaryReader reader,
        int length,
        CancellationToken cancellationToken
    )
    {
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await reader.ReadBytesAsync(buffer, 0, length, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async ValueTask<string> ReadLongNameAsync(
        AsyncBinaryReader reader,
        byte[] buffer,
        CancellationToken cancellationToken
    )
    {
        var nameBytes = await ReadMetadataPayloadAsync(
                reader,
                buffer,
                MAX_LONG_NAME_SIZE,
                "Long name",
                cancellationToken
            )
            .ConfigureAwait(false);

        return ArchiveEncoding.Decode(nameBytes, 0, nameBytes.Length).TrimNulls();
    }

    private async ValueTask ReadPaxMetadataAsync(
        AsyncBinaryReader reader,
        byte[] buffer,
        PaxMetadata pendingMetadata,
        CancellationToken cancellationToken
    )
    {
        var payload = await ReadMetadataPayloadAsync(
                reader,
                buffer,
                MAX_PAX_HEADER_SIZE,
                "PAX header",
                cancellationToken
            )
            .ConfigureAwait(false);

        ParsePaxRecords(payload, pendingMetadata);
    }

    private async ValueTask<byte[]> ReadMetadataPayloadAsync(
        AsyncBinaryReader reader,
        byte[] buffer,
        int maxSize,
        string payloadName,
        CancellationToken cancellationToken
    )
    {
        var size = ReadSize(buffer);

        // Validate size to prevent memory exhaustion from malformed headers
        if (size < 0 || size > maxSize)
        {
            throw new InvalidFormatException(
                $"{payloadName} size {size} is invalid or exceeds maximum allowed size of {maxSize} bytes"
            );
        }

        var payloadLength = (int)size;
        var payload = new byte[payloadLength];
        await reader
            .ReadBytesAsync(payload, 0, payloadLength, cancellationToken)
            .ConfigureAwait(false);

        var paddingLength = GetPaddingLength(payloadLength);
        if (paddingLength > 0)
        {
            await ReadLengthAsync(reader, paddingLength, cancellationToken).ConfigureAwait(false);
        }

        return payload;
    }
}
