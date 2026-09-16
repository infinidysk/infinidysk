using System.Buffers.Binary;
using System.Text;

namespace NzbWebDAV.Tests.Fakes;

internal static class Rar4TestArchiveBuilder
{
    internal static byte[] BuildRar4SplitFirstVolume(
        string fileName,
        int packedSize,
        int uncompressedSize,
        ReadOnlySpan<byte> payloadPrefix = default) =>
        BuildRar4Volume(
            fileName,
            packedSize,
            uncompressedSize ?? packedSize,
            firstVolume: true,
            splitBefore: false,
            splitAfter: true,
            payloadPrefix: payloadPrefix);

    internal static byte[] BuildRar4ContinuationVolume(
        string fileName,
        int packedSize,
        int trailingBytes = 0,
        bool splitAfter = false,
        bool encrypted = false,
        int? uncompressedSize = null,
        IReadOnlyList<(string FileName, int PackedSize)>? trailingMembers = null) =>
        BuildRar4Volume(
            fileName,
            packedSize,
            uncompressedSize,
            firstVolume: false,
            splitBefore: true,
            splitAfter: splitAfter,
            trailingBytes: trailingBytes,
            encrypted: encrypted,
            trailingMembers: trailingMembers);

    internal static byte[] BuildRar4Volume(
        string fileName,
        int packedSize,
        int? uncompressedSize = null,
        bool firstVolume = false,
        bool splitBefore = false,
        bool splitAfter = false,
        int trailingBytes = 0,
        ReadOnlySpan<byte> payloadPrefix = default,
        bool encrypted = false,
        IReadOnlyList<(string FileName, int PackedSize)>? trailingMembers = null,
        IReadOnlyList<string>? trailingDirectories = null)
    {
        using var stream = new MemoryStream();
        stream.Write([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00]);

        WriteArchiveHeader(stream, firstVolume);
        WriteFileEntry(
            stream,
            fileName,
            packedSize,
            uncompressedSize ?? packedSize,
            splitBefore,
            splitAfter,
            encrypted,
            isDirectory: false,
            payloadPrefix);
        foreach (var (memberName, memberPacked) in trailingMembers ?? [])
            WriteFileEntry(
                stream,
                memberName,
                memberPacked,
                memberPacked,
                splitBefore: false,
                splitAfter: false,
                encrypted: false,
                isDirectory: false,
                payloadPrefix: default);
        foreach (var directoryName in trailingDirectories ?? [])
            WriteFileEntry(
                stream,
                directoryName,
                packedSize: 0,
                uncompressedSize: 0,
                splitBefore: false,
                splitAfter: false,
                encrypted: false,
                isDirectory: true,
                payloadPrefix: default);
        stream.Write(new byte[trailingBytes]);
        return stream.ToArray();
    }

    private static void WriteArchiveHeader(Stream stream, bool firstVolume)
    {
        Span<byte> archiveBody = stackalloc byte[11];
        archiveBody[0] = 0x73;
        var archiveFlags = firstVolume ? (ushort)0x0101 : (ushort)0x0001;
        BinaryPrimitives.WriteUInt16LittleEndian(archiveBody[1..], archiveFlags);
        BinaryPrimitives.WriteUInt16LittleEndian(archiveBody[3..], 13);
        BinaryPrimitives.WriteUInt16LittleEndian(archiveBody[5..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(archiveBody[7..], 0);
        WriteHeader(stream, archiveBody);
    }

    private static void WriteFileEntry(
        Stream stream,
        string fileName,
        int packedSize,
        int uncompressedSize,
        bool splitBefore,
        bool splitAfter,
        bool encrypted,
        bool isDirectory,
        ReadOnlySpan<byte> payloadPrefix)
    {
        var nameBytes = Encoding.ASCII.GetBytes(fileName);
        var headSize = (ushort)(32 + nameBytes.Length);
        var fileBody = new byte[headSize - 2];
        var offset = 0;
        fileBody[offset++] = 0x74;
        var fileFlags = (ushort)(0x8000
                                 | (splitBefore ? 0x0001 : 0)
                                 | (splitAfter ? 0x0002 : 0)
                                 | (encrypted ? 0x0004 : 0)
                                 | (isDirectory ? 0x00E0 : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(fileBody.AsSpan(offset), fileFlags);
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(fileBody.AsSpan(offset), headSize);
        offset += 2;
        BinaryPrimitives.WriteUInt32LittleEndian(fileBody.AsSpan(offset), (uint)packedSize);
        offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(
            fileBody.AsSpan(offset),
            (uint)uncompressedSize);
        offset += 4;
        fileBody[offset++] = 2; // HostOS Unix
        BinaryPrimitives.WriteUInt32LittleEndian(fileBody.AsSpan(offset), 0);
        offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(fileBody.AsSpan(offset), 0);
        offset += 4;
        fileBody[offset++] = 20; // UnpVer
        fileBody[offset++] = 0x30; // store
        BinaryPrimitives.WriteUInt16LittleEndian(
            fileBody.AsSpan(offset),
            (ushort)nameBytes.Length);
        offset += 2;
        BinaryPrimitives.WriteUInt32LittleEndian(fileBody.AsSpan(offset), 0);
        offset += 4;
        nameBytes.CopyTo(fileBody.AsSpan(offset));
        WriteHeader(stream, fileBody);

        if (isDirectory) return;
        var payload = new byte[packedSize];
        payloadPrefix.CopyTo(payload);
        stream.Write(payload);
    }

    private static void WriteHeader(Stream stream, ReadOnlySpan<byte> bodyWithoutCrc)
    {
        var crc = RarCrc16(bodyWithoutCrc);
        Span<byte> header = stackalloc byte[bodyWithoutCrc.Length + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(header, crc);
        bodyWithoutCrc.CopyTo(header[2..]);
        stream.Write(header);
    }

    private static ushort RarCrc16(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var value in data)
        {
            crc ^= value;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return (ushort)(~crc);
    }
}
