using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Streams;

public class AdaptiveBodyBatchSizerTests
{
    [Fact]
    public void TwoNonAdjacentStarvedInEight_NarrowsFourToTwoThenTwoToOne()
    {
        var clock = new ManualTimeProvider();
        var sizer = new AdaptiveBodyBatchSizer(4, clock);
        // S-R-R-R-S-R-R-R → two starved in eight → 4→2
        ObservePattern(sizer, "SRRRSRRR");
        Assert.Equal(2, sizer.Current);

        // Same pattern again after window clear → 2→1
        ObservePattern(sizer, "SRRRSRRR");
        Assert.Equal(1, sizer.Current);
    }

    [Fact]
    public void OneStarvedInEight_DoesNotNarrow()
    {
        var sizer = new AdaptiveBodyBatchSizer(4);
        ObservePattern(sizer, "SRRRRRRR");
        Assert.Equal(4, sizer.Current);
    }

    [Fact]
    public void SixteenConsecutiveReady_RecoversOneStepAtATime()
    {
        var clock = new ManualTimeProvider();
        var sizer = new AdaptiveBodyBatchSizer(4, clock);
        ObservePattern(sizer, "SRRRSRRR"); // 4→2
        ObservePattern(sizer, "SRRRSRRR"); // 2→1
        Assert.Equal(1, sizer.Current);

        AdvancePastHold(clock);
        ObservePattern(sizer, new string('R', 16));
        Assert.Equal(2, sizer.Current);

        AdvancePastHold(clock);
        ObservePattern(sizer, new string('R', 16));
        Assert.Equal(4, sizer.Current);
    }

    [Fact]
    public void FifteenReadyThenStarved_DoesNotRecover()
    {
        var clock = new ManualTimeProvider();
        var sizer = new AdaptiveBodyBatchSizer(4, clock);
        ObservePattern(sizer, "SRRRSRRR"); // 4→2
        ObservePattern(sizer, new string('R', 15) + "S");
        Assert.Equal(2, sizer.Current);
    }

    [Fact]
    public void AlternatingObservations_ResizeOnlyOncePerClearedWindow()
    {
        var clock = new ManualTimeProvider();
        var sizer = new AdaptiveBodyBatchSizer(4, clock);
        // Alternating fills the window with four starved → one narrow, then clear.
        ObservePattern(sizer, "SRSRSRSR");
        Assert.Equal(2, sizer.Current);

        // Next eight alternating → one more narrow, not a flap within the window.
        ObservePattern(sizer, "SRSRSRSR");
        Assert.Equal(1, sizer.Current);

        // Further starvation at floor must not flap.
        ObservePattern(sizer, "SRSRSRSR");
        Assert.Equal(1, sizer.Current);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MaximumSizesStayWithinBounds(int maximum)
    {
        var clock = new ManualTimeProvider();
        var sizer = new AdaptiveBodyBatchSizer(maximum, clock);
        Assert.Equal(maximum, sizer.Current);

        // Drive starvation until the floor.
        for (var i = 0; i < 8; i++)
            ObservePattern(sizer, "SRRRSRRR");
        Assert.Equal(1, sizer.Current);

        // Recover past the configured maximum must clamp.
        for (var i = 0; i < 8; i++)
        {
            AdvancePastHold(clock);
            ObservePattern(sizer, new string('R', 16));
        }
        Assert.Equal(maximum, sizer.Current);
        Assert.InRange(sizer.Current, 1, maximum);
    }

    [Fact]
    public void ReadyBurstWithinHold_DoesNotRewiden()
    {
        var clock = new ManualTimeProvider();
        var sizer = new AdaptiveBodyBatchSizer(4, clock);
        ObservePattern(sizer, "SRRRSRRR"); // 4→2
        Assert.Equal(2, sizer.Current);

        ObservePattern(sizer, new string('R', 16));
        Assert.Equal(2, sizer.Current);

        ObservePattern(sizer, new string('R', 16));
        Assert.Equal(2, sizer.Current);

        AdvancePastHold(clock);
        ObservePattern(sizer, new string('R', 16));
        Assert.Equal(4, sizer.Current);
    }

    [Fact]
    public void NarrowsAreNotTimeDampened()
    {
        var clock = new ManualTimeProvider();
        var sizer = new AdaptiveBodyBatchSizer(4, clock);
        ObservePattern(sizer, "SRRRSRRR");
        Assert.Equal(2, sizer.Current);

        ObservePattern(sizer, "SRRRSRRR");
        Assert.Equal(1, sizer.Current);
    }

    [Fact]
    public void WideningLadderIsSpacedByHold()
    {
        var clock = new ManualTimeProvider();
        var sizer = new AdaptiveBodyBatchSizer(4, clock);
        ObservePattern(sizer, "SRRRSRRR"); // 4→2
        ObservePattern(sizer, "SRRRSRRR"); // 2→1
        Assert.Equal(1, sizer.Current);

        AdvancePastHold(clock);
        ObservePattern(sizer, new string('R', 16));
        Assert.Equal(2, sizer.Current);

        ObservePattern(sizer, new string('R', 16));
        Assert.Equal(2, sizer.Current);

        AdvancePastHold(clock);
        ObservePattern(sizer, new string('R', 16));
        Assert.Equal(4, sizer.Current);
    }

    [Fact]
    public void SuppressedWiden_KeepsStarvationWindowArmed()
    {
        var clock = new ManualTimeProvider();
        var sizer = new AdaptiveBodyBatchSizer(4, clock);
        ObservePattern(sizer, "SRRRSRRR"); // 4→2
        Assert.Equal(2, sizer.Current);

        ObservePattern(sizer, new string('R', 16)); // suppressed widen within hold
        Assert.Equal(2, sizer.Current);

        ObservePattern(sizer, "SRRRSRRR"); // narrows 2→1 immediately
        Assert.Equal(1, sizer.Current);
    }

    [Fact]
    public void FirstWidenAfterConstruction_NotSuppressed()
    {
        var clock = new ManualTimeProvider();
        var sizer = new AdaptiveBodyBatchSizer(4, clock);
        ObservePattern(sizer, "SRRRSRRR"); // 4→2
        Assert.Equal(2, sizer.Current);

        AdvancePastHold(clock);
        ObservePattern(sizer, new string('R', 16));
        Assert.Equal(4, sizer.Current);
    }

    private static void AdvancePastHold(ManualTimeProvider clock) =>
        clock.Advance(TimeSpan.FromMilliseconds(AdaptiveBodyBatchSizer.RewidenHoldMilliseconds + 50));

    private static void ObservePattern(AdaptiveBodyBatchSizer sizer, string pattern)
    {
        foreach (var c in pattern)
        {
            var ready = c is 'R' or 'r';
            sizer.Observe(ready);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
