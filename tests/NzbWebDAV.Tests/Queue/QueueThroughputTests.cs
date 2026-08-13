using NzbWebDAV.Queue;

namespace NzbWebDAV.Tests.Queue;

public class QueueThroughputTests
{
    private static readonly DateTime T0 = new(2026, 8, 12, 12, 0, 0, DateTimeKind.Utc);
    private const long TenMb = 10L * 1024 * 1024;

    [Fact]
    public void ComputeEta_ReturnsRemainingOverRate()
    {
        // 50% of 10 MiB left at 1 MiB/s → 5s
        var eta = QueueThroughput.ComputeEta(1024 * 1024, 50, TenMb);

        Assert.NotNull(eta);
        Assert.Equal(5, eta!.Value.TotalSeconds, 3);
    }

    [Fact]
    public void ComputeEta_ClearsWhenProgressReachesDownloadComplete()
    {
        Assert.Null(QueueThroughput.ComputeEta(1024 * 1024, 100, TenMb));
        Assert.Null(QueueThroughput.ComputeEta(1024 * 1024, 150, TenMb));
    }

    [Fact]
    public void ComputeEta_ClearsWhenRateIsZero()
    {
        Assert.Null(QueueThroughput.ComputeEta(0, 40, TenMb));
    }

    [Fact]
    public void ComputeEta_CapsAt24Hours()
    {
        var eta = QueueThroughput.ComputeEta(1, 0, 100L * 1024 * 1024 * 1024);

        Assert.Equal(QueueThroughput.MaxEta, eta!.Value);
    }

    [Fact]
    public void Update_IgnoresSamplesShorterThanMinimumInterval()
    {
        var previous = new QueueThroughput.SampleState(0, T0, 0);
        var next = QueueThroughput.Update(previous, 10, TenMb, T0.AddMilliseconds(200));

        Assert.Equal(previous, next);
    }

    [Fact]
    public void Update_ComputesInstantRateOnFirstSample()
    {
        var previous = new QueueThroughput.SampleState(0, T0, 0);
        var next = QueueThroughput.Update(previous, 10, TenMb, T0.AddSeconds(1));

        // 10% of 10 MiB in 1s → 1 MiB/s
        Assert.Equal(1024 * 1024, next.BytesPerSecond, 0);
        Assert.Equal(10, next.LastSampleProgress);
    }

    [Fact]
    public void Update_AppliesEmaSmoothing()
    {
        var first = QueueThroughput.Update(
            new QueueThroughput.SampleState(0, T0, 0),
            10,
            TenMb,
            T0.AddSeconds(1));
        var second = QueueThroughput.Update(first, 20, TenMb, T0.AddSeconds(2));

        var expected = QueueThroughput.EmaAlpha * (1024 * 1024) + (1 - QueueThroughput.EmaAlpha) * first.BytesPerSecond;
        Assert.Equal(expected, second.BytesPerSecond, 3);
    }

    [Fact]
    public void Update_DoesNotAdvanceSampleTimeWhenProgressUnchanged()
    {
        var first = QueueThroughput.Update(
            new QueueThroughput.SampleState(0, T0, 0),
            10,
            TenMb,
            T0.AddSeconds(1));
        var stalled = QueueThroughput.Update(first, 10, TenMb, T0.AddSeconds(3));

        Assert.Equal(first, stalled);

        var resumed = QueueThroughput.Update(stalled, 20, TenMb, T0.AddSeconds(4));
        // 10% of 10 MiB over 3s (stall included), not 1s after an advanced timestamp.
        var instant = TenMb * 0.10 / 3.0;
        var expected = QueueThroughput.EmaAlpha * instant + (1 - QueueThroughput.EmaAlpha) * first.BytesPerSecond;
        Assert.Equal(expected, resumed.BytesPerSecond, 3);
        Assert.Equal(T0.AddSeconds(4), resumed.LastSampleTime);
    }

    [Fact]
    public void Update_ClearsRateDuringHealthCheckPhase()
    {
        var previous = new QueueThroughput.SampleState(1024 * 1024, T0, 90);
        var next = QueueThroughput.Update(previous, 150, TenMb, T0.AddSeconds(1));

        Assert.Equal(0, next.BytesPerSecond);
        Assert.Equal(150, next.LastSampleProgress);
    }

    [Fact]
    public void FormatKbPerSec_UsesTwoDecimals()
    {
        Assert.Equal("0.00", QueueThroughput.FormatKbPerSec(0));
        Assert.Equal("1296.02", QueueThroughput.FormatKbPerSec(1296.02 * 1024));
    }

    [Fact]
    public void FormatSpeed_UsesSabHumanUnits()
    {
        Assert.Equal("0 ", QueueThroughput.FormatSpeed(0));
        Assert.Equal("1.3 M", QueueThroughput.FormatSpeed(1296.02 * 1024));
        Assert.Equal("500", QueueThroughput.FormatSpeed(500));
        Assert.Equal("2 K", QueueThroughput.FormatSpeed(2 * 1024));
    }
}
