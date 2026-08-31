using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Streams;

public class InitialBodyBatchPlanTests
{
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 2)]
    [InlineData(15, 1, 15)]
    [InlineData(16, 1, 16)]
    [InlineData(39, 2, 20)]
    [InlineData(40, 2, 20)]
    [InlineData(64, 3, 22)]
    [InlineData(80, 4, 20)]
    [InlineData(160, 4, 40)]
    public void SelectInitialBatchWidth_Target20Maximum4_MatchesRequiredTable(
        int segments,
        int expectedWidth,
        int expectedBatches)
    {
        var width = InitialBodyBatchPlan.SelectInitialBatchWidth(segments, 20, 4);

        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedBatches, InitialBodyBatchPlan.CountBatches(segments, width));
    }

    [Fact]
    public void SelectInitialBatchWidth_TargetOneUsesWidestAllowedWidth()
    {
        Assert.Equal(4, InitialBodyBatchPlan.SelectInitialBatchWidth(16, 1, 4));
    }

    [Fact]
    public void SelectInitialBatchWidth_TargetAboveWorkUsesWidthOne()
    {
        Assert.Equal(1, InitialBodyBatchPlan.SelectInitialBatchWidth(16, 20, 4));
    }

    [Fact]
    public void InvalidInputsThrowBeforePlanning()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => InitialBodyBatchPlan.SelectInitialBatchWidth(0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => InitialBodyBatchPlan.CountBatches(1, 0));
    }

    [Fact]
    public void WideningFloor_SaturatesWithoutOverflow()
    {
        Assert.Equal(
            int.MaxValue,
            InitialBodyBatchPlan.CalculateWideningObservationFloor(int.MaxValue, int.MaxValue));
    }

    [Fact]
    public void ArticleBuffer_ClampsEffectiveMaximum()
    {
        var plan = InitialBodyBatchPlan.Create(64, 64, 20, 8, 3);

        Assert.Equal(3, plan.ConfiguredMaximumBatchWidth);
        Assert.Equal(3, plan.InitialBatchWidth);
    }
}
