using NzbWebDAV.Api.Controllers.GetOverviewStats;
using NzbWebDAV.Database.Models.Metrics;

namespace NzbWebDAV.Tests.Api;

public class GetOverviewStatsProviderFilterTests
{
    private static readonly string ConfiguredKey = Guid.NewGuid().ToString("N");
    private static readonly string DeletedKey = Guid.NewGuid().ToString("N");

    private static readonly IReadOnlyDictionary<string, string?> Labels =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ConfiguredKey] = "Primary",
        };

    [Fact]
    public void IsConfiguredMetricsKey_OnlyMatchesLabelMap()
    {
        Assert.True(GetOverviewStatsController.IsConfiguredMetricsKey(ConfiguredKey, Labels));
        Assert.False(GetOverviewStatsController.IsConfiguredMetricsKey(DeletedKey, Labels));
    }

    [Fact]
    public void BuildProvidersFromMinutes_OmitsDeletedProviderKeys()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var windowStart = nowMs - 60_000;
        var minutes = new[]
        {
            (windowStart, ConfiguredKey, 10L, 1000L, 0L, 0L, 0L, 100L),
            (windowStart, DeletedKey, 50L, 5000L, 0L, 1L, 2L, 500L),
        };

        var rows = GetOverviewStatsController.BuildProvidersFromMinutes(
            minutes,
            windowStart,
            GetOverviewStatsRequest.OverviewWindow.Last1Hour,
            Labels);

        Assert.Single(rows);
        Assert.Equal(ConfiguredKey, rows[0].Provider);
        Assert.Equal("Primary", rows[0].Nickname);
        Assert.Equal(10, rows[0].Articles);
    }

    [Fact]
    public void BuildProvidersFromMinutes_BucketsErrorAndRetrySparksAlongsideActivitySpark()
    {
        var windowStart = 1_700_000_000_000L; // aligned to minute
        var minute0 = windowStart;
        var minute1 = windowStart + 60_000;
        var minutes = new[]
        {
            (minute0, ConfiguredKey, 10L, 1000L, 0L, 2L, 4L, 100L),
            (minute1, ConfiguredKey, 5L, 500L, 0L, 1L, 3L, 50L),
        };

        var rows = GetOverviewStatsController.BuildProvidersFromMinutes(
            minutes,
            windowStart,
            GetOverviewStatsRequest.OverviewWindow.Last1Hour,
            Labels);

        Assert.Single(rows);
        Assert.Equal(3, rows[0].Errors);
        Assert.Equal(7, rows[0].Retries);
        Assert.Equal(60, rows[0].Spark.Count);
        Assert.Equal(60, rows[0].ErrorSpark.Count);
        Assert.Equal(60, rows[0].RetrySpark.Count);
        Assert.Equal(10, rows[0].Spark[0]);
        Assert.Equal(2, rows[0].ErrorSpark[0]);
        Assert.Equal(4, rows[0].RetrySpark[0]);
        Assert.Equal(5, rows[0].Spark[1]);
        Assert.Equal(1, rows[0].ErrorSpark[1]);
        Assert.Equal(3, rows[0].RetrySpark[1]);
        Assert.Equal(0, rows[0].ErrorSpark[2]);
        Assert.Equal(0, rows[0].RetrySpark[2]);
    }

    [Fact]
    public void BuildProvidersFromMinutes_AvgDurationMs_UsesOkFetchesOnly()
    {
        var windowStart = 1_700_000_000_000L;
        // 10 articles: 7 ok + 2 misses + 1 error. SumDurationMs is Ok-only (7 * 10 = 70).
        var minutes = new[]
        {
            (windowStart, ConfiguredKey, 10L, 1000L, 2L, 1L, 0L, 70L),
        };

        var rows = GetOverviewStatsController.BuildProvidersFromMinutes(
            minutes,
            windowStart,
            GetOverviewStatsRequest.OverviewWindow.Last1Hour,
            Labels);

        Assert.Single(rows);
        Assert.Equal(10.0, rows[0].AvgDurationMs);
    }

    [Fact]
    public void BuildProvidersFromMinutes_ComputesAggregateAndBucketSpeeds()
    {
        var windowStart = 1_700_000_000_000L;
        var minutes = new[]
        {
            (windowStart, ConfiguredKey, 10L, 2_000_000L, 0L, 0L, 0L, 1_000L),
            (windowStart + 60_000, ConfiguredKey, 10L, 1_000_000L, 0L, 0L, 0L, 1_000L),
        };

        var rows = GetOverviewStatsController.BuildProvidersFromMinutes(
            minutes,
            windowStart,
            GetOverviewStatsRequest.OverviewWindow.Last1Hour,
            Labels);

        var row = Assert.Single(rows);
        Assert.Equal(1.5, row.SpeedMbPerSec);
        Assert.Equal(60, row.SpeedSpark.Count);
        Assert.Equal(2.0, row.SpeedSpark[0]);
        Assert.Equal(1.0, row.SpeedSpark[1]);
        Assert.Equal(0.0, row.SpeedSpark[2]);
    }

    [Fact]
    public void BuildProvidersFromHourly_ComputesDailySpeedBuckets()
    {
        const long oneHour = 3_600_000;
        const long oneDay = 86_400_000;
        var windowStart = 1_700_000_000_000L - (1_700_000_000_000L % oneDay);
        var hours = new[]
        {
            (windowStart, ConfiguredKey, 10L, 2_000_000L, 0L, 0L, 0L, 1_000L),
            (windowStart + oneDay, ConfiguredKey, 10L, 1_000_000L, 0L, 0L, 0L, 1_000L),
        };

        var rows = GetOverviewStatsController.BuildProvidersFromHourly(
            hours,
            windowStart,
            oneHour,
            windowStart + (2 * oneDay),
            Labels);

        var row = Assert.Single(rows);
        Assert.Equal(1.5, row.SpeedMbPerSec);
        Assert.Equal(3, row.SpeedSpark.Count);
        Assert.Equal(2.0, row.SpeedSpark[0]);
        Assert.Equal(1.0, row.SpeedSpark[1]);
        Assert.Equal(0.0, row.SpeedSpark[2]);
    }

    [Fact]
    public void BuildFailover_OmitsDeletedProvidersFromListsButKeepsAggregateTotals()
    {
        var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var rescues = new[]
        {
            (at, ConfiguredKey, 3L),
            (at, DeletedKey, 7L),
        };
        var misses = new[]
        {
            (ConfiguredKey, SegmentFetch.FetchStatus.Missing, 2L),
            (DeletedKey, SegmentFetch.FetchStatus.Timeout, 4L),
        };

        var block = GetOverviewStatsController.BuildFailover(
            rescues,
            misses,
            totalArticles: 100,
            readSessions: 5,
            readsSaved: 2,
            previousSaves: 1,
            chartBucketSize: 60_000,
            Labels);

        Assert.Equal(10, block.ArticlesRecovered);
        Assert.Equal(6, block.SegmentsCovered);

        Assert.Single(block.RescuedBy);
        Assert.Equal(ConfiguredKey, block.RescuedBy[0].Provider);
        Assert.Equal("Primary", block.RescuedBy[0].Nickname);
        Assert.Equal(3, block.RescuedBy[0].Saves);

        Assert.Single(block.RescuedFrom);
        Assert.Equal(ConfiguredKey, block.RescuedFrom[0].Provider);
        Assert.Equal(2, block.RescuedFrom[0].Misses);

        Assert.All(block.Buckets, b => Assert.Single(b.Counts));
        Assert.Equal(3, block.Buckets.Sum(b => b.Counts.Sum()));
    }
}
