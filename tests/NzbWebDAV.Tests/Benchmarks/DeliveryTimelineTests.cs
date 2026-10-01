using System.Diagnostics;
using NzbWebDAV.Benchmarks;

namespace NzbWebDAV.Tests.Benchmarks;

public sealed class DeliveryTimelineTests
{
    private const long Start = 1_000_000;

    private static long At(double milliseconds) => Start + (long)(milliseconds * Stopwatch.Frequency / 1000d);

    [Fact]
    public void Summarize_SteadyDelivery_ReportsEqualPercentilesAndShortGaps()
    {
        var timeline = new DeliveryTimeline(Start);
        // 1 MB every 25 ms for 2 s = 40 MB/s.
        for (var i = 1; i <= 80; i++)
            timeline.Record(At(i * 25d), i * 1_000_000L);

        var summary = timeline.Summarize(TimeSpan.FromMilliseconds(250));

        Assert.Equal(40, summary.P05WindowThroughputMbps, precision: 3);
        Assert.Equal(40, summary.P50WindowThroughputMbps, precision: 3);
        Assert.Equal(25, summary.LongestReadGapMs, precision: 3);
        Assert.Equal(200, summary.TimeTo8MbMs, precision: 3);
        Assert.Equal(1600, summary.TimeTo64MbMs, precision: 3);
    }

    [Fact]
    public void Summarize_HeadOfLineBursts_ExposeStallsTheAverageHides()
    {
        var timeline = new DeliveryTimeline(Start);
        // Same 40 MB over 1 s as a steady stream, but delivered as 20 MB bursts after 500 ms stalls.
        timeline.Record(At(500), 20_000_000);
        timeline.Record(At(1000), 40_000_000);

        var summary = timeline.Summarize(TimeSpan.FromMilliseconds(250));

        Assert.Equal(0, summary.P05WindowThroughputMbps);
        Assert.Equal(500, summary.LongestReadGapMs, precision: 3);
        Assert.Equal(500, summary.TimeTo8MbMs, precision: 3);
        Assert.Equal(0, summary.TimeTo64MbMs);
    }

    [Fact]
    public void Summarize_TimeBeforeFirstByteCountsAsZeroRateWindows()
    {
        var timeline = new DeliveryTimeline(Start);
        timeline.Record(At(600), 1_000_000);
        timeline.Record(At(1000), 2_000_000);

        var rates = timeline.Summarize(TimeSpan.FromMilliseconds(250));

        Assert.Equal(0, rates.P05WindowThroughputMbps);
    }

    [Fact]
    public void Summarize_NoReads_ReturnsZeros()
    {
        var summary = new DeliveryTimeline(Start).Summarize();

        Assert.Equal(new DeliverySmoothness(0, 0, 0, 0, 0), summary);
    }

    [Fact]
    public void AddAndDivide_AverageRepetitionsAndTolerateMissingDelivery()
    {
        var first = new DeliverySmoothness(10, 20, 30, 40, 50);
        var second = new DeliverySmoothness(30, 40, 50, 60, 70);

        var total = DeliverySmoothness.Add(first, second)!;

        Assert.Equal(new DeliverySmoothness(20, 30, 40, 50, 60), DeliverySmoothness.Divide(total, 2));
        Assert.Same(first, DeliverySmoothness.Add(first, null));
        Assert.Null(DeliverySmoothness.Add(null, null));
    }
}
