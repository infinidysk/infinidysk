using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Crypto;

namespace SharpCompress.Compressors.LZMA;

public sealed partial class LZipStream
{
    public static LZipStream Create(Stream stream, CompressionMode mode, bool leaveOpen = false)
    {
        if (mode == CompressionMode.Compress)
        {
            WriteHeaderSize(stream);
        }
        return new LZipStream(stream, mode, leaveOpen);
    }

    public static async ValueTask<LZipStream> CreateAsync(
        Stream stream,
        CompressionMode mode,
        bool leaveOpen = false,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (mode == CompressionMode.Compress)
        {
            await WriteHeaderSizeAsync(stream).ConfigureAwait(false);
        }
        return new LZipStream(stream, mode, leaveOpen);
    }

    public async ValueTask FinishAsync(CancellationToken cancellationToken)
    {
        if (_finished)
        {
            return;
        }

        if (Mode == CompressionMode.Compress)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var crc32Stream = (Crc32Stream)_stream;
            await FinishWrappedStreamAsync(crc32Stream).ConfigureAwait(false);
            var compressedCount = _countingWritableSubStream.NotNull().BytesWritten;

            var intBuf = ArrayPool<byte>.Shared.Rent(8);
            try
            {
                BinaryPrimitives.WriteUInt32LittleEndian(intBuf, crc32Stream.Crc);
                await _countingWritableSubStream
                    .NotNull()
                    .WriteAsync(intBuf.AsMemory(0, 4), cancellationToken)
                    .ConfigureAwait(false);

                BinaryPrimitives.WriteInt64LittleEndian(intBuf, _writeCount);
                await _countingWritableSubStream
                    .NotNull()
                    .WriteAsync(intBuf.AsMemory(0, 8), cancellationToken)
                    .ConfigureAwait(false);

                // Total member size includes the 6-byte header and 20-byte trailer.
                BinaryPrimitives.WriteUInt64LittleEndian(
                    intBuf,
                    (ulong)compressedCount + (ulong)(6 + 20)
                );
                await _countingWritableSubStream
                    .NotNull()
                    .WriteAsync(intBuf.AsMemory(0, 8), cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(intBuf);
            }
        }

        _finished = true;
    }

    // Header bytes are fixed metadata; no caller token exists at factory creation time.
    private static async ValueTask WriteHeaderSizeAsync(Stream stream) =>
        await stream
            .WriteAsync(headerBytes.AsMemory(0, 6), CancellationToken.None)
            .ConfigureAwait(false);

    private static async ValueTask FinishWrappedStreamAsync(Crc32Stream crc32Stream)
    {
        if (crc32Stream.WrappedStream is IAsyncDisposable asyncDisposableWrappedStream)
        {
            await asyncDisposableWrappedStream.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
#pragma warning disable VSTHRD103 // Fallback for streams that do not support async disposal.
            crc32Stream.WrappedStream.Dispose();
            crc32Stream.Dispose();
#pragma warning restore VSTHRD103
        }
    }

    /// <summary>
    /// Asynchronously determines if the given stream is positioned at the start of a v1 LZip
    /// file, as indicated by the ASCII characters "LZIP" and a version byte
    /// of 1, followed by at least one byte.
    /// </summary>
    /// <param name="stream">The stream to read from. Must not be null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the given stream is an LZip file, <c>false</c> otherwise.</returns>
    public static async ValueTask<bool> IsLZipFileAsync(
        Stream stream,
        CancellationToken cancellationToken = default
    ) => await ValidateAndReadSizeAsync(stream, cancellationToken).ConfigureAwait(false) != 0;

    /// <summary>
    /// Asynchronously reads the 6-byte header of the stream, and returns 0 if either the header
    /// couldn't be read or it isn't a validate LZIP header, or the dictionary
    /// size if it *is* a valid LZIP file.
    /// </summary>
    public static async ValueTask<int> ValidateAndReadSizeAsync(
        Stream stream,
        CancellationToken cancellationToken
    )
    {
        // Read the header
        var header = ArrayPool<byte>.Shared.Rent(6);
        try
        {
            var n = await stream
                .ReadAsync(header.AsMemory(0, 6), cancellationToken)
                .ConfigureAwait(false);

            // Incomplete header read is treated as not-LZIP (return 0); callers do not retry partial reads.

            if (n != 6)
            {
                return 0;
            }

            if (
                header[0] != 'L'
                || header[1] != 'Z'
                || header[2] != 'I'
                || header[3] != 'P'
                || header[4] != 1 /* version 1 */
            )
            {
                return 0;
            }
            var basePower = header[5] & 0x1F;
            var subtractionNumerator = (header[5] & 0xE0) >> 5;
            if (basePower < 4 || basePower > 30)
            {
                return 0;
            }
            return (1 << basePower) - (subtractionNumerator * (1 << (basePower - 4)));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }
    }

    /// <summary>
    /// Asynchronously reads bytes from the current stream into a buffer.
    /// </summary>
    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    ) => ReadAndValidateAsync(buffer, cancellationToken);

    /// <summary>
    /// Asynchronously reads bytes from the current stream into a buffer.
    /// </summary>
    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken = default
    ) => ReadAndValidateAsync(buffer, offset, count, cancellationToken);

    private async Task<int> ReadAndValidateAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        while (count > 0)
        {
            var read = await _stream
                .ReadAsync(buffer.AsMemory(offset, count), cancellationToken)
                .ConfigureAwait(false);
            if (read > 0)
            {
                UpdateChecksum(buffer.AsSpan(offset, read));
                return read;
            }
            if (!await AdvanceToNextMemberAsync(cancellationToken).ConfigureAwait(false))
            {
                break;
            }
        }
        return 0;
    }

    private async ValueTask<int> ReadAndValidateAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        while (!buffer.IsEmpty)
        {
            var read = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                UpdateChecksum(buffer.Span[..read]);
                return read;
            }
            if (!await AdvanceToNextMemberAsync(cancellationToken).ConfigureAwait(false))
            {
                break;
            }
        }
        return 0;
    }

    /// <summary>
    /// Async counterpart of <see cref="AdvanceToNextMember"/>: validates and consumes the
    /// current member trailer, then advances into the next concatenated member if present.
    /// </summary>
    private async ValueTask<bool> AdvanceToNextMemberAsync(CancellationToken cancellationToken)
    {
        if (Mode != CompressionMode.Decompress)
        {
            return false;
        }

        await ValidateTrailerAsync(cancellationToken).ConfigureAwait(false);
        return await TryStartNextMemberAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ValidateTrailerAsync(CancellationToken cancellationToken)
    {
        if (_trailerValidated || Mode != CompressionMode.Decompress)
        {
            return;
        }

        _trailerValidated = true;

        var compressedDataSize = PrepareTrailerPosition();
        var buffer = ArrayPool<byte>.Shared.Rent(20);
        try
        {
            var trailerRead = await _countingReadableSubStream
                .NotNull()
                .ReadAtLeastAsync(
                    buffer.AsMemory(0, 20),
                    20,
                    throwOnEndOfStream: false,
                    cancellationToken
                )
                .ConfigureAwait(false);
            ValidateTrailerData(buffer.AsSpan(0, 20), trailerRead, compressedDataSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async ValueTask<bool> TryStartNextMemberAsync(CancellationToken cancellationToken)
    {
        var countingStream = _countingReadableSubStream.NotNull();
        var buffer = ArrayPool<byte>.Shared.Rent(6);
        try
        {
            var magicRead = await countingStream
                .ReadAtLeastAsync(
                    buffer.AsMemory(0, 4),
                    4,
                    throwOnEndOfStream: false,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (magicRead < 4 || !HasLZipMagic(buffer.AsSpan(0, 4)))
            {
                return false;
            }

            // Read the remaining two header bytes: version and coded dictionary size.
            var restRead = await countingStream
                .ReadAtLeastAsync(
                    buffer.AsMemory(4, 2),
                    2,
                    throwOnEndOfStream: false,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (restRead < 2 || buffer[4] != 1)
            {
                return false;
            }

            var dictionarySize = DecodeDictionarySize(buffer[5]);
            if (dictionarySize == 0)
            {
                return false;
            }

            StartNextMember(dictionarySize);
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Asynchronously writes bytes from a buffer to the current stream.
    /// </summary>
    public override async Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _stream
            .WriteAsync(buffer.AsMemory(offset, count), cancellationToken)
            .ConfigureAwait(false);
        _writeCount += count;
    }
}
