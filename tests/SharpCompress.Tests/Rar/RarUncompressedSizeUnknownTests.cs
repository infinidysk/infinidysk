using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Hashing;
using System.Text;
using SharpCompress.Common.Rar.Headers;
using SharpCompress.IO;
using SharpCompress.Readers;
using Xunit;

namespace SharpCompress.Test.Rar;

public class RarUncompressedSizeUnknownTests
{
    [Fact]
    public void Rar5_UnpackedSizeUnknownFlag_SetsIsUncompressedSizeUnknown()
    {
        var bytes = BuildRar5StoredVolume(
            fileName: "movie.mkv",
            packedSize: 100,
            declaredUncompressedSize: 42,
            unknownUncompressedSize: true
        );

        var fh = ReadFirstFileHeader(bytes);
        Assert.True(fh.IsUncompressedSizeUnknown);
        Assert.Equal(long.MaxValue, fh.UncompressedSize);
        Assert.Equal(100, fh.AdditionalDataSize);
        Assert.True(fh.IsStored);
    }

    [Fact]
    public void Rar5_KnownUncompressedSize_IsNotUnknown()
    {
        var bytes = BuildRar5StoredVolume(
            fileName: "movie.mkv",
            packedSize: 100,
            declaredUncompressedSize: 100,
            unknownUncompressedSize: false
        );

        var fh = ReadFirstFileHeader(bytes);
        Assert.False(fh.IsUncompressedSizeUnknown);
        Assert.Equal(100, fh.UncompressedSize);
    }

    [Fact]
    public void Rar4_UnpSizeFfffffffWithoutLarge_SetsIsUncompressedSizeUnknown()
    {
        var bytes = BuildRar4StoredVolume(
            fileName: "movie.mkv",
            packedSize: 50,
            uncompressedSize: unchecked((int)0xffffffff)
        );

        var fh = ReadFirstFileHeader(bytes);
        Assert.True(fh.IsUncompressedSizeUnknown);
        Assert.Equal(long.MaxValue, fh.UncompressedSize);
    }

    [Fact]
    public void ExistingRar5Fixture_KnownSize_IsNotUnknown()
    {
        var path = Path.Combine(TestBase.TEST_ARCHIVES_PATH, "Rar5.none.rar");
        using var stream = File.OpenRead(path);
        var factory = new RarHeaderFactory(
            StreamingMode.Seekable,
            ReaderOptions.ForExternalStream with
            {
                LeaveStreamOpen = true,
            }
        );

        IRarFileHeader? first = null;
        foreach (var header in factory.ReadHeaders(stream))
        {
            if (header is IRarFileHeader fh && !fh.IsDirectory)
            {
                first = fh;
                break;
            }
        }

        Assert.NotNull(first);
        Assert.False(first.IsUncompressedSizeUnknown);
        Assert.NotEqual(long.MaxValue, first.UncompressedSize);
    }

    private static IRarFileHeader ReadFirstFileHeader(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var factory = new RarHeaderFactory(
            StreamingMode.Seekable,
            ReaderOptions.ForExternalStream with
            {
                LeaveStreamOpen = true,
            }
        );

        foreach (var header in factory.ReadHeaders(stream))
        {
            if (header is IRarFileHeader fh && !fh.IsDirectory)
            {
                return fh;
            }
        }

        throw new InvalidOperationException("No file header found.");
    }

    private static byte[] BuildRar5StoredVolume(
        string fileName,
        int packedSize,
        long declaredUncompressedSize,
        bool unknownUncompressedSize
    )
    {
        using var ms = new MemoryStream();
        ms.Write([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00]);

        // Archive header: type=1, flags=0, archive flags=VOLUME (first volume has no volume number).
        WriteRar5Header(
            ms,
            headerCode: 1,
            headerFlags: 0,
            dataSize: null,
            bodyWriter: body =>
            {
                WriteVInt(body, 1); // VOLUME
            }
        );

        var nameBytes = Encoding.UTF8.GetBytes(fileName);
        ushort fileFlags = unknownUncompressedSize
            ? (ushort)0x0008 // UNPACKED_SIZE_UNKNOWN
            : (ushort)0;

        WriteRar5Header(
            ms,
            headerCode: 2,
            headerFlags: 0x0002, // HAS_DATA
            dataSize: packedSize,
            bodyWriter: body =>
            {
                WriteVInt(body, fileFlags);
                WriteVInt(body, declaredUncompressedSize);
                WriteVInt(body, 0); // attributes
                WriteVInt(body, 0); // compression info (stored)
                WriteVInt(body, 1); // host OS unix
                WriteVInt(body, nameBytes.Length);
                body.Write(nameBytes);
            }
        );

        ms.Write(new byte[packedSize]);
        return ms.ToArray();
    }

    private static byte[] BuildRar4StoredVolume(
        string fileName,
        int packedSize,
        int uncompressedSize
    )
    {
        using var ms = new MemoryStream();
        ms.Write([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00]);

        {
            Span<byte> body = stackalloc byte[11];
            body[0] = 0x73;
            BinaryPrimitives.WriteUInt16LittleEndian(body[1..], 0x0101);
            BinaryPrimitives.WriteUInt16LittleEndian(body[3..], 13);
            BinaryPrimitives.WriteUInt16LittleEndian(body[5..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(body[7..], 0);
            WriteRar4Header(ms, body);
        }

        var nameBytes = Encoding.ASCII.GetBytes(fileName);
        var headSize = (ushort)(32 + nameBytes.Length);
        {
            var body = new byte[headSize - 2];
            var o = 0;
            body[o++] = 0x74;
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(o), 0x8000); // HAS_DATA
            o += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(o), headSize);
            o += 2;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(o), (uint)packedSize);
            o += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(o), (uint)uncompressedSize);
            o += 4;
            body[o++] = 2;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(o), 0);
            o += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(o), 0);
            o += 4;
            body[o++] = 20;
            body[o++] = 0x30;
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(o), (ushort)nameBytes.Length);
            o += 2;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(o), 0);
            o += 4;
            nameBytes.CopyTo(body.AsSpan(o));
            WriteRar4Header(ms, body);
        }

        ms.Write(new byte[packedSize]);
        return ms.ToArray();
    }

    private static void WriteRar5Header(
        Stream stream,
        byte headerCode,
        ushort headerFlags,
        long? dataSize,
        Action<MemoryStream> bodyWriter
    )
    {
        using var content = new MemoryStream();
        WriteVInt(content, headerCode);
        WriteVInt(content, headerFlags);
        if ((headerFlags & 0x0002) != 0)
        {
            if (dataSize is null)
            {
                throw new ArgumentNullException(nameof(dataSize));
            }
            WriteVInt(content, dataSize.Value);
        }

        bodyWriter(content);
        var contentBytes = content.ToArray();

        using var sized = new MemoryStream();
        WriteVInt(sized, contentBytes.Length);
        sized.Write(contentBytes);
        var crcPayload = sized.ToArray();

        var crc = Crc32.HashToUInt32(crcPayload);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(crcBytes, crc);
        stream.Write(crcBytes);
        stream.Write(crcPayload);
    }

    private static void WriteRar4Header(Stream stream, ReadOnlySpan<byte> bodyWithoutCrc)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in bodyWithoutCrc)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        Span<byte> hdr = stackalloc byte[bodyWithoutCrc.Length + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(hdr, (ushort)(~crc));
        bodyWithoutCrc.CopyTo(hdr[2..]);
        stream.Write(hdr);
    }

    private static void WriteVInt(Stream stream, long value)
    {
        var n = (ulong)value;
        while (n >= 0x80)
        {
            stream.WriteByte((byte)(n | 0x80));
            n >>= 7;
        }
        stream.WriteByte((byte)n);
    }
}
