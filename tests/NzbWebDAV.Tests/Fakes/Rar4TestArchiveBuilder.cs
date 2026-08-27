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
            uncompressedSize,
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
        int? uncompressedSize = null) =>
        BuildRar4Volume(
            fileName,
            packedSize,
            uncompressedSize ?? packedSize,
            firstVolume: false,
            splitBefore: true,
            splitAfter: splitAfter,
            trailingBytes: trailingBytes,
            encrypted: encrypted);

    internal static byte[] BuildRar4Volume(
        string fileName,
        int packedSize,
        int? uncompressedSize = null,
        bool firstVolume = false,
        bool splitBefore = false,
        bool splitAfter = false,
        int trailingBytes = 0,
        ReadOnlySpan<byte> payloadPrefix = default,
        bool encrypted = false)
    {
        using var stream = new MemoryStream();
        stream.Write([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00]);

        Span<byte> archiveBody = stackalloc byte[11];
        archiveBody[0] = 0x73;
        var archiveFlags = firstVolume ? (ushort)0x0101 : (ushort)0x0001;
        BinaryPrimitives.WriteUInt16LittleEndian(archiveBody[1..], archiveFlags);
        BinaryPrimitives.WriteUInt16LittleEndian(archiveBody[3..], 13);
        BinaryPrimitives.WriteUInt16LittleEndian(archiveBody[5..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(archiveBody[7..], 0);
        WriteHeader(stream, archiveBody);

        var nameBytes = Encoding.ASCII.GetBytes(fileName);
        var headSize = (ushort)(32 + nameBytes.Length);
        var fileBody = new byte[headSize - 2];
        var offset = 0;
        fileBody[offset++] = 0x74;
        var fileFlags = (ushort)(0x8000
                                 | (splitBefore ? 0x0001 : 0)
                                 | (splitAfter ? 0x0002 : 0)
                                 | (encrypted ? 0x0004 : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(fileBody.AsSpan(offset), fileFlags);
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(fileBody.AsSpan(offset), headSize);
        offset += 2;
        BinaryPrimitives.WriteUInt32LittleEndian(fileBody.AsSpan(offset), (uint)packedSize);
        offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(
            fileBody.AsSpan(offset),
            (uint)(uncompressedSize ?? packedSize));
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

        var payload = new byte[packedSize];
        payloadPrefix.CopyTo(payload);
        stream.Write(payload);
        stream.Write(new byte[trailingBytes]);
        return stream.ToArray();
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
