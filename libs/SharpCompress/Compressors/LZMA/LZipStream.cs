using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Algorithms;
using SharpCompress.Common;
using SharpCompress.Crypto;
using SharpCompress.IO;
using SharpCompress.Providers;

namespace SharpCompress.Compressors.LZMA;

/// <summary>
/// Stream supporting the LZIP format, as documented at http://www.nongnu.org/lzip/manual/lzip_manual.html
/// </summary>
public sealed partial class LZipStream : Stream, IFinishable
{
    // _stream is reassigned per member when decompressing a multimember (multi-volume)
    // LZIP stream, so it is not readonly.
    private Stream _stream;
    private readonly CountingStream? _countingWritableSubStream;
    private readonly CountingStream? _countingReadableSubStream;

    // Only populated (and only trusted) when the stream is a verified single member;
    // the trailer size fields at the very end of a multimember stream describe the
    // last member only, so they cannot be used for Length or the LzmaStream output size.
    private readonly ulong? _expectedDataSize;
    private readonly bool _singleMemberVerified;
    private readonly bool _skipTrailerValidation;
    private bool _disposed;
    private bool _finished;

    // Per-member decode state, reset by StartNextMember when advancing to the next member.
    private bool _trailerValidated;
    private uint _seed = Crc32Stream.DEFAULT_SEED;
    private ulong _memberReadCount;
    private long _memberStartPosition;
    private long _compressedDataStartPosition;

    // Cumulative count of decompressed bytes returned across all members; backs Position.
    private ulong _readCount;

    private long _writeCount;
    private readonly Stream? _originalStream;
    private readonly bool _leaveOpen;

    private LZipStream(Stream stream, CompressionMode mode, bool leaveOpen = false)
    {
        Mode = mode;
        _originalStream = stream;
        _leaveOpen = leaveOpen;

        if (mode == CompressionMode.Decompress)
        {
            _skipTrailerValidation = stream is SharpCompressStream;
            _memberStartPosition = stream.CanSeek ? stream.Position : 0;
            var dSize = ValidateAndReadSize(stream);
            if (dSize == 0)
            {
                throw new InvalidFormatException("Not an LZip stream");
            }
            var properties = GetProperties(dSize);

            // The 16-byte size fields at the very end of the stream describe the LAST
            // member only. They can be trusted for the decompressed Length and the
            // LzmaStream output size solely when this is a verified single-member stream;
            // otherwise the expected sizes stay unset and decoding proceeds without a
            // known output size so that multimember streams are read correctly.
            var outputSize = -1L;
            var trailerStream = GetSeekableTrailerStream(stream);
            if (trailerStream is not null)
            {
                var position = trailerStream.Position;
                trailerStream.Position = trailerStream.Length - 16;
                Span<byte> sizeTrailer = stackalloc byte[16];
                trailerStream.ReadAtLeast(
                    sizeTrailer,
                    sizeTrailer.Length,
                    throwOnEndOfStream: false
                );
                var dataSize = BinaryPrimitives.ReadUInt64LittleEndian(sizeTrailer);
                var memberSize = BinaryPrimitives.ReadUInt64LittleEndian(sizeTrailer[8..]);
                trailerStream.Position = position;

                // Single member when the reported member size spans exactly from this
                // member's start to the end of the stream. The member starts six header
                // bytes before the current trailer-stream position.
                var memberStart = position - 6;
                if (memberSize == (ulong)(trailerStream.Length - memberStart))
                {
                    if (dataSize > long.MaxValue)
                    {
                        throw new InvalidFormatException("LZip data size is too large.");
                    }
                    _expectedDataSize = dataSize;
                    _singleMemberVerified = true;
                    outputSize = (long)dataSize;
                }
            }
            _compressedDataStartPosition = stream.CanSeek ? stream.Position : 0;
            _countingReadableSubStream = new CountingStream(
                SharpCompressStream.CreateNonDisposing(stream)
            );
            // leaveOpen: true keeps the counting substream alive so it can be reused when
            // reinitializing the LzmaStream for a subsequent member. LZipStream.Dispose
            // owns disposal of the counting substream.
            _stream = LzmaStream.Create(
                properties,
                _countingReadableSubStream,
                inputSize: -1,
                outputSize: outputSize,
                leaveOpen: true
            );
        }
        else
        {
            //default
            var dSize = 104 * 1024;
            _countingWritableSubStream = new CountingStream(
                SharpCompressStream.CreateNonDisposing(stream)
            );
            _stream = new Crc32Stream(
                LzmaStream.Create(
                    new LzmaEncoderProperties(true, dSize),
                    false,
                    null,
                    _countingWritableSubStream
                )
            );
        }
    }

    public void Finish()
    {
        if (!_finished)
        {
            if (Mode == CompressionMode.Compress)
            {
                var crc32Stream = (Crc32Stream)_stream;
                crc32Stream.WrappedStream.Dispose();
                crc32Stream.Dispose();
                var compressedCount = _countingWritableSubStream.NotNull().BytesWritten;

                Span<byte> intBuf = stackalloc byte[8];
                BinaryPrimitives.WriteUInt32LittleEndian(intBuf, crc32Stream.Crc);
                _countingWritableSubStream?.Write(intBuf.Slice(0, 4));

                BinaryPrimitives.WriteInt64LittleEndian(intBuf, _writeCount);
                _countingWritableSubStream?.Write(intBuf);

                //total with headers
                BinaryPrimitives.WriteUInt64LittleEndian(
                    intBuf,
                    (ulong)compressedCount + (ulong)(6 + 20)
                );
                _countingWritableSubStream?.Write(intBuf);
            }
            _finished = true;
        }
    }

    #region Stream methods

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            base.Dispose(disposing);
            return;
        }
        _disposed = true;
        if (disposing)
        {
            Finish();
            _stream.Dispose();
            // The inner decompress LzmaStream is created with leaveOpen: true, so the
            // counting substream is disposed here (it never disposes the original stream).
            _countingReadableSubStream?.Dispose();
            if (!_leaveOpen)
            {
                _originalStream?.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    public CompressionMode Mode { get; }

    public override bool CanRead => Mode == CompressionMode.Decompress;

    public override bool CanSeek => false;

    public override bool CanWrite => Mode == CompressionMode.Compress;

    public override void Flush() => _stream.Flush();

    // The decompressed length is only known (from the trailer) for a verified
    // single-member stream; the stream is otherwise forward-only.
    public override long Length =>
        Mode == CompressionMode.Decompress && _singleMemberVerified && _expectedDataSize is { } size
            ? checked((long)size)
            : throw new NotSupportedException();

    public override long Position
    {
        get => Mode == CompressionMode.Decompress ? checked((long)_readCount) : _writeCount;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        while (count > 0)
        {
            var read = _stream.Read(buffer, offset, count);
            if (read > 0)
            {
                UpdateChecksum(buffer.AsSpan(offset, read));
                return read;
            }
            if (!AdvanceToNextMember())
            {
                break;
            }
        }
        return 0;
    }

    public override int ReadByte()
    {
        // Delegate to the span-based Read so member advancement, checksumming, and the
        // LzmaStream block-read path (which tracks compressed bytes read) are shared.
        Span<byte> buffer = stackalloc byte[1];
        return Read(buffer) == 0 ? -1 : buffer[0];
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(Span<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            var read = _stream.Read(buffer);
            if (read > 0)
            {
                UpdateChecksum(buffer[..read]);
                return read;
            }
            if (!AdvanceToNextMember())
            {
                break;
            }
        }
        return 0;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _stream.Write(buffer);

        _writeCount += buffer.Length;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _stream.Write(buffer, offset, count);
        _writeCount += count;
    }

    public override void WriteByte(byte value)
    {
        _stream.WriteByte(value);
        ++_writeCount;
    }

    // Async methods moved to LZipStream.Async.cs

    #endregion

    /// <summary>
    /// Determines if the given stream is positioned at the start of a v1 LZip
    /// file, as indicated by the ASCII characters "LZIP" and a version byte
    /// of 1, followed by at least one byte.
    /// </summary>
    /// <param name="stream">The stream to read from. Must not be null.</param>
    /// <returns><c>true</c> if the given stream is an LZip file, <c>false</c> otherwise.</returns>
    public static bool IsLZipFile(Stream stream) => ValidateAndReadSize(stream) != 0;

    /// <summary>
    /// Reads the 6-byte header of the stream, and returns 0 if either the header
    /// couldn't be read or it isn't a validate LZIP header, or the dictionary
    /// size if it *is* a valid LZIP file.
    /// </summary>
    public static int ValidateAndReadSize(Stream stream)
    {
        // Read the header
        Span<byte> header = stackalloc byte[6];
        var n = stream.Read(header);

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
        return (1 << basePower) - (subtractionNumerator * (1 << (basePower - 4)));
    }

    // Async methods moved to LZipStream.Async.cs

    private static readonly byte[] headerBytes =
    [
        (byte)'L',
        (byte)'Z',
        (byte)'I',
        (byte)'P',
        1,
        113,
    ];

    public static void WriteHeaderSize(Stream stream) =>
        // hard coding the dictionary size encoding
        stream.Write(headerBytes, 0, 6);

    /// <summary>
    /// Creates a byte array to communicate the parameters and dictionary size to LzmaStream.
    /// </summary>
    private static byte[] GetProperties(int dictionarySize) =>
        [
            // Parameters as per http://www.nongnu.org/lzip/manual/lzip_manual.html#Stream-format
            // but encoded as a single byte in the format LzmaStream expects.
            // literal_context_bits = 3
            // literal_pos_state_bits = 0
            // pos_state_bits = 2
            93,
            // Dictionary size as 4-byte little-endian value
            (byte)(dictionarySize & 0xff),
            (byte)((dictionarySize >> 8) & 0xff),
            (byte)((dictionarySize >> 16) & 0xff),
            (byte)((dictionarySize >> 24) & 0xff),
        ];

    private static Stream? GetSeekableTrailerStream(Stream stream)
    {
        while (stream is SharpCompressStream { IsPassthrough: true } sharpCompressStream)
        {
            stream = sharpCompressStream.BaseStream();
        }

        if (stream is SeekableSharpCompressStream seekableSharpCompressStream)
        {
            stream = seekableSharpCompressStream.BaseStream();
        }

        return stream is SharpCompressStream ? null
            : stream.CanSeek ? stream
            : null;
    }

    private static bool HasLZipMagic(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 4
        && bytes[0] == (byte)'L'
        && bytes[1] == (byte)'Z'
        && bytes[2] == (byte)'I'
        && bytes[3] == (byte)'P';

    /// <summary>
    /// Decodes the LZIP coded dictionary-size byte, returning 0 for out-of-range values.
    /// </summary>
    private static int DecodeDictionarySize(byte codedSize)
    {
        var basePower = codedSize & 0x1F;
        if (basePower < 4 || basePower > 30)
        {
            return 0;
        }
        var subtractionNumerator = (codedSize & 0xE0) >> 5;
        return (1 << basePower) - (subtractionNumerator * (1 << (basePower - 4)));
    }

    private void UpdateChecksum(ReadOnlySpan<byte> buffer)
    {
        _seed = Crc32Helper.Append(_seed, buffer);
        _readCount += (ulong)buffer.Length;
        _memberReadCount += (ulong)buffer.Length;
    }

    /// <summary>
    /// Called when the current member reports end-of-stream. Validates and consumes the
    /// member trailer, then continues into the next concatenated member when one is present.
    /// Returns <c>false</c> when the whole LZIP stream is exhausted.
    /// </summary>
    private bool AdvanceToNextMember()
    {
        if (Mode != CompressionMode.Decompress)
        {
            return false;
        }

        ValidateTrailer();
        return TryStartNextMember();
    }

    /// <summary>
    /// Repositions a seekable substream to the exact start of the current member's trailer
    /// and returns the compressed size the LZMA decoder consumed for this member. Non-seekable
    /// substreams are already positioned at the trailer because the range decoder reads
    /// byte-by-byte.
    /// </summary>
    private long PrepareTrailerPosition()
    {
        var countingStream = _countingReadableSubStream.NotNull();
        var compressedDataSize = _stream is LzmaStream lzmaStream
            ? lzmaStream.CompressedBytesRead
            : 0;

        if (countingStream.CanSeek && compressedDataSize > 0)
        {
            countingStream.Position = _compressedDataStartPosition + compressedDataSize;
        }

        return compressedDataSize;
    }

    /// <summary>
    /// Validates the 20-byte trailer that was read for the current member. The trailer is
    /// always consumed (see <see cref="ValidateTrailer"/>); when
    /// <see cref="_skipTrailerValidation"/> is set the CRC/size checks are skipped but the
    /// trailer bytes are still consumed so a following member can be located.
    /// </summary>
    private void ValidateTrailerData(
        ReadOnlySpan<byte> trailer,
        int trailerRead,
        long compressedDataSize
    )
    {
        if (_skipTrailerValidation)
        {
            return;
        }

        if (trailerRead < trailer.Length)
        {
            throw new InvalidFormatException(
                "LZip stream is truncated; the member trailer is incomplete."
            );
        }

        var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(trailer);
        var expectedDataSize = BinaryPrimitives.ReadUInt64LittleEndian(trailer[4..]);
        var expectedMemberSize = BinaryPrimitives.ReadUInt64LittleEndian(trailer[12..]);

        var actualCrc = ~_seed;
        if (actualCrc != expectedCrc)
        {
            throw new InvalidFormatException(
                $"LZip CRC mismatch. Expected 0x{expectedCrc:X8}, actual 0x{actualCrc:X8}."
            );
        }

        if (_memberReadCount != expectedDataSize)
        {
            throw new InvalidFormatException(
                $"LZip data size mismatch. Expected {expectedDataSize}, actual {_memberReadCount}."
            );
        }

        // Member size = 6-byte header + compressed data + 20-byte trailer.
        var actualMemberSize = (ulong)compressedDataSize + 26;
        if (actualMemberSize != expectedMemberSize)
        {
            throw new InvalidFormatException(
                $"LZip member size mismatch. Expected {expectedMemberSize}, actual {actualMemberSize}."
            );
        }
    }

    private void ValidateTrailer()
    {
        if (_trailerValidated || Mode != CompressionMode.Decompress)
        {
            return;
        }

        _trailerValidated = true;

        var compressedDataSize = PrepareTrailerPosition();
        Span<byte> trailer = stackalloc byte[20];
        var trailerRead = _countingReadableSubStream
            .NotNull()
            .ReadAtLeast(trailer, trailer.Length, throwOnEndOfStream: false);
        ValidateTrailerData(trailer, trailerRead, compressedDataSize);
    }

    /// <summary>
    /// Reinitializes decode state for a subsequent member and creates a fresh
    /// <see cref="LzmaStream"/> over the shared counting substream. The dictionary size comes
    /// from the already-consumed member header.
    /// </summary>
    private void StartNextMember(int dictionarySize)
    {
        var countingStream = _countingReadableSubStream.NotNull();

        // The previous member's decoder was created with leaveOpen: true, so disposing it
        // leaves the shared counting substream open for reuse here.
        _stream.Dispose();

        _seed = Crc32Stream.DEFAULT_SEED;
        _memberReadCount = 0;
        _trailerValidated = false;
        _memberStartPosition = countingStream.CanSeek ? countingStream.Position - 6 : 0;
        _compressedDataStartPosition = countingStream.CanSeek ? countingStream.Position : 0;

        var properties = GetProperties(dictionarySize);
        _stream = LzmaStream.Create(
            properties,
            countingStream,
            inputSize: -1,
            outputSize: -1,
            leaveOpen: true
        );
    }

    /// <summary>
    /// Peeks for the next concatenated member. Per the lzip specification, trailing data that
    /// is not a valid member header ends the stream.
    /// </summary>
    private bool TryStartNextMember()
    {
        var countingStream = _countingReadableSubStream.NotNull();

        Span<byte> magic = stackalloc byte[4];
        if (
            countingStream.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false)
                < magic.Length
            || !HasLZipMagic(magic)
        )
        {
            return false;
        }

        // Read the remaining two header bytes: version and coded dictionary size.
        Span<byte> rest = stackalloc byte[2];
        if (
            countingStream.ReadAtLeast(rest, rest.Length, throwOnEndOfStream: false) < rest.Length
            || rest[0] != 1
        )
        {
            return false;
        }

        var dictionarySize = DecodeDictionarySize(rest[1]);
        if (dictionarySize == 0)
        {
            return false;
        }

        StartNextMember(dictionarySize);
        return true;
    }
}
