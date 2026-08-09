namespace NzbWebDAV.Services.Metrics;

public static class CircuitOutageSparkBuilder
{
    public sealed record Event(
        long At,
        string Provider,
        string State,
        long? CooldownMs);

    public static Dictionary<string, List<int>> Build(
        IEnumerable<Event> events,
        IEnumerable<string> providerKeys,
        long sparkStart,
        long bucketSize,
        int bucketCount,
        long nowMs)
    {
        var distinctProviderKeys = providerKeys.Distinct(StringComparer.Ordinal).ToArray();
        var result = distinctProviderKeys
            .ToDictionary(
                key => key,
                _ => new long[bucketCount],
                StringComparer.Ordinal);

        var tripBuckets = distinctProviderKeys
            .ToDictionary(
                key => key,
                _ => new bool[bucketCount],
                StringComparer.Ordinal);

        foreach (var group in events
                     .Where(item => result.ContainsKey(item.Provider))
                     .GroupBy(item => item.Provider, StringComparer.Ordinal))
        {
            var outageMs = result[group.Key];
            var trips = tripBuckets[group.Key];
            long? openAt = null;
            long openUntil = 0;

            foreach (var item in group.OrderBy(item => item.At))
            {
                if (item.State.Equals("open", StringComparison.OrdinalIgnoreCase))
                {
                    if (openAt is { } previousOpen)
                        AddInterval(outageMs, previousOpen, Math.Min(openUntil, item.At));

                    MarkTripBucket(trips, item.At);
                    openAt = item.At;
                    openUntil = item.At + Math.Max(0, item.CooldownMs ?? 0);
                    continue;
                }

                if (!item.State.Equals("closed", StringComparison.OrdinalIgnoreCase) ||
                    openAt is not { } started)
                {
                    continue;
                }

                AddInterval(outageMs, started, Math.Min(openUntil, item.At));
                openAt = null;
            }

            if (openAt is { } stillOpen)
                AddInterval(outageMs, stillOpen, Math.Min(openUntil, nowMs));

            void AddInterval(long[] buckets, long start, long end)
            {
                start = Math.Max(start, sparkStart);
                end = Math.Min(end, sparkStart + bucketSize * bucketCount);
                if (end <= start)
                    return;

                var first = Math.Max(0, (int)((start - sparkStart) / bucketSize));
                var last = Math.Min(bucketCount - 1, (int)((end - 1 - sparkStart) / bucketSize));
                for (var index = first; index <= last; index++)
                {
                    var bucketStart = sparkStart + index * bucketSize;
                    var bucketEnd = bucketStart + bucketSize;
                    buckets[index] += Math.Max(
                        0,
                        Math.Min(end, bucketEnd) - Math.Max(start, bucketStart));
                }
            }

            void MarkTripBucket(bool[] buckets, long at)
            {
                if (at < sparkStart || at >= sparkStart + bucketSize * bucketCount)
                    return;
                var index = (int)((at - sparkStart) / bucketSize);
                if (index >= 0 && index < bucketCount)
                    buckets[index] = true;
            }
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair =>
            {
                var trips = tripBuckets[pair.Key];
                return pair.Value
                    .Select((duration, index) =>
                    {
                        var percent = (int)Math.Clamp(
                            Math.Round(duration * 100d / bucketSize),
                            0,
                            100);
                        // Keep recorded trips visible when occupancy rounds below 1%.
                        if (percent == 0 && trips[index])
                            return 1;
                        return percent;
                    })
                    .ToList();
            },
            StringComparer.Ordinal);
    }
}
