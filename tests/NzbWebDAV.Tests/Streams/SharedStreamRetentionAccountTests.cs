using NzbWebDAV.Services;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Streams;

[Collection(nameof(SharedStreamCollection))]
public sealed class SharedStreamRetentionAccountTests
{
    [Fact]
    public void Rent_AccountsArrayPoolBucketLengthNotRequestedMinimum()
    {
        var account = new SharedStreamRetentionAccount();
        var inner = new RecordingPool();
        var pool = new SharedStreamAccountingPool(SharedStreamBufferCategory.Ring, inner, account);

        var buffer = pool.Rent(8);
        try
        {
            Assert.True(buffer.Length >= 8);
            Assert.Equal(buffer.Length, account.Snapshot().RingRentedBytes);
            Assert.NotEqual(8, account.Snapshot().RingRentedBytes);
        }
        finally
        {
            pool.Return(buffer);
        }

        Assert.Equal(0, account.Snapshot().RingRentedBytes);
        Assert.Equal(buffer.Length, account.Snapshot().RingRentedBytesPeak);
    }

    [Fact]
    public void Rent_FromArrayPool_UsesBucketCapacity()
    {
        var account = new SharedStreamRetentionAccount();
        var pool = new SharedStreamAccountingPool(
            SharedStreamBufferCategory.Ring, SharedArrayPoolAdapter.Instance, account);
        var buffer = pool.Rent(1);
        try
        {
            Assert.True(buffer.Length >= 16, $"ArrayPool bucket was {buffer.Length}");
            Assert.Equal(buffer.Length, account.Snapshot().RingRentedBytes);
        }
        finally
        {
            pool.Return(buffer);
        }

        Assert.Equal(0, account.Snapshot().RingRentedBytes);
    }

    [Fact]
    public void CursorEviction_DecrementsAccountImmediately()
    {
        var account = new SharedStreamRetentionAccount();
        var inner = new RecordingPool();
        var pool = new SharedStreamAccountingPool(SharedStreamBufferCategory.Ring, inner, account);
        var ring = new SharedStreamRingBuffer(64, pool: pool, chunkSize: 8);
        ring.RegisterReader(1, 0);
        ring.Append("abcdefghijklmnop"u8); // two chunks

        var rentedAfterAppend = account.Snapshot().RingRentedBytes;
        Assert.True(rentedAfterAppend > 0);

        ring.AdvanceCursor(1, 8);
        ring.EvictThrough(8);

        Assert.True(account.Snapshot().RingRentedBytes < rentedAfterAppend);
        Assert.Equal(inner.Rents - inner.Returns, ring.ChunkCount);
        ring.ReleaseAll();
        Assert.Equal(0, account.Snapshot().RingRentedBytes);
        Assert.Equal(inner.Rents, inner.Returns);
    }

    [Fact]
    public void DoubleReturn_DoesNotGoNegativeOrReturnTwice()
    {
        var account = new SharedStreamRetentionAccount();
        var inner = new RecordingPool();
        var pool = new SharedStreamAccountingPool(SharedStreamBufferCategory.Ring, inner, account);

        var buffer = pool.Rent(8);
        pool.Return(buffer);
        pool.Return(buffer);

        Assert.Equal(0, account.Snapshot().RingRentedBytes);
        Assert.Equal(1, inner.Returns);
    }

    [Fact]
    public void PumpScratch_IsTrackedSeparatelyFromRing()
    {
        var account = new SharedStreamRetentionAccount();
        var inner = new RecordingPool();
        var ringPool = new SharedStreamAccountingPool(SharedStreamBufferCategory.Ring, inner, account);
        var scratchPool = new SharedStreamAccountingPool(SharedStreamBufferCategory.PumpScratch, inner, account);

        var ring = ringPool.Rent(8);
        var scratch = scratchPool.Rent(8);
        var snapshot = account.Snapshot();
        Assert.Equal(ring.Length, snapshot.RingRentedBytes);
        Assert.Equal(scratch.Length, snapshot.PumpScratchRentedBytes);

        ringPool.Return(ring);
        scratchPool.Return(scratch);
        snapshot = account.Snapshot();
        Assert.Equal(0, snapshot.RingRentedBytes);
        Assert.Equal(0, snapshot.PumpScratchRentedBytes);
    }

    [Fact]
    public void ConcurrentReadTrackerSnapshot_UsesExactRentedCapacity()
    {
        var account = new SharedStreamRetentionAccount();
        var inner = new RecordingPool();
        var pool = new SharedStreamAccountingPool(SharedStreamBufferCategory.Ring, inner, account);
        var tracker = new ConcurrentReadTracker(retentionAccount: account);

        var buffer = pool.Rent(8);
        tracker.UpdateSharedRingRetainedBytes(3);
        var snapshot = tracker.Snapshot();
        Assert.Equal(buffer.Length, snapshot.SharedStreamRingRetainedBytes);
        Assert.Equal(3, snapshot.SharedStreamRingLogicalBytes);

        pool.Return(buffer);
        snapshot = tracker.Snapshot();
        Assert.Equal(0, snapshot.SharedStreamRingRetainedBytes);
        Assert.Equal(3, snapshot.SharedStreamRingLogicalBytes);
    }

    private sealed class RecordingPool : ISegmentBufferPool
    {
        public int Rents;
        public int Returns;

        public byte[] Rent(int minimumLength)
        {
            Interlocked.Increment(ref Rents);
            return new byte[Math.Max(minimumLength, 16)];
        }

        public void Return(byte[] buffer) => Interlocked.Increment(ref Returns);
    }
}
