using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using SharpCompress.Common.Tar.Headers;
using SharpCompress.Compressors;
using SharpCompress.Compressors.Deflate;
using SharpCompress.IO;
using SharpCompress.Providers;

namespace SharpCompress.Common.GZip;

internal sealed partial class GZipFilePart : FilePart
{
    private string? _name;
    private readonly Stream _stream;
    private readonly CompressionProviderRegistry _compressionProviders;

    internal static GZipFilePart Create(
        Stream stream,
        IArchiveEncoding archiveEncoding,
        CompressionProviderRegistry compressionProviders
    )
    {
        var part = new GZipFilePart(stream, archiveEncoding, compressionProviders);

        part.ReadAndValidateGzipHeader();
        if (stream.CanSeek)
        {
            var position = stream.Position;
            stream.Position = stream.Length - 8;
            part.ReadTrailer();
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

    private GZipFilePart(
        Stream stream,
        IArchiveEncoding archiveEncoding,
        CompressionProviderRegistry compressionProviders
    )
        : base(archiveEncoding)
    {
        _stream = SharpCompressStream.Create(stream);
        _compressionProviders = compressionProviders;
    }

    internal long EntryStartPosition { get; private set; }

    internal DateTime? DateModified { get; private set; }
    internal uint? Crc { get; private set; }
    internal uint? UncompressedSize { get; private set; }

    internal override string? FilePartName => _name;

    internal override Stream GetCompressedStream()
    {
        //GZip uses Deflate compression, at this point we need a deflate stream
        return _compressionProviders.CreateDecompressStream(CompressionType.Deflate, _stream);
    }

    internal Stream WrapWithChecksumValidation(Stream source, string? entryName) =>
        new GZipChecksumValidationStream(source, _stream, entryName, Crc, UncompressedSize);

    internal override Stream GetRawStream() => _stream;

    private void ReadTrailer()
    {
        // Read and potentially verify the GZIP trailer: CRC32 and  size mod 2^32
        Span<byte> trailer = stackalloc byte[8];
        _stream.ReadAtLeast(trailer, trailer.Length, throwOnEndOfStream: false);
        ParseTrailer(trailer);
    }

    // IO-free core that decodes the 8-byte gzip trailer. Shared by the sync
    // ReadTrailer and async ReadTrailerAsync paths, which only fill the buffer.
    private void ParseTrailer(ReadOnlySpan<byte> trailer)
    {
        Crc = BinaryPrimitives.ReadUInt32LittleEndian(trailer);
        UncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(trailer.Slice(4));
    }

    // IO-free core that validates and decodes the fixed 10-byte gzip member header.
    // Returns false for an empty stream (n == 0; workitem 8501), validates the magic
    // bytes, sets DateModified, and reports the FLG byte via <paramref name="flags"/>.
    // The variable-length extra/name/comment/CRC16 fields still require IO and are
    // read by the sync/async wrappers using the returned flags.
    private bool ParseFixedHeader(ReadOnlySpan<byte> header, int n, out int flags)
    {
        flags = 0;

        // workitem 8501: handle edge case (decompress empty stream)
        if (n == 0)
        {
            return false;
        }

        if (n != 10)
        {
            throw new ZlibException("Not a valid GZIP stream.");
        }

        if (header[0] != 0x1F || header[1] != 0x8B || header[2] != 8)
        {
            throw new ZlibException("Bad GZIP header.");
        }

        var timet = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(4));
        DateModified = TarHeader.EPOCH.AddSeconds(timet);
        flags = header[3];
        return true;
    }

    private void ReadAndValidateGzipHeader()
    {
        // read the header on the first read
        Span<byte> header = stackalloc byte[10];
        var n = _stream.Read(header);

        if (!ParseFixedHeader(header, n, out var flags))
        {
            return;
        }

        if ((flags & 0x04) == 0x04)
        {
            // read and discard extra field
            _ = _stream.Read(header.Slice(0, 2)); // 2-byte length field

            var extraLength = (short)(header[0] + (header[1] * 256));
            var extra = new byte[extraLength];

            if (_stream.ReadAtLeast(extra, extra.Length, throwOnEndOfStream: false) != extra.Length)
            {
                throw new ZlibException("Unexpected end-of-file reading GZIP header.");
            }
        }
        if ((flags & 0x08) == 0x08)
        {
            _name = ReadZeroTerminatedString(_stream);
        }
        if ((flags & 0x10) == 0x010)
        {
            ReadZeroTerminatedString(_stream);
        }
        if ((flags & 0x02) == 0x02)
        {
            _stream.ReadByte(); // CRC16, ignore
        }
    }

    private string ReadZeroTerminatedString(Stream stream)
    {
        Span<byte> buf1 = stackalloc byte[1];
        var list = new List<byte>();
        var done = false;
        do
        {
            // workitem 7740
            var n = stream.Read(buf1);
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
}
