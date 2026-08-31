using NzbWebDAV.Models;

namespace NzbWebDAV.Tests.Streams;

public class FiniteRangeSegmentPlanTests
{
    private static readonly LongRange[] Ranges =
    [
        new(0, 3),
        new(3, 10),
        new(10, 14),
        new(14, 25),
    ];

    [Fact]
    public void ClosedRange_InclusiveEndMapsToHalfOpenLastByte()
    {
        var created = FiniteRangeSegmentPlan.TryCreate(
            Ranges, 1, rangeStart: 3, readBudget: 7, fileSize: 25, out var plan, out var reason);

        Assert.True(created);
        Assert.Equal(FiniteRangePlanUnavailableReason.None, reason);
        Assert.Equal(1, plan.FirstSegmentIndex);
        Assert.Equal(1, plan.SegmentCount);
        Assert.Equal(0, plan.PrefixBytes);
        Assert.Equal(0, plan.FinalSegmentSlackBytes);
    }

    [Fact]
    public void OneByteAtSegmentBoundarySelectsOnlyNextSegment()
    {
        Assert.True(FiniteRangeSegmentPlan.TryCreate(Ranges, 1, 3, 1, 25, out var plan, out _));

        Assert.Equal(1, plan.FirstSegmentIndex);
        Assert.Equal(1, plan.SegmentCount);
        Assert.Equal(6, plan.FinalSegmentSlackBytes);
    }

    [Fact]
    public void RangeAcrossIrregularSegmentsSelectsMinimalContiguousSlice()
    {
        Assert.True(FiniteRangeSegmentPlan.TryCreate(Ranges, 1, 6, 12, 25, out var plan, out _));

        Assert.Equal(1, plan.FirstSegmentIndex);
        Assert.Equal(3, plan.SegmentCount);
        Assert.Equal(3, plan.PrefixBytes);
        Assert.Equal(7, plan.FinalSegmentSlackBytes);
        Assert.Equal(12, plan.RequestedBytes);
    }

    [Fact]
    public void FirstHeadPartitionUsesVisibleTailNotWholeFirstSegment()
    {
        Assert.True(FiniteRangeSegmentPlan.TryCreate(Ranges, 1, 6, 10, 25, out var plan, out _));

        Assert.Equal(4, plan.HeadContributionBytes);
        Assert.Equal(6, plan.RemainderBudget);
        Assert.Equal(2, plan.RemainderSegmentCount);
    }

    [Fact]
    public void FirstHeadSatisfiesRangeCreatesNoRemainder()
    {
        Assert.True(FiniteRangeSegmentPlan.TryCreate(Ranges, 1, 6, 2, 25, out var plan, out _));

        Assert.False(plan.HasBufferedRemainder);
        Assert.Equal(0, plan.RemainderSegmentCount);
    }

    [Theory]
    [InlineData(0, FiniteRangePlanUnavailableReason.ZeroBudget)]
    [InlineData(-1, FiniteRangePlanUnavailableReason.ZeroBudget)]
    public void NonPositiveBudgetIsIneligible(long budget, FiniteRangePlanUnavailableReason expected)
    {
        Assert.False(FiniteRangeSegmentPlan.TryCreate(Ranges, 0, 0, budget, 25, out _, out var reason));
        Assert.Equal(expected, reason);
    }

    [Fact]
    public void StartPlusBudgetOverflowIsIneligible()
    {
        Assert.False(FiniteRangeSegmentPlan.TryCreate(
            [new LongRange(0, long.MaxValue)], 0, long.MaxValue - 1, 2, long.MaxValue, out _, out var reason));

        Assert.Equal(FiniteRangePlanUnavailableReason.ArithmeticOverflow, reason);
    }

    [Fact]
    public void InvalidRangesAreIneligibleWithoutEstimateFallback()
    {
        Assert.False(FiniteRangeSegmentPlan.TryCreate(
            [new LongRange(0, 3), new LongRange(4, 25)], 0, 0, 1, 25, out _, out var reason));

        Assert.Equal(FiniteRangePlanUnavailableReason.InvalidRanges, reason);
    }
}
