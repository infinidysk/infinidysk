using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Streams;

public sealed class ContainerAwareFillStreamTests
{
    [Fact]
    public async Task TransportStreamFill_EmitsOnlyCompleteAlignedNullPackets()
    {
        await using var stream = ContainerAwareFillStream.Create("video.ts", 400, fileOffset: 10);

        var output = await ReadWithSmallBufferAsync(stream);

        Assert.All(output[..178], value => Assert.Equal(0x00, value));
        Assert.Equal(new byte[] { 0x47, 0x1F, 0xFF, 0x10 }, output[178..182]);
        Assert.All(output[182..366], value => Assert.Equal(0xFF, value));
        Assert.All(output[366..], value => Assert.Equal(0x00, value));
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
    [InlineData("movie.mkv", 16)]
    public async Task UnsupportedFill_RetainsZeroFill(string fileName, long length)
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
