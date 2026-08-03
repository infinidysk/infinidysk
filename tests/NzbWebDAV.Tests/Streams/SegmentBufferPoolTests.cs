using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Streams;

public class SegmentBufferPoolTests
{
    [Theory]
    [InlineData(1, 256 * 1024)]
    [InlineData(256 * 1024, 256 * 1024)]
    [InlineData(256 * 1024 + 1, 512 * 1024)]
    [InlineData(750_000, 768 * 1024)]
    [InlineData(1024 * 1024, 1024 * 1024)]
    public void RoundToSizeClass_AlignsToBoundary(int input, int expected)
    {
        Assert.Equal(expected, SegmentBufferPool.RoundToSizeClass(input));
    }

    [Fact]
    public void Rent_ReturnsBufferOfAtLeastRequestedSize()
    {
        var pool = new SegmentBufferPool(maxIdleBytes: 64 * 1024 * 1024);
        var buffer = pool.Rent(700_000);
        Assert.True(buffer.Length >= 700_000);
        pool.Return(buffer);
    }

    [Fact]
    public void Return_ThenRent_ReusesBuffer()
    {
        var pool = new SegmentBufferPool(maxIdleBytes: 64 * 1024 * 1024);
        var first = pool.Rent(500_000);
        pool.Return(first);

        var second = pool.Rent(500_000);
        Assert.Same(first, second);
        pool.Return(second);
    }

    [Fact]
    public void IdleBytes_ReflectsRetainedBuffers()
    {
        var pool = new SegmentBufferPool(maxIdleBytes: 64 * 1024 * 1024);
        Assert.Equal(0, pool.IdleBytes);

        var buffer = pool.Rent(750_000);
        Assert.Equal(0, pool.IdleBytes);

        pool.Return(buffer);
        Assert.Equal(buffer.Length, pool.IdleBytes);
    }

    [Fact]
    public void Rent_ZeroLength_ReturnsEmptyArray()
    {
        var pool = new SegmentBufferPool(maxIdleBytes: 64 * 1024 * 1024);
        var buffer = pool.Rent(0);
        Assert.Empty(buffer);
    }

    [Fact]
    public void Return_EmptyArray_IsNoOp()
    {
        var pool = new SegmentBufferPool(maxIdleBytes: 64 * 1024 * 1024);
        pool.Return([]);
        Assert.Equal(0, pool.IdleBytes);
    }

    [Fact]
    public void MaxBuffersPerClass_PreventsUnboundedRetention()
    {
        var pool = new SegmentBufferPool(maxIdleBytes: 256 * 1024 * 1024L);
        var buffers = Enumerable.Range(0, 100)
            .Select(_ => pool.Rent(256 * 1024))
            .ToList();

        foreach (var b in buffers) pool.Return(b);

        // Pool caps at 64 buffers per class; excess is silently dropped.
        Assert.True(pool.IdleBytes <= 64 * 256 * 1024);
    }
}

public class BufferPoolDiagnosticsTests
{
    [Fact]
    public void RentAndReturn_UpdatesCounters()
    {
        BufferPoolDiagnostics.Reset();
        using var stream = new PooledBufferStream(1024);
        Assert.Equal(1, BufferPoolDiagnostics.Rents);
        Assert.True(BufferPoolDiagnostics.ActiveBytes > 0);

        stream.Dispose();
        Assert.Equal(1, BufferPoolDiagnostics.Returns);
    }

    [Fact]
    public void Growth_TrackedSeparately()
    {
        BufferPoolDiagnostics.Reset();
        using var stream = new PooledBufferStream(16);
        stream.Write(new byte[1024]);
        Assert.True(BufferPoolDiagnostics.Growths >= 1);
    }

    [Fact]
    public void Snapshot_CapturesCurrentState()
    {
        BufferPoolDiagnostics.Reset();
        using var stream = new PooledBufferStream(512);
        var snap = BufferPoolDiagnostics.Snapshot();
        Assert.Equal(1, snap.Rents);
        Assert.True(snap.ActiveBytes > 0);
    }
}

public class PooledBufferStreamPoolSwapTests
{
    [Fact]
    public async Task CustomPool_IsUsedForRentAndReturn()
    {
        var pool = new SegmentBufferPool(maxIdleBytes: 64 * 1024 * 1024);
        var previous = PooledBufferStream.Pool;
        PooledBufferStream.Pool = pool;

        try
        {
            await using var stream = new PooledBufferStream(750_000);
            await stream.WriteAsync(new byte[750_000]);
        }
        finally
        {
            PooledBufferStream.Pool = previous;
        }

        Assert.True(pool.IdleBytes > 0);
    }
}
