using System.Runtime.InteropServices;
using System.Text;
using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Par2Recovery.Packets;

namespace NzbWebDAV.Tests.Par2Recovery;

internal static class Par2TestPackets
{
    internal static byte[] BuildFileDescBody(
        byte[] fileId,
        string fileName,
        byte[]? file16kHash = null,
        ulong fileLength = 0)
    {
        var nameBytes = Encoding.UTF8.GetBytes(fileName);
        var paddedLen = (nameBytes.Length + 3) / 4 * 4;
        var body = new byte[56 + paddedLen];
        fileId.CopyTo(body, 0);
        (file16kHash ?? new byte[16]).CopyTo(body, 32);
        BitConverter.TryWriteBytes(body.AsSpan(48, 8), fileLength);
        nameBytes.CopyTo(body, 56);
        return body;
    }

    internal static byte[] BuildPar2Bytes(params byte[][] fileDescBodies)
    {
        using var stream = new MemoryStream();
        foreach (var body in fileDescBodies)
            WritePacket(stream, FileDesc.PacketType, body);
        return stream.ToArray();
    }

    internal static async Task<List<FileDesc>> ReadFileDescsAsync(byte[] par2Bytes)
    {
        var descriptors = new List<FileDesc>();
        await using var stream = new MemoryStream(par2Bytes);
        await foreach (var descriptor in Par2.ReadFileDescriptions(stream))
            descriptors.Add(descriptor);
        return descriptors;
    }

    private static void WritePacket(Stream stream, string packetType, byte[] body)
    {
        var headerSize = Marshal.SizeOf<Par2PacketHeader>();
        var header = new Par2PacketHeader
        {
            Magic = "PAR2\0PKT"u8.ToArray(),
            PacketLength = (ulong)(headerSize + body.Length),
            PacketHash = new byte[16],
            RecoverySetID = new byte[16],
            PacketType = Encoding.ASCII.GetBytes(packetType.PadRight(16, '\0')[..16]),
        };

        var headerBytes = new byte[headerSize];
        var handle = GCHandle.Alloc(headerBytes, GCHandleType.Pinned);
        try
        {
            Marshal.StructureToPtr(header, handle.AddrOfPinnedObject(), false);
        }
        finally
        {
            handle.Free();
        }

        stream.Write(headerBytes);
        stream.Write(body);
    }
}
