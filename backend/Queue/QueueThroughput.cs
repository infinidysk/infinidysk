using System.Globalization;

namespace NzbWebDAV.Queue;

/// <summary>
/// EMA-smoothed queue throughput and SAB-shaped speed/ETA formatting.
/// Rate is derived from progress-percentage deltas, not raw NNTP bytes.
/// </summary>
internal static class QueueThroughput
{
    internal const double EmaAlpha = 0.4;
    internal static readonly TimeSpan MinSampleInterval = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan MaxEta = TimeSpan.FromDays(1);

    internal readonly record struct SampleState(
        double BytesPerSecond,
        DateTime LastSampleTime,
        int LastSampleProgress);

    internal static SampleState Update(
        SampleState previous,
        int progress,
        long totalSegmentBytes,
        DateTime now)
    {
        if (progress > 100)
            return previous with { BytesPerSecond = 0, LastSampleProgress = progress };

        var elapsed = now - previous.LastSampleTime;
        if (elapsed < MinSampleInterval)
            return previous;

        var clamped = Math.Clamp(progress, 0, 100);
        var prevClamped = Math.Clamp(previous.LastSampleProgress, 0, 100);
        var progressDelta = clamped - prevClamped;
        if (progressDelta <= 0)
            return previous with { LastSampleTime = now, LastSampleProgress = clamped };

        var seconds = Math.Max(elapsed.TotalSeconds, MinSampleInterval.TotalSeconds);
        var deltaBytes = progressDelta / 100.0 * totalSegmentBytes;
        var instantRate = deltaBytes / seconds;
        var rate = previous.BytesPerSecond <= 0
            ? instantRate
            : EmaAlpha * instantRate + (1 - EmaAlpha) * previous.BytesPerSecond;

        return new SampleState(rate, now, clamped);
    }

    internal static TimeSpan? ComputeEta(
        double bytesPerSecond,
        int progressPercentage,
        long totalSegmentBytes)
    {
        if (bytesPerSecond <= 0 || progressPercentage >= 100)
            return null;

        var pct = Math.Clamp(progressPercentage, 0, 100);
        var remainingBytes = (100 - pct) / 100.0 * totalSegmentBytes;
        return FromRemaining(bytesPerSecond, remainingBytes);
    }

    internal static TimeSpan? FromRemaining(double bytesPerSecond, double remainingBytes)
    {
        if (bytesPerSecond <= 0 || remainingBytes <= 0)
            return null;

        var seconds = remainingBytes / bytesPerSecond;
        return seconds > MaxEta.TotalSeconds ? MaxEta : TimeSpan.FromSeconds(seconds);
    }

    internal static string FormatKbPerSec(double bytesPerSecond)
    {
        var kb = Math.Max(0, bytesPerSecond) / 1024.0;
        return kb.ToString("0.00", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// SABnzbd <c>to_units</c> style without a byte postfix: <c>"1.3 M"</c>, idle <c>"0 "</c>.
    /// </summary>
    internal static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
            return "0 ";

        var val = bytesPerSecond;
        var n = val < 1024 ? 0 : Math.Min(5, (int)Math.Truncate(Math.Log2(val) / 10));
        val /= Math.Pow(2, 10 * n);
        var decimals = n > 1 ? 1 : 0;
        ReadOnlySpan<string> units = ["", "K", "M", "G", "T", "P"];
        var number = val.ToString(decimals == 0 ? "0" : "0.0", CultureInfo.InvariantCulture);
        return n == 0 ? number : $"{number} {units[n]}";
    }
}
