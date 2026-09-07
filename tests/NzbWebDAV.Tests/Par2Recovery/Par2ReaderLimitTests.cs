using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Par2Recovery.Packets;

namespace NzbWebDAV.Tests.Par2Recovery;

public sealed class Par2ReaderLimitTests
{
    [Theory]
    [InlineData(MainPacket.PacketType, 16)]
    [InlineData(FileDesc.PacketType, 52)]
    [InlineData(FileDesc.PacketType, 100060)]
    [InlineData(IfscPacket.PacketType, 20)]
    [InlineData(IfscPacket.PacketType, 655396)]
    public async Task Reader_RejectsStructuralLengthsBeforeReadingBody(string type, int length)
    {
        await using var stream = new MemoryStream(Packet(type, new byte[length]));

        await Assert.ThrowsAsync<InvalidDataException>(() => Par2RepairReader.ReadVerifiedPacketAsync(stream, false, CancellationToken.None));
        Assert.Equal(64, stream.Position);
    }

    [Fact]
    public async Task Reader_ReservesMetadataBeforeBodyReadAndReleasesOnFailure()
    {
        var body = new byte[16 + 20 * 100];
        var budget = new Par2MemoryBudget(Par2RepairReader.ScratchBytes + 2048);
        await using var stream = new MemoryStream(Packet(IfscPacket.PacketType, body));

        await Assert.ThrowsAsync<Par2BudgetExceededException>(() => Par2RepairReader.ReadVerifiedPacketAsync(
            stream, new Par2RepairReader.ReadOptions(budget, false), CancellationToken.None));

        Assert.Equal(64, stream.Position);
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public async Task Reader_ForeignRecoveryIsHashedWithoutPayloadReservation()
    {
        var budget = new Par2MemoryBudget(Par2RepairReader.ScratchBytes + 1024);
        await using var stream = new MemoryStream(Packet(RecvSlic.PacketType, new byte[128 * 1024 + 4]));

        var packet = Assert.IsType<RecvSlic>(await Par2RepairReader.ReadVerifiedPacketAsync(
            stream, new Par2RepairReader.ReadOptions(budget, true, new string('F', 32), 4096), CancellationToken.None));

        Assert.Empty(packet.Payload);
        Assert.Equal(0, budget.ReservedBytes);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    public async Task Main_RejectsMissingOrDuplicateRecoverableIds(uint count, int entries)
    {
        var body = new byte[12 + 16 * entries];
        BinaryPrimitives.WriteUInt64LittleEndian(body, 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), count);
        await using var stream = new MemoryStream(Packet(MainPacket.PacketType, body));

        await Assert.ThrowsAsync<InvalidDataException>(() => Par2RepairReader.ReadVerifiedPacketAsync(stream, false, CancellationToken.None));
    }

    [Fact]
    public async Task Main_DoesNotIndexNonRecoveryIds()
    {
        var body = new byte[44];
        BinaryPrimitives.WriteUInt64LittleEndian(body, 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), 1);
        body[28] = 1;
        await using var stream = new MemoryStream(Packet(MainPacket.PacketType, body));

        var packet = Assert.IsType<MainPacket>(await Par2RepairReader.ReadVerifiedPacketAsync(stream, false, CancellationToken.None));

        Assert.Single(packet.FileIds);
    }

    [Fact]
    public async Task Reader_BudgetAtExactRequirementRetainsOnlyFinalPayload()
    {
        const int payloadSize = 4096;
        const int requirement = Par2RepairReader.ScratchBytes + 1024 + payloadSize + 4 + 512;
        var bytes = Packet(RecvSlic.PacketType, new byte[payloadSize + 4]);
        var budget = new Par2MemoryBudget(requirement);
        await using var stream = new MemoryStream(bytes);
        var packet = Assert.IsType<RecvSlic>(await Par2RepairReader.ReadVerifiedPacketAsync(stream, new Par2RepairReader.ReadOptions(budget, true), CancellationToken.None));
        Assert.Equal(payloadSize, packet.Payload.Length);
        Assert.Equal(requirement, budget.PeakBytes);
        packet.ReleaseMemory();
        Assert.Equal(0, budget.ReservedBytes);
        await using var rejected = new MemoryStream(bytes);
        await Assert.ThrowsAsync<Par2BudgetExceededException>(() => Par2RepairReader.ReadVerifiedPacketAsync(
            rejected, new Par2RepairReader.ReadOptions(new Par2MemoryBudget(requirement - 1), true), CancellationToken.None));
    }

#pragma warning disable CA5351 // PAR2 packet integrity uses MD5.
    internal static byte[] Packet(string type, byte[] body, byte[]? setId = null)
    {
        var packet = new byte[64 + body.Length];
        "PAR2\0PKT"u8.CopyTo(packet);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(8), (ulong)packet.Length);
        (setId ?? new byte[16]).CopyTo(packet, 32);
        Encoding.ASCII.GetBytes(type).CopyTo(packet, 48);
        body.CopyTo(packet, 64);
        MD5.HashData(packet.AsSpan(32)).CopyTo(packet, 16);
        return packet;
    }
#pragma warning restore CA5351
}