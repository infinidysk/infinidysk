namespace NzbWebDAV.Services;

internal static class ArrHealthMath
{
    internal const int UnusualMedianMinSamples = 5;
    internal const double UnusualWaitMultiplier = 3.0;
    internal static readonly TimeSpan MedianWindow = TimeSpan.FromDays(30);

    internal static long? ComputeHandoffMs(DateTimeOffset importedAt, DateTime? createdAt)
    {
        if (createdAt is null) return null;
        var createdAtUtc = DateTime.SpecifyKind(createdAt.Value, DateTimeKind.Local).ToUniversalTime();
        var ms = (long)(importedAt.UtcDateTime - createdAtUtc).TotalMilliseconds;
        return Math.Max(0L, ms);
    }

    internal static long? ComputeWaitingMs(DateTime? createdAt, DateTimeOffset now)
    {
        if (createdAt is null) return null;
        var createdAtUtc = DateTime.SpecifyKind(createdAt.Value, DateTimeKind.Local).ToUniversalTime();
        var ms = (long)(now.UtcDateTime - createdAtUtc).TotalMilliseconds;
        return Math.Max(0L, ms);
    }

    internal static long? Percentile(IEnumerable<long> samples, double p)
    {
        var sorted = samples.ToList();
        if (sorted.Count == 0) return null;
        sorted.Sort();
        var idx = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }

    internal static bool IsUnusual(long? waitingMs, long? medianMs, int sampleCount)
    {
        if (waitingMs is null || medianMs is null || medianMs.Value <= 0 || sampleCount < UnusualMedianMinSamples)
            return false;
        return waitingMs.Value > UnusualWaitMultiplier * medianMs.Value;
    }
}
