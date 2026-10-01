using System.Diagnostics;

namespace NzbWebDAV.Benchmarks;

/// <summary>
/// Records when bytes reach the consumer so a run reports delivery steadiness, not only
/// its average rate. Ordered head-of-line stalls are invisible in wall time but show up
/// here as a long read gap and a low windowed percentile.
/// </summary>
internal sealed class DeliveryTimeline(long startTimestamp, int expectedReads = 0)
{
    internal static readonly TimeSpan DefaultWindow = TimeSpan.FromMilliseconds(250);
    private const long EightMb = 8_000_000;
    private const long SixtyFourMb = 64_000_000;

    private readonly List<(long Timestamp, long CumulativeBytes)> _points = new(Math.Max(0, expectedReads));

    public void Record(long timestamp, long cumulativeBytes) => _points.Add((timestamp, cumulativeBytes));

    public DeliverySmoothness Summarize() => Summarize(DefaultWindow);

    public DeliverySmoothness Summarize(TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        if (_points.Count == 0)
            return new DeliverySmoothness(0, 0, 0, 0, 0);

        var longestGapMs = 0d;
        for (var i = 1; i < _points.Count; i++)
            longestGapMs = Math.Max(longestGapMs, ToMs(_points[i].Timestamp - _points[i - 1].Timestamp));

        var rates = WindowRatesMbps(window);
        rates.Sort();
        return new DeliverySmoothness(
            TimeToBytesMs(EightMb),
            TimeToBytesMs(SixtyFourMb),
            longestGapMs,
            Percentile(rates, 0.05),
            Percentile(rates, 0.50));
    }

    private double TimeToBytesMs(long bytes)
    {
        foreach (var (timestamp, cumulative) in _points)
        {
            if (cumulative >= bytes)
                return ToMs(timestamp - startTimestamp);
        }

        return 0;
    }

    // Windows start at the request, so time before the first byte counts as zero-rate windows.
    private List<double> WindowRatesMbps(TimeSpan window)
    {
        var windowTicks = (long)(window.TotalSeconds * Stopwatch.Frequency);
        var end = _points[^1].Timestamp;
        var rates = new List<double>();
        var index = 0;
        var previousBytes = 0L;
        for (var windowEnd = startTimestamp + windowTicks; ; windowEnd += windowTicks)
        {
            var isLast = windowEnd >= end;
            var boundary = isLast ? end : windowEnd;
            var bytesAtBoundary = previousBytes;
            while (index < _points.Count && _points[index].Timestamp <= boundary)
                bytesAtBoundary = _points[index++].CumulativeBytes;

            var spanTicks = boundary - (windowEnd - windowTicks);
            // A short trailing window would report a misleading rate from a few reads.
            if (!isLast || spanTicks * 2 >= windowTicks || rates.Count == 0)
            {
                var seconds = Math.Max(spanTicks, 1) / (double)Stopwatch.Frequency;
                rates.Add((bytesAtBoundary - previousBytes) / seconds / 1_000_000d);
            }

            previousBytes = bytesAtBoundary;
            if (isLast)
                return rates;
        }
    }

    private static double Percentile(List<double> sorted, double quantile) =>
        sorted.Count == 0 ? 0 : sorted[(int)Math.Floor(quantile * (sorted.Count - 1))];

    private static double ToMs(long ticks) => ticks * 1000d / Stopwatch.Frequency;
}

internal sealed record DeliverySmoothness(
    double TimeTo8MbMs,
    double TimeTo64MbMs,
    double LongestReadGapMs,
    double P05WindowThroughputMbps,
    double P50WindowThroughputMbps)
{
    public static DeliverySmoothness? Add(DeliverySmoothness? left, DeliverySmoothness? right) =>
        left is null || right is null
            ? left ?? right
            : new(
                left.TimeTo8MbMs + right.TimeTo8MbMs,
                left.TimeTo64MbMs + right.TimeTo64MbMs,
                left.LongestReadGapMs + right.LongestReadGapMs,
                left.P05WindowThroughputMbps + right.P05WindowThroughputMbps,
                left.P50WindowThroughputMbps + right.P50WindowThroughputMbps);

    public static DeliverySmoothness Divide(DeliverySmoothness value, int divisor) => new(
        value.TimeTo8MbMs / divisor,
        value.TimeTo64MbMs / divisor,
        value.LongestReadGapMs / divisor,
        value.P05WindowThroughputMbps / divisor,
        value.P50WindowThroughputMbps / divisor);
}
