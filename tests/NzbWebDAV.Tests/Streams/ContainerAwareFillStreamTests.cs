using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Streams;

public sealed class ContainerAwareFillStreamTests
{
    [Theory]
    [InlineData(6, new byte[] { 0xEC, 0x80, 0xEC, 0x80, 0xEC, 0x80 })]
    [InlineData(7, new byte[] { 0xEC, 0x81, 0x00, 0xEC, 0x80, 0xEC, 0x80 })]
    public async Task MatroskaFill_TilesExactLengthWithVoidElements(long length, byte[] expected)
    {
        await using var stream = ContainerAwareFillStream.Create("movie.mkv", length, fileOffset: null);

        var output = await ReadWithSmallBufferAsync(stream);

        Assert.Equal(expected, output);
    }

    [Fact]
    public async Task TransportStreamFill_EmitsOnlyCompleteAlignedNullPackets()
    {
        await using var stream = ContainerAwareFillStream.Create("video.ts", 400, fileOffset: 10);

        var output = await ReadWithSmallBufferAsync(stream);

        Assert.All(output[..178], value => Assert.Equal(0xFF, value));
        Assert.Equal(new byte[] { 0x47, 0x1F, 0xFF, 0x10 }, output[178..182]);
        Assert.All(output[182..366], value => Assert.Equal(0xFF, value));
        Assert.All(output[366..], value => Assert.Equal(0xFF, value));
    }

    [Fact]
    public async Task M2tsFill_PreservesArrivalPrefixBeforeNullPacket()
    {
        await using var stream = ContainerAwareFillStream.Create("video.m2ts", 384, fileOffset: 0);

        var output = await ReadWithSmallBufferAsync(stream);

        Assert.Equal(new byte[4], output[..4]);
        Assert.Equal(new byte[] { 0x47, 0x1F, 0xFF, 0x10 }, output[4..8]);
        Assert.Equal(new byte[4], output[192..196]);
        Assert.Equal(new byte[] { 0x47, 0x1F, 0xFF, 0x10 }, output[196..200]);
    }

    [Theory]
    [InlineData("movie.mp4", 16)]
    [InlineData("movie.avi", 16)]
    [InlineData("movie.mkv", 1)]
    public async Task UnsupportedOrTinyFill_RetainsZeroFill(string fileName, long length)
    {
        await using var stream = ContainerAwareFillStream.Create(fileName, length, fileOffset: 0);

        var output = await ReadWithSmallBufferAsync(stream);

        Assert.Equal(new byte[(int)length], output);
    }

    [Fact]
    public async Task TransportStreamWithoutExactOffset_RetainsZeroFill()
    {
        await using var stream = ContainerAwareFillStream.Create("video.ts", 188, fileOffset: null);

        var output = await ReadWithSmallBufferAsync(stream);

        Assert.Equal(new byte[188], output);
    }

    private static async Task<byte[]> ReadWithSmallBufferAsync(Stream stream)
    {
        using var output = new MemoryStream();
        var buffer = new byte[3];
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) return output.ToArray();
            await output.WriteAsync(buffer.AsMemory(0, read));
        }
    }
}
