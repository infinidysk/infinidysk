using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Par2Recovery.Packets;

namespace NzbWebDAV.Par2Recovery;

/// <summary>
/// Reads PAR2 packets for repair (full payloads, hash verification). Does not alter import-time parsing.
/// </summary>
public static class Par2RepairReader
{
    private const int HeaderSize = 64;
    internal const int ScratchBytes = 64 * 1024;

    public static Task<Par2Packet> ReadVerifiedPacketAsync(
        Stream stream, bool readRecvSlicPayload, CancellationToken ct)
        => ReadVerifiedPacketAsync(stream, new ReadOptions(new Par2MemoryBudget(256L * 1024 * 1024),
            readRecvSlicPayload), ct);

    internal sealed record ReadOptions(Par2MemoryBudget Budget, bool RetainRecoveryPayload,
        string? ExpectedRecoverySetId = null, int? ExpectedSliceSize = null);

#pragma warning disable CA5351 // PAR2 packet integrity uses MD5.
    internal static async Task<Par2Packet> ReadVerifiedPacketAsync(Stream stream, ReadOptions options, CancellationToken ct)
    {
        using var scratchReservation = options.Budget.Reserve(ScratchBytes + 1024);
        var headerBytes = new byte[HeaderSize];
        await stream.ReadExactlyAsync(headerBytes, ct).ConfigureAwait(false);
        if (!headerBytes.AsSpan(0, 8).SequenceEqual("PAR2\0PKT"u8))
            throw new InvalidDataException("Invalid PAR2 magic constant.");

        var packetLength = BinaryPrimitives.ReadUInt64LittleEndian(headerBytes.AsSpan(8));
        if (packetLength < HeaderSize || packetLength > int.MaxValue || packetLength % 4 != 0)
            throw new InvalidDataException($"Invalid PAR2 packet length {packetLength}.");
        var header = new Par2PacketHeader
        {
            Magic = headerBytes[..8],
            PacketLength = packetLength,
            PacketHash = headerBytes[16..32],
            RecoverySetID = headerBytes[32..48],
            PacketType = headerBytes[48..64],
        };
        var bodyLength = (int)packetLength - HeaderSize;
        var packetType = Encoding.ASCII.GetString(header.PacketType).TrimEnd('\0');
        var matchingSet = options.ExpectedRecoverySetId is null
            || options.ExpectedRecoverySetId == Convert.ToHexString(header.RecoverySetID);
        ValidateBodyLength(packetType, bodyLength, matchingSet ? options.ExpectedSliceSize : null);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        hasher.AppendData(headerBytes.AsSpan(32));
        IDisposable? retained = null;
        try
        {
            Par2Packet packet;
            if (matchingSet && packetType == RecvSlic.PacketType && options.RetainRecoveryPayload)
            {
                retained = options.Budget.Reserve(bodyLength + 512L);
                var exponent = new byte[4];
                await stream.ReadExactlyAsync(exponent, ct).ConfigureAwait(false);
                hasher.AppendData(exponent);
                var payload = new byte[bodyLength - 4];
                await ReadAndHashAsync(stream, payload, hasher, ct).ConfigureAwait(false);
                packet = new RecvSlic(header, BinaryPrimitives.ReadUInt32LittleEndian(exponent), payload);
            }
            else if (matchingSet && packetType is MainPacket.PacketType or FileDesc.PacketType or IfscPacket.PacketType or UniFileN.PacketType)
            {
                retained = options.Budget.Reserve(EstimateMetadataBytes(packetType, bodyLength));
                using var bodyReservation = options.Budget.Reserve(bodyLength + 32L);
                var body = new byte[bodyLength];
                await ReadAndHashAsync(stream, body, hasher, ct).ConfigureAwait(false);
                VerifyHash(hasher, header);
                packet = packetType switch
                {
                    MainPacket.PacketType => new MainPacket(header),
                    FileDesc.PacketType => new FileDesc(header),
                    IfscPacket.PacketType => new IfscPacket(header),
                    _ => new UniFileN(header),
                };
                packet.ParseBodyBytes(body);
                packet.MemoryReservation = retained;
                retained = null;
                return packet;
            }
            else
            {
                var scratch = ArrayPool<byte>.Shared.Rent(ScratchBytes);
                try
                {
                    var remaining = bodyLength;
                    while (remaining > 0)
                    {
                        var count = Math.Min(remaining, ScratchBytes);
                        await ReadAndHashAsync(stream, scratch.AsMemory(0, count), hasher, ct).ConfigureAwait(false);
                        remaining -= count;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(scratch);
                }
                packet = packetType == RecvSlic.PacketType ? new RecvSlic(header) : new Par2Packet(header);
            }

            VerifyHash(hasher, header);
            packet.MemoryReservation = retained;
            retained = null;
            return packet;
        }
        finally
        {
            retained?.Dispose();
        }
    }
#pragma warning restore CA5351

    private static void ValidateBodyLength(string packetType, int length, int? sliceSize)
    {
        var valid = packetType switch
        {
            MainPacket.PacketType => length >= 12 && (length - 12) % 16 == 0
                && (length - 12) / 16 <= MainPacket.MaxFileCount,
            FileDesc.PacketType => length >= 56 && length - 56 <= FileDesc.MaxFileNameBytes,
            IfscPacket.PacketType => length >= 16 && (length - 16) % 20 == 0
                && (length - 16) / 20 <= ReedSolomon.Gf16Field.MaxInputSlices,
            UniFileN.PacketType => length >= 16 && length - 16 <= FileDesc.MaxFileNameBytes,
            RecvSlic.PacketType => length >= 4 && length - 4 <= (long)MainPacket.MaxSliceSize
                && (sliceSize is null || length - 4 == sliceSize),
            _ => true,
        };
        if (!valid)
            throw new InvalidDataException($"Invalid {packetType} packet body length {length}.");
    }

    private static long EstimateMetadataBytes(string packetType, int length) => packetType switch
    {
        MainPacket.PacketType => 512L + (length - 12L) / 16 * 128,
        IfscPacket.PacketType => 512L + (length - 16L) / 20 * 128,
        _ => 512L + 8L * length,
    };

    private static async Task ReadAndHashAsync(Stream stream, Memory<byte> destination, IncrementalHash hasher, CancellationToken ct)
    {
        while (!destination.IsEmpty)
        {
            var count = await stream.ReadAsync(destination[..Math.Min(destination.Length, ScratchBytes)], ct).ConfigureAwait(false);
            if (count == 0)
                throw new EndOfStreamException("Truncated PAR2 packet body.");
            hasher.AppendData(destination.Span[..count]);
            destination = destination[count..];
        }
    }

    private static void VerifyHash(IncrementalHash hasher, Par2PacketHeader header)
    {
        if (!hasher.GetHashAndReset().AsSpan().SequenceEqual(header.PacketHash))
            throw new InvalidDataException("PAR2 packet MD5 hash mismatch.");
    }
}
