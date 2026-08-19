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
    public void RoundToSizeClass_AlignsToBoundary(int input, int expected) =>
        Assert.Equal(expected, SegmentBufferPool.RoundToSizeClass(input));

    [Fact]
    public void Return_StrictlyEnforcesIdleByteCap()
    {
        var pool = new SegmentBufferPool(maxIdleBytes: 512 * 1024);
        var buffers = Enumerable.Range(0, 3)
            .Select(_ => pool.Rent(256 * 1024))
            .ToArray();

        foreach (var buffer in buffers)
            pool.Return(buffer);

        var snapshot = pool.Snapshot();
        Assert.Equal(512 * 1024, snapshot.IdleBytes);
        Assert.Equal(256 * 1024, snapshot.TrimmedBytes);
        Assert.Equal(2, snapshot.SizeClasses.Single().BufferCount);
    }

    [Fact]
    public void Return_ReclaimsOldestBuffersAcrossSizeClasses()
    {
        var pool = new SegmentBufferPool(maxIdleBytes: 768 * 1024);
        var small = pool.Rent(256 * 1024);
        var medium = pool.Rent(512 * 1024);
        var large = pool.Rent(768 * 1024);
        pool.Return(small);
        pool.Return(medium);

        pool.Return(large);

        var snapshot = pool.Snapshot();
        Assert.Equal(768 * 1024, snapshot.IdleBytes);
        Assert.Equal(768 * 1024, snapshot.TrimmedBytes);
        Assert.Single(snapshot.SizeClasses);
        Assert.Equal(768 * 1024, snapshot.SizeClasses[0].BufferSize);
    }

    [Fact]
    public void Rent_TrimsStaleBuffersBeforeReuse()
    {
        var clock = new ManualTimeProvider();
        var pool = new SegmentBufferPool(
            maxIdleBytes: 4 * 1024 * 1024,
            staleAfter: TimeSpan.FromMinutes(1),
            timeProvider: clock);
        var first = pool.Rent(750_000);
        pool.Return(first);
        clock.Advance(TimeSpan.FromMinutes(2));

        var second = pool.Rent(750_000);

        Assert.NotSame(first, second);
        Assert.Equal(first.Length, pool.Snapshot().TrimmedBytes);
        pool.Return(second);
    }

    [Fact]
    public void Return_EnforcesPerClassLimit()
    {
        var pool = new SegmentBufferPool(
            maxIdleBytes: 16 * 1024 * 1024,
            maxBuffersPerClass: 2);
        var buffers = Enumerable.Range(0, 3)
            .Select(_ => pool.Rent(256 * 1024))
            .ToArray();

        foreach (var buffer in buffers)
            pool.Return(buffer);

        var snapshot = pool.Snapshot();
        Assert.Equal(2 * 256 * 1024, snapshot.IdleBytes);
        Assert.Equal(256 * 1024, snapshot.TrimmedBytes);
    }

    [Fact]
    public void Return_IgnoresAndCountsForeignOrDuplicateBuffers()
    {
        var pool = new SegmentBufferPool(maxIdleBytes: 1024 * 1024);
        var buffer = pool.Rent(256 * 1024);
        pool.Return(buffer);

        // A caller bug must not crash a stream, and the duplicate must not be
        // pooled a second time (that would hand one array to two renters).
        pool.Return(buffer);
        pool.Return(new byte[256 * 1024]);

        var snapshot = pool.Snapshot();
        Assert.Equal(2, snapshot.RejectedReturnCount);
        Assert.Equal(1, snapshot.ReturnCount);
        Assert.Equal(256 * 1024, snapshot.IdleBytes);
        Assert.Equal(1, snapshot.SizeClasses.Single().BufferCount);
    }

    [Fact]
    public void LeakedBuffer_IsNotRootedByThePool()
    {
        var pool = new SegmentBufferPool(maxIdleBytes: 1024 * 1024);
        var weakBuffer = RentAndDropBuffer(pool);

#pragma warning disable CA2001 // forced GC is the standard pattern for weak-reference leak tests
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
#pragma warning restore CA2001

        Assert.False(weakBuffer.IsAlive);
        // The leak stays visible in diagnostics even though nothing is rooted.
        Assert.Equal(256 * 1024, pool.Snapshot().CheckedOutBytes);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference RentAndDropBuffer(SegmentBufferPool pool)
    {
        return new WeakReference(pool.Rent(256 * 1024));
    }

    [Fact]
    public void Snapshot_AccountsForCheckedOutAndReusedBytes()
    {
        var pool = new SegmentBufferPool(maxIdleBytes: 4 * 1024 * 1024);
        var first = pool.Rent(750_000);
        Assert.Equal(first.Length, pool.Snapshot().CheckedOutBytes);
        pool.Return(first);

        var second = pool.Rent(750_000);
        var snapshot = pool.Snapshot();

        Assert.Same(first, second);
        Assert.Equal(2, snapshot.RentCount);
        Assert.Equal(1, snapshot.ReturnCount);
        Assert.Equal(1, snapshot.ReuseCount);
        Assert.Equal(1, snapshot.AllocationCount);
        pool.Return(second);
    }

    [Fact]
    public void MixedClassBurst_HonorsByteCapViaCrossClassEviction()
    {
        var pool = new SegmentBufferPool(maxIdleBytes: 1024 * 1024);
        var rented = new[]
        {
            pool.Rent(256 * 1024),
            pool.Rent(512 * 1024),
            pool.Rent(768 * 1024),
            pool.Rent(256 * 1024),
            pool.Rent(512 * 1024),
        };

        foreach (var buffer in rented)
            pool.Return(buffer);

        var snapshot = pool.Snapshot();
        Assert.True(snapshot.IdleBytes <= 1024 * 1024);
        Assert.True(snapshot.TrimmedBytes > 0);
        Assert.Equal(0, snapshot.CheckedOutBytes);
        Assert.Equal(snapshot.RentCount, snapshot.ReturnCount);
        Assert.Equal(snapshot.IdleBytes, snapshot.SizeClasses.Sum(c => c.IdleBytes));
    }

    [Fact]
    public void RepeatedSameSizeRentReturn_ReusesBuffers()
    {
        var pool = new SegmentBufferPool(maxIdleBytes: 4 * 1024 * 1024);
        byte[]? last = null;
        for (var i = 0; i < 50; i++)
        {
            var buffer = pool.Rent(750_000);
            if (last is not null)
                Assert.Same(last, buffer);
            last = buffer;
            pool.Return(buffer);
        }

        var snapshot = pool.Snapshot();
        Assert.Equal(50, snapshot.RentCount);
        Assert.Equal(50, snapshot.ReturnCount);
        Assert.Equal(49, snapshot.ReuseCount);
        Assert.Equal(1, snapshot.AllocationCount);
        Assert.Equal(0, snapshot.CheckedOutBytes);
        Assert.True(snapshot.ReuseCount / (double)snapshot.RentCount >= 0.9);
    }

    [Fact]
    public void Snapshot_PerClassAccountingUnderConcurrency_QuiescesWithBalancedRents()
    {
        var pool = new SegmentBufferPool(maxIdleBytes: 16 * 1024 * 1024);
        var sizes = new[] { 256 * 1024, 512 * 1024, 768 * 1024 };
        Parallel.For(0, 120, i =>
        {
            var buffer = pool.Rent(sizes[i % sizes.Length]);
            pool.Return(buffer);
        });

        var snapshot = pool.Snapshot();
        Assert.Equal(0, snapshot.CheckedOutBytes);
        Assert.Equal(snapshot.RentCount, snapshot.ReturnCount);
        Assert.Equal(snapshot.IdleBytes, snapshot.SizeClasses.Sum(c => c.IdleBytes));
        foreach (var sizeClass in snapshot.SizeClasses)
        {
            Assert.Equal((long)sizeClass.BufferSize * sizeClass.BufferCount, sizeClass.IdleBytes);
            Assert.True(sizeClass.BufferCount > 0);
        }
    }

    [Fact]
    public void TypicalSegment_UsesLessCapacityThanSharedArrayPoolBucket()
    {
        const int requested = 750_000;
        var custom = new SegmentBufferPool(maxIdleBytes: 4 * 1024 * 1024);
        var customBuffer = custom.Rent(requested);
        var sharedBuffer = SharedArrayPoolAdapter.Instance.Rent(requested);

        try
        {
            Assert.Equal(768 * 1024, customBuffer.Length);
            Assert.True(sharedBuffer.Length >= customBuffer.Length);
        }
        finally
        {
            custom.Return(customBuffer);
            SharedArrayPoolAdapter.Instance.Return(sharedBuffer);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}

public class BufferPoolDiagnosticsTests
{
    [Fact]
    public void GrowthAndDispose_KeepOwnershipAndWasteAccountingBalanced()
    {
        var diagnostics = new BufferPoolDiagnostics();
        var pool = new SegmentBufferPool(maxIdleBytes: 4 * 1024 * 1024);
        var stream = new PooledBufferStream(750_000, pool, diagnostics);

        var initial = diagnostics.Snapshot();
        Assert.Equal(1, initial.Rents);
        Assert.Equal(0, initial.Returns);
        Assert.Equal(768 * 1024, initial.CheckedOutBytes);
        Assert.Equal(750_000, initial.RequestedBytes);
        Assert.Equal(768 * 1024 - 750_000, initial.BucketWasteBytes);

        stream.Write(new byte[900_000]);
        var grown = diagnostics.Snapshot();
        Assert.Equal(2, grown.Rents);
        Assert.Equal(1, grown.Returns);
        Assert.Equal(1, grown.Growths);
        var expectedGrownCapacity = SegmentBufferPool.RoundToSizeClass(
            (int)Math.Max(900_000L, 768 * 1024 + (768 * 1024) / 2));
        Assert.Equal(expectedGrownCapacity, grown.CheckedOutBytes);

        stream.Dispose();
        var disposed = diagnostics.Snapshot();
        Assert.Equal(2, disposed.Returns);
        Assert.Equal(0, disposed.CheckedOutBytes);
    }

    [Fact]
    public void PooledBufferStream_ReturnsToThePoolThatRentedIt()
    {
        var pool = new SegmentBufferPool(maxIdleBytes: 4 * 1024 * 1024);
        using (var stream = new PooledBufferStream(750_000, pool))
            stream.Write(new byte[900_000]);

        var snapshot = pool.Snapshot();
        Assert.Equal(0, snapshot.CheckedOutBytes);
        Assert.Equal(2, snapshot.ReturnCount);
    }

    [Fact]
    public void GrowthFromEmpty_DoesNotRecordAFalseReturn()
    {
        var diagnostics = new BufferPoolDiagnostics();
        using var stream = new PooledBufferStream(
            0,
            SharedArrayPoolAdapter.Instance,
            diagnostics);

        stream.WriteByte(1);

        var snapshot = diagnostics.Snapshot();
        Assert.Equal(1, snapshot.Rents);
        Assert.Equal(0, snapshot.Returns);
        Assert.Equal(1, snapshot.Growths);
    }

    [Fact]
    public void PooledBufferStream_RejectsUndersizedPoolRent()
    {
        var pool = new UndersizedPool();

        Assert.Throws<InvalidOperationException>(
            () => new PooledBufferStream(1024, pool));
        Assert.Equal(1, pool.ReturnCount);
    }

    private sealed class UndersizedPool : ISegmentBufferPool
    {
        public int ReturnCount { get; private set; }
        public byte[] Rent(int minimumLength) => new byte[minimumLength - 1];
        public void Return(byte[] buffer) => ReturnCount++;
    }
}
