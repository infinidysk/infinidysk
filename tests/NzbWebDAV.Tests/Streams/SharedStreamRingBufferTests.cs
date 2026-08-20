using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Streams;

public class SharedStreamRingBufferTests
{
    [Fact]
    public void AppendAndCopy_CrossesChunkBoundaries()
    {
        var pool = new CountingBufferPool();
        var ring = CreateRing(pool, chunkSize: 8, ringSize: 64);
        ring.RegisterReader(1, 0);
        ring.Append("abcdefghijklmnop"u8); // 16 bytes → 2 chunks

        var dest = new byte[16];
        var result = ring.TryCopyAt(1, 0, dest);

        Assert.Equal(RingReadKind.Copied, result.Kind);
        Assert.Equal(16, result.Count);
        Assert.Equal("abcdefghijklmnop", System.Text.Encoding.ASCII.GetString(dest));
        Assert.Equal(2, pool.Rents);
        Assert.Equal(0, pool.Returns);
        Assert.Equal(16, ring.RetainedBytes);
        ring.ReleaseAll();
        Assert.Equal(pool.Rents, pool.Returns);
    }

    [Fact]
    public void EvictThrough_RetainsChunkWhenCursorSitsOnBoundary()
    {
        var pool = new CountingBufferPool();
        var ring = CreateRing(pool, chunkSize: 8, ringSize: 64);
        ring.RegisterReader(1, 0);
        ring.Append("abcdefghijklmnop"u8); // chunks [0,8) and [8,16)

        ring.AdvanceCursor(1, 8);
        ring.EvictThrough(8);

        // Chunk [0,8) has b=8 ≤ minCursor=8 → returned. Next chunk [8,16) is kept.
        Assert.Equal(1, pool.Returns);
        Assert.Equal(1, ring.ChunkCount);
        Assert.Equal(8, ring.TailStart);

        var dest = new byte[8];
        var result = ring.TryCopyAt(1, 8, dest);
        Assert.Equal(RingReadKind.Copied, result.Kind);
        Assert.Equal("ijklmnop", System.Text.Encoding.ASCII.GetString(dest));
        ring.ReleaseAll();
        Assert.Equal(pool.Rents, pool.Returns);
    }

    [Fact]
    public void EvictThrough_DoesNotEvictChunkUntilItsEndIsAtOrBehindMinCursor()
    {
        var pool = new CountingBufferPool();
        var ring = CreateRing(pool, chunkSize: 8, ringSize: 64);
        ring.RegisterReader(1, 0);
        ring.Append("abcdefgh"u8);

        ring.AdvanceCursor(1, 7);
        ring.EvictThrough(7);

        Assert.Equal(0, pool.Returns);
        Assert.Equal(1, ring.ChunkCount);
        ring.ReleaseAll();
    }

    [Fact]
    public async Task WaitForDataAsync_WakesOnAppend()
    {
        var ring = CreateRing(new CountingBufferPool(), chunkSize: 8, ringSize: 64);
        ring.RegisterReader(1, 0);
        var waiting = ring.WaitForDataAsync(1, 0, CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        ring.Append("abcd"u8);
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));

        var dest = new byte[4];
        Assert.Equal(RingReadKind.Copied, ring.TryCopyAt(1, 0, dest).Kind);
        ring.ReleaseAll();
    }

    [Fact]
    public async Task WaitForDataAsync_WakesOnComplete()
    {
        var ring = CreateRing(new CountingBufferPool(), chunkSize: 8, ringSize: 64);
        ring.RegisterReader(1, 0);
        var waiting = ring.WaitForDataAsync(1, 0, CancellationToken.None);
        ring.SetComplete();
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));

        var dest = new byte[1];
        var result = ring.TryCopyAt(1, 0, dest);
        Assert.Equal(RingReadKind.Copied, result.Kind);
        Assert.Equal(0, result.Count);
        ring.ReleaseAll();
    }

    [Fact]
    public async Task SetFailure_IsStickyAndDeliveredOncePerReader()
    {
        var ring = CreateRing(new CountingBufferPool(), chunkSize: 8, ringSize: 64);
        ring.RegisterReader(1, 0);
        ring.RegisterReader(2, 0);
        var waiting = ring.WaitForDataAsync(1, 0, CancellationToken.None);
        var boom = new IOException("pump failed");
        ring.SetFailure(boom);
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));

        var dest = new byte[1];
        var first = ring.TryCopyAt(1, 0, dest);
        Assert.Equal(RingReadKind.Failed, first.Kind);
        Assert.Same(boom, first.Exception);

        var second = ring.TryCopyAt(1, 0, dest);
        Assert.Equal(RingReadKind.Detached, second.Kind);

        var other = ring.TryCopyAt(2, 0, dest);
        Assert.Equal(RingReadKind.Failed, other.Kind);
        Assert.Same(boom, other.Exception);
        ring.ReleaseAll();
    }

    [Fact]
    public void ForceEvictBelow_ReturnsLaggardIdsAndReturnsChunks()
    {
        var pool = new CountingBufferPool();
        var ring = CreateRing(pool, chunkSize: 8, ringSize: 16);
        ring.RegisterReader(1, 0);
        ring.RegisterReader(2, 16);
        ring.Append("abcdefghijklmnopqrstuvwx"u8); // 24 bytes

        var evicted = ring.ForceEvictBelow(16);

        Assert.Contains(1L, evicted);
        Assert.DoesNotContain(2L, evicted);
        Assert.Equal(16, ring.TailStart);
        Assert.True(pool.Returns > 0);

        var dest = new byte[4];
        Assert.Equal(RingReadKind.Evicted, ring.TryCopyAt(1, 0, dest).Kind);
        Assert.Equal(RingReadKind.Copied, ring.TryCopyAt(2, 16, dest).Kind);
        ring.ReleaseAll();
        Assert.Equal(pool.Rents, pool.Returns);
    }

    [Fact]
    public void Failure_RetainsChunksUntilReleaseAll()
    {
        var pool = new CountingBufferPool();
        var ring = CreateRing(pool, chunkSize: 8, ringSize: 64);
        ring.RegisterReader(1, 0);
        ring.Append("abcdefgh"u8);
        ring.SetFailure(new IOException("x"));

        Assert.Equal(0, pool.Returns);
        Assert.Equal(8, ring.RetainedBytes);
        ring.ReleaseAll();
        Assert.Equal(pool.Rents, pool.Returns);
        Assert.Equal(0, ring.RetainedBytes);
    }

    [Fact]
    public async Task ReleaseAll_ConcurrentWithTryCopyAt_DoesNotUseReturnedArrays()
    {
        var pool = new PoisoningBufferPool();
        var ring = CreateRing(pool, chunkSize: 64, ringSize: 1024);
        var payload = Enumerable.Repeat((byte)0x11, 256).ToArray();
        ring.RegisterReader(1, 0);
        ring.Append(payload);

        var failures = 0;
        using var start = new ManualResetEventSlim(false);
        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            start.Wait();
            var dest = new byte[32];
            for (var i = 0; i < 2_000; i++)
            {
                var result = ring.TryCopyAt(1, 0, dest);
                if (result.Kind == RingReadKind.Copied && dest.AsSpan(0, result.Count).Contains(PoisoningBufferPool.Poison))
                    Interlocked.Increment(ref failures);
            }
        })).ToArray();

        var releaser = Task.Run(() =>
        {
            start.Set();
            Thread.SpinWait(1000);
            ring.ReleaseAll();
        });

        await Task.WhenAll([releaser, .. readers]);
        Assert.Equal(0, failures);

        var after = ring.TryCopyAt(1, 0, new byte[8]);
        Assert.Equal(RingReadKind.Released, after.Kind);
        Assert.Equal(pool.Rents, pool.Returns);
    }

    [Fact]
    public async Task WaitForDataAsync_CancelledReaderDoesNotAffectOthers()
    {
        var ring = CreateRing(new CountingBufferPool(), chunkSize: 8, ringSize: 64);
        ring.RegisterReader(1, 0);
        ring.RegisterReader(2, 0);
        using var cts = new CancellationTokenSource();
        var parked = ring.WaitForDataAsync(1, 0, cts.Token);
        var other = ring.WaitForDataAsync(2, 0, CancellationToken.None);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parked);

        Assert.False(other.IsCompleted);
        ring.Append("abcd"u8);
        await other.WaitAsync(TimeSpan.FromSeconds(2));
        ring.ReleaseAll();
    }

    private static SharedStreamRingBuffer CreateRing(
        ISegmentBufferPool pool, int chunkSize, long ringSize, long tailStart = 0) =>
        new(ringSize, tailStart, pool, chunkSize);

    private sealed class CountingBufferPool : ISegmentBufferPool
    {
        public int Rents;
        public int Returns;

        public byte[] Rent(int minimumLength)
        {
            Interlocked.Increment(ref Rents);
            return new byte[minimumLength];
        }

        public void Return(byte[] buffer)
        {
            Interlocked.Increment(ref Returns);
        }
    }

    private sealed class PoisoningBufferPool : ISegmentBufferPool
    {
        public const byte Poison = 0xA5;
        public int Rents;
        public int Returns;

        public byte[] Rent(int minimumLength)
        {
            Interlocked.Increment(ref Rents);
            return new byte[minimumLength];
        }

        public void Return(byte[] buffer)
        {
            Array.Fill(buffer, Poison);
            Interlocked.Increment(ref Returns);
        }
    }
}
