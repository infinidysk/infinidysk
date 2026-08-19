using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Par2Recovery.Packets;
using NzbWebDAV.Par2Recovery.ReedSolomon;

namespace NzbWebDAV.Tests.Par2Recovery;

public sealed class Par2PacketParsingTests
{
    [Fact]
    public async Task ReadVerifiedPacket_MainAndIfsc_ParseCorrectly()
    {
        var data = new byte[5000];
        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)(i * 3);

        const ulong sliceSize = 4096;
        var (_, volume) = Par2TestEncoder.EncodeSet("test.bin", data, sliceSize, [0u, 1u]);

        await using var stream = new MemoryStream(volume);
        var main = await ReadUntilTypeAsync<MainPacket>(stream);
        Assert.Equal(sliceSize, main.SliceSize);
        Assert.Equal(1u, main.RecoverySetFileCount);

        var ifsc = await ReadUntilTypeAsync<IfscPacket>(stream);
        Assert.Equal(2, ifsc.Slices.Count);
    }

    [Fact]
    public async Task ReadVerifiedPacket_RecvSlicPayload_ReadsExponentAndData()
    {
        var data = new byte[64];
        Random.Shared.NextBytes(data);
        const ulong sliceSize = 4096;
        var (_, volume) = Par2TestEncoder.EncodeSet("f.bin", data, sliceSize, [0u]);

        await using var stream = new MemoryStream(volume);
        Par2Packet? packet;
        do
        {
            packet = await Par2RepairReader.ReadVerifiedPacketAsync(stream, readRecvSlicPayload: true, CancellationToken.None);
        } while (packet is not RecvSlic);

        var recv = (RecvSlic)packet;
        Assert.Equal(0u, recv.Exponent);
        Assert.Equal((int)sliceSize, recv.Payload.Length);
    }

    [Fact]
    public async Task ReadVerifiedPacket_CorruptedHash_Throws()
    {
        var data = new byte[32];
        var (_, volume) = Par2TestEncoder.EncodeSet("f.bin", data, 4096, [0u]);
        volume[25] ^= 0xFF;

        await using var stream = new MemoryStream(volume);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Par2RepairReader.ReadVerifiedPacketAsync(stream, readRecvSlicPayload: false, CancellationToken.None));
    }

    private static async Task<T> ReadUntilTypeAsync<T>(Stream stream) where T : Par2Packet
    {
        while (stream.Position < stream.Length)
        {
            var packet = await Par2RepairReader.ReadVerifiedPacketAsync(stream, readRecvSlicPayload: false, CancellationToken.None);
            if (packet is T typed)
                return typed;
        }

        throw new InvalidOperationException($"No {typeof(T).Name} in stream.");
    }
}
