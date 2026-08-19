using System.Runtime.InteropServices;
using System.Text;
using NzbWebDAV.Par2Recovery.Packets;

namespace NzbWebDAV.Par2Recovery;

/// <summary>
/// Reads PAR2 packets for repair (full payloads, hash verification). Does not alter import-time parsing.
/// </summary>
public static class Par2RepairReader
{
    private const string Par2PacketHeaderMagic = "PAR2\0PKT";

    public static async Task<Par2Packet> ReadVerifiedPacketAsync(
        Stream stream, bool readRecvSlicPayload, CancellationToken ct)
    {
        var header = await ReadStructAsync<Par2PacketHeader>(stream, ct).ConfigureAwait(false);
        var magic = Encoding.ASCII.GetString(header.Magic);
        if (!Par2PacketHeaderMagic.Equals(magic, StringComparison.Ordinal))
            throw new InvalidDataException("Invalid PAR2 magic constant.");

        var headerSize = Marshal.SizeOf<Par2PacketHeader>();
        var packetLength = header.PacketLength;
        if (packetLength < (ulong)headerSize || packetLength > int.MaxValue)
            throw new InvalidDataException($"Invalid PAR2 packet length {packetLength}.");

        var bodyLength = (int)(packetLength - (ulong)headerSize);
        var packetBytes = new byte[(int)packetLength];
        WriteHeader(packetBytes, header);

        if (bodyLength > 0)
            await stream.ReadExactlyAsync(packetBytes.AsMemory(headerSize, bodyLength), ct).ConfigureAwait(false);

        if (!Par2PacketHash.Verify(packetBytes, header.PacketHash))
            throw new InvalidDataException("PAR2 packet MD5 hash mismatch.");

        var packetType = Encoding.ASCII.GetString(header.PacketType).TrimEnd('\0');
        Par2Packet packet = packetType switch
        {
            FileDesc.PacketType => new FileDesc(header),
            MainPacket.PacketType => new MainPacket(header),
            IfscPacket.PacketType => new IfscPacket(header),
            RecvSlic.PacketType => new RecvSlic(header, readRecvSlicPayload),
            UniFileN.PacketType => new UniFileN(header),
            _ => new Par2Packet(header),
        };

        if (bodyLength > 0)
        {
            var body = new byte[bodyLength];
            Buffer.BlockCopy(packetBytes, headerSize, body, 0, bodyLength);
            packet.ParseBodyBytes(body);
        }

        return packet;
    }

    private static async Task<T> ReadStructAsync<T>(Stream stream, CancellationToken ct) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var buffer = new byte[size];
        await stream.ReadExactlyAsync(buffer.AsMemory(0, size), ct).ConfigureAwait(false);
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            return Marshal.PtrToStructure<T>(pinned.AddrOfPinnedObject())!;
        }
        finally
        {
            pinned.Free();
        }
    }

    private static void WriteHeader(byte[] packetBytes, Par2PacketHeader header)
    {
        var pinned = GCHandle.Alloc(packetBytes, GCHandleType.Pinned);
        try
        {
            Marshal.StructureToPtr(header, pinned.AddrOfPinnedObject(), false);
        }
        finally
        {
            pinned.Free();
        }
    }
}
