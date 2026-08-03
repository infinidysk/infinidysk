using System;
using System.IO;
using System.Threading.Tasks;
using SharpCompress.IO;
using SharpCompress.Test.Mocks;
using Xunit;

namespace SharpCompress.Test.Streams;

public class SharpCompressStreamRingBufferSpanTests
{
    private static byte[] MakeData(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(i & 0xFF);
        }
        return data;
    }

    [Fact]
    public void SpanRead_AfterRewind_MatchesArrayRead()
    {
        var data = MakeData(32);
        using var stream = SharpCompressStream.Create(
            new ForwardOnlyStream(new MemoryStream(data)),
            64
        );
        stream.StartRecording();

        var probe = new byte[16];
        Assert.Equal(16, stream.Read(probe, 0, 16));
        stream.Rewind();

        var arrayBuf = new byte[16];
        Assert.Equal(16, stream.Read(arrayBuf, 0, 16));
        stream.Rewind();

        Span<byte> spanBuf = stackalloc byte[16];
        Assert.Equal(16, stream.Read(spanBuf));
        Assert.Equal(arrayBuf, spanBuf.ToArray());
    }

    [Fact]
    public async Task ReadAsync_Memory_AfterRewind_MatchesArrayRead()
    {
        var data = MakeData(32);
        await using var stream = SharpCompressStream.Create(
            new ForwardOnlyStream(new MemoryStream(data)),
            64
        );
        stream.StartRecording();

        var probe = new byte[16];
        Assert.Equal(16, await stream.ReadAsync(probe.AsMemory(0, 16)));
        stream.Rewind();

        var arrayBuf = new byte[16];
        Assert.Equal(16, await stream.ReadAsync(arrayBuf, 0, 16));
        stream.Rewind();

        var memoryBuf = new byte[16];
        Assert.Equal(16, await stream.ReadAsync(memoryBuf.AsMemory()));
        Assert.Equal(arrayBuf, memoryBuf);
    }

    [Fact]
    public void SpanRead_SpanningReplayToLiveBoundary_MatchesArrayRead()
    {
        var data = MakeData(40);
        using var stream = SharpCompressStream.Create(
            new ForwardOnlyStream(new MemoryStream(data)),
            64
        );
        stream.StartRecording();

        var probe = new byte[10];
        Assert.Equal(10, stream.Read(probe, 0, 10));
        stream.Rewind();

        // Replay 6 from ring, then 10 fresh from stream → spans replay→live boundary
        var arrayBuf = new byte[16];
        Assert.Equal(16, stream.Read(arrayBuf, 0, 16));
        stream.Rewind();

        // Re-read probe so ring is filled again, then rewind for span path
        Assert.Equal(10, stream.Read(probe, 0, 10));
        stream.Rewind();

        Span<byte> spanBuf = stackalloc byte[16];
        Assert.Equal(16, stream.Read(spanBuf));
        Assert.Equal(arrayBuf, spanBuf.ToArray());
    }

    [Fact]
    public async Task CopyToAsync_MatchesFullContent()
    {
        var data = MakeData(100);
        await using var stream = SharpCompressStream.Create(
            new ForwardOnlyStream(new MemoryStream(data)),
            64
        );
        stream.StartRecording();
        var probe = new byte[20];
        Assert.Equal(20, await stream.ReadAsync(probe.AsMemory()));
        stream.Rewind();
        stream.FreezeAndReleaseBuffer();

        using var destination = new MemoryStream();
        await stream.CopyToAsync(destination);
        Assert.Equal(data, destination.ToArray());
    }

    [Fact]
    public void RingBuffer_SpanWriteAndReadFromEnd_MatchArrayOverloads()
    {
        using var buffer = new RingBuffer(8);
        var data = new byte[] { 1, 2, 3, 4, 5, 6 };
        buffer.Write(data.AsSpan());

        Span<byte> dest = stackalloc byte[4];
        Assert.Equal(4, buffer.ReadFromEnd(4, dest));
        Assert.Equal(new byte[] { 3, 4, 5, 6 }, dest.ToArray());
    }
}
