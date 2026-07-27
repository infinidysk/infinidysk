using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Streams;

public class DrainBufferCapacityTests
{
    /// <summary>
    /// The estimate a drain gets is fileSize/segmentCount. Reproduce that
    /// arithmetic, because the bug lives in the gap between that average and a
    /// real full segment.
    /// </summary>
    private static long Estimate(int segmentCount, int fullSegment, int lastSegment)
    {
        var fileSize = (long)(segmentCount - 1) * fullSegment + lastSegment;
        return Math.Max(1, fileSize / segmentCount);
    }

    /// <summary>What MemoryStream ends up allocating for a given starting capacity.</summary>
    private static int CapacityAfterWriting(int initialCapacity, int payloadLength)
    {
        var buffer = new MemoryStream(initialCapacity);
        new MemoryStream(new byte[payloadLength]).CopyTo(buffer);
        return buffer.Capacity;
    }

    [Theory]
    [InlineData(5000, 716_800, 300_000)] // large file, ~700KB segments
    [InlineData(1000, 768_000, 12_345)]
    [InlineData(50, 384_000, 1)]
    [InlineData(17, 716_800, 1)] // worst case at the smallest n the headroom covers
    [InlineData(17, 720_897, 1)] // lands on a boundary; truncating the headroom would fall a byte short
    public void FullSegment_FitsWithoutDoubling(int segmentCount, int fullSegment, int lastSegment)
    {
        var estimate = Estimate(segmentCount, fullSegment, lastSegment);
        Assert.True(estimate < fullSegment, "the estimate must under-shoot a full segment, else this proves nothing");

        // Before: the raw estimate overflows and MemoryStream doubles.
        Assert.Equal(2 * (int)estimate, CapacityAfterWriting((int)estimate, fullSegment));

        // After: it fits.
        var capacity = MultiSegmentStream.DrainBufferCapacity(estimate, isExact: false);
        Assert.True(capacity >= fullSegment, $"capacity {capacity} < full segment {fullSegment}");
        Assert.Equal(capacity, CapacityAfterWriting(capacity, fullSegment));
    }

    [Fact]
    public void HeadroomIsSmall()
    {
        var estimate = Estimate(5000, 716_800, 300_000);
        var capacity = MultiSegmentStream.DrainBufferCapacity(estimate, isExact: false);

        // ~6.25%, versus the 100% the doubling cost.
        Assert.InRange(capacity, estimate, estimate + estimate / 8);
    }

    [Fact]
    public void ExactSize_GetsNoHeadroom()
    {
        // A recorded per-segment size is the real length; padding it would only
        // waste memory, and it cannot overflow.
        Assert.Equal(716_800, MultiSegmentStream.DrainBufferCapacity(716_800, isExact: true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData((long)int.MaxValue + 1)]
    public void UnusableEstimate_LetsMemoryStreamPickItsOwnCapacity(long estimate)
    {
        Assert.Equal(0, MultiSegmentStream.DrainBufferCapacity(estimate, isExact: false));
        Assert.Equal(0, MultiSegmentStream.DrainBufferCapacity(estimate, isExact: true));
    }

    [Fact]
    public void EstimateNearIntMax_DoesNotOverflow()
    {
        var capacity = MultiSegmentStream.DrainBufferCapacity(int.MaxValue, isExact: false);
        Assert.Equal(int.MaxValue, capacity);
    }
}
