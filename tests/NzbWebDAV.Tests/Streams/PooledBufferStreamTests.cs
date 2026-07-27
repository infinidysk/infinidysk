using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Streams;

public class PooledBufferStreamTests
{
    [Fact]
    public async Task CopyToAsync_RoundTripsBytes()
    {
        var sourceBytes = Enumerable.Range(0, 1000).Select(i => (byte)(i % 256)).ToArray();
        await using var source = new MemoryStream(sourceBytes);
        await using var buffer = new PooledBufferStream(capacityHint: 64);
        await source.CopyToAsync(buffer);
        buffer.Position = 0;

        using var output = new MemoryStream();
        await buffer.CopyToAsync(output);
        Assert.Equal(sourceBytes, output.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(100)]
    public void Write_GrowsPastCapacityHint_PreservesData(int capacityHint)
    {
        var payload = Enumerable.Range(0, 100).Select(i => (byte)(i + 1)).ToArray();
        using var stream = new PooledBufferStream(capacityHint);
        stream.Write(payload);
        stream.Position = 0;

        var read = new byte[payload.Length];
        Assert.Equal(payload.Length, stream.Read(read));
        Assert.Equal(payload, read);
        Assert.Equal(payload.Length, stream.Length);
    }

    [Fact]
    public void SetLength_Growth_ExposesZerosIncludingTruncateThenGrow()
    {
        using var stream = new PooledBufferStream(16);
        stream.Write(Enumerable.Repeat((byte)0xAB, 16).ToArray());
        stream.SetLength(8);
        Assert.Equal(8, stream.Length);

        // Dirty the region beyond Length by writing into a rented-looking pattern:
        // grow again and require zeros in the newly exposed region.
        stream.SetLength(16);
        stream.Position = 8;
        var tail = new byte[8];
        Assert.Equal(8, stream.Read(tail));
        Assert.Equal(new byte[8], tail);
    }

    [Fact]
    public void SetLength_Growth_ClearsDirtyRentedRegion()
    {
        using var stream = new PooledBufferStream(32);
        // Force a rent, then truncate without clearing the physical array past Length.
        stream.Write(Enumerable.Repeat((byte)0xFF, 32).ToArray());
        stream.SetLength(0);
        stream.SetLength(32);
        stream.Position = 0;
        var bytes = new byte[32];
        Assert.Equal(32, stream.Read(bytes));
        Assert.Equal(new byte[32], bytes);
    }

    [Fact]
    public async Task SyncAndAsyncWrites_LandSameBytes()
    {
        var payload = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();

        using var sync = new PooledBufferStream(0);
        sync.Write(payload, 0, payload.Length);

        await using var asyncMemory = new PooledBufferStream(0);
        await asyncMemory.WriteAsync(payload.AsMemory());

        await using var asyncArray = new PooledBufferStream(0);
        await asyncArray.WriteAsync(payload, 0, payload.Length);

        sync.Position = 0;
        asyncMemory.Position = 0;
        asyncArray.Position = 0;
        var syncBytes = new byte[payload.Length];
        var asyncMemoryBytes = new byte[payload.Length];
        var asyncArrayBytes = new byte[payload.Length];
        Assert.Equal(payload.Length, sync.Read(syncBytes));
        Assert.Equal(payload.Length, asyncMemory.Read(asyncMemoryBytes));
        Assert.Equal(payload.Length, asyncArray.Read(asyncArrayBytes));
        Assert.Equal(syncBytes, asyncMemoryBytes);
        Assert.Equal(syncBytes, asyncArrayBytes);
        Assert.Equal(payload, syncBytes);
    }

    [Fact]
    public void Write_AfterSeekingPastEnd_ZeroesTheGap()
    {
        using var stream = new PooledBufferStream(16);
        stream.Write(Enumerable.Repeat((byte)0xFF, 16).ToArray());
        stream.SetLength(0);
        stream.Position = 8;
        stream.WriteByte(1);
        stream.Position = 0;

        var bytes = new byte[9];
        Assert.Equal(9, stream.Read(bytes));
        Assert.Equal(new byte[8], bytes[..8]);
        Assert.Equal(1, bytes[8]);
    }

    [Fact]
    public void SpanWrite_LandsSameBytes()
    {
        var payload = Enumerable.Range(0, 20).Select(i => (byte)(255 - i)).ToArray();
        using var stream = new PooledBufferStream(4);
        stream.Write(payload.AsSpan());
        stream.Position = 0;
        var read = new byte[payload.Length];
        Assert.Equal(payload.Length, stream.Read(read.AsSpan()));
        Assert.Equal(payload, read);
    }

    [Fact]
    public void DoubleDispose_IsSafe_AndUseAfterDisposeThrows()
    {
        var stream = new PooledBufferStream(8);
        stream.WriteByte(1);
        stream.Dispose();
        stream.Dispose();

        Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
        Assert.Throws<ObjectDisposedException>(() => stream.WriteByte(2));
        Assert.Throws<ObjectDisposedException>(() => _ = stream.Length);
    }
}
