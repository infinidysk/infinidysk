using NzbWebDAV.Api.Controllers.GetOverviewStats;
using NzbWebDAV.Database.Models.Metrics;

namespace NzbWebDAV.Tests.Api;

public class GetOverviewStatsProviderSeriesTests
{
    private const string ProviderA = "11111111111111111111111111111111";
    private const string ProviderB = "22222222222222222222222222222222";
    private static readonly IReadOnlyDictionary<string, string?> Labels =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ProviderA] = "Alpha",
            [ProviderB] = "Beta",
        };

    private const long OneMinute = 60_000;
    private const long OneHour = 3_600_000;
    private const long OneDay = 86_400_000;

    [Theory]
    [InlineData(GetOverviewStatsRequest.OverviewWindow.Last1Hour, OneMinute, 60, false)]
    [InlineData(GetOverviewStatsRequest.OverviewWindow.Last24Hours, 15 * OneMinute, 96, false)]
    [InlineData(GetOverviewStatsRequest.OverviewWindow.Last7Days, OneHour, 168, false)]
    [InlineData(GetOverviewStatsRequest.OverviewWindow.Last30Days, 6 * OneHour, 120, false)]
    [InlineData(GetOverviewStatsRequest.OverviewWindow.AllTime, 7 * OneDay, 0, true)]
    public void ResolveProviderSeriesGeometry_AlignsBucketsAndMarksTruncation(
        GetOverviewStatsRequest.OverviewWindow window,
        long expectedBucket,
        int expectedCount,
        bool truncated)
    {
        var nowMs = 1_700_000_000_000L;
        var windowStart = window switch
        {
            GetOverviewStatsRequest.OverviewWindow.Last1Hour => nowMs - OneHour,
            GetOverviewStatsRequest.OverviewWindow.Last24Hours => nowMs - OneDay,
            GetOverviewStatsRequest.OverviewWindow.Last7Days => nowMs - 7 * OneDay,
            GetOverviewStatsRequest.OverviewWindow.Last30Days => nowMs - 30 * OneDay,
            _ => 0L,
        };
        var geometry = GetOverviewStatsController.ResolveProviderSeriesGeometry(window, windowStart, nowMs);

        Assert.Equal(expectedBucket, geometry.BucketSize);
        Assert.Equal(truncated, geometry.Truncated);
        Assert.Equal(0, geometry.Start % expectedBucket);
        Assert.Equal(0, geometry.End % expectedBucket);
        Assert.True(geometry.End > geometry.Start);
        if (expectedCount > 0)
            Assert.Equal(expectedCount, (geometry.End - geometry.Start) / expectedBucket);
        else
            Assert.True(geometry.Start >= nowMs - 365 * OneDay - expectedBucket);
    }

    [Fact]
    public void BuildProvidersFromMinutes_ZeroFillsAndIsolatesProviders()
    {
        var windowStart = 1_700_000_000_000L;
        var minutes = new[]
        {
            (windowStart, ProviderA, 10L, 2_000_000L, 0L, 0L, 0L, 1_000L),
            (windowStart + OneMinute, ProviderB, 10L, 1_000_000L, 0L, 0L, 0L, 1_000L),
        };

        var rows = GetOverviewStatsController.BuildProvidersFromMinutes(
            minutes,
            windowStart,
            GetOverviewStatsRequest.OverviewWindow.Last1Hour,
            Labels,
            windowStart + OneHour);

        var a = Assert.Single(rows, r => r.Provider == ProviderA);
        var b = Assert.Single(rows, r => r.Provider == ProviderB);
        var alignedStart = windowStart - windowStart % OneMinute;
        Assert.Equal(60, a.SpeedSeries.Count);
        Assert.Equal(60, b.SpeedSeries.Count);
        Assert.Equal(alignedStart, a.SpeedSeries[0].Bucket);
        Assert.Equal(2.0, a.SpeedSeries[0].SpeedMbPerSec);
        Assert.Equal(2_000_000, a.SpeedSeries[0].BytesFetched);
        Assert.Equal(0, a.SpeedSeries[1].SpeedMbPerSec);
        Assert.Equal(0, b.SpeedSeries[0].SpeedMbPerSec);
        Assert.Equal(1.0, b.SpeedSeries[1].SpeedMbPerSec);
    }

    [Fact]
    public void BuildProvidersFromHourly_AllTimeClampsSparkAndOmitsLifetimeFromSeries()
    {
        var nowMs = 1_800_000_000_000L;
        var recentHour = nowMs - OneHour;
        recentHour -= recentHour % OneHour;
        var hours = new[]
        {
            (recentHour, ProviderA, 10L, 2_000_000L, 0L, 0L, 0L, 1_000L),
        };
        var lifetime = new[]
        {
            new ProviderLifetimeTotal
            {
                Provider = ProviderA,
                Articles = 99,
                BytesFetched = 9_000_000,
                Misses = 0,
                Errors = 0,
                Retries = 0,
                SumDurationMs = 1_000,
            },
        };

        var rows = GetOverviewStatsController.BuildProvidersFromHourly(
            hours,
            windowStart: 0,
            bucketSize: OneHour,
            nowMs,
            Labels,
            GetOverviewStatsRequest.OverviewWindow.AllTime,
            lifetime);

        var row = Assert.Single(rows);
        Assert.Equal(109, row.Articles);
        Assert.True(row.SpeedSpark.Count is > 0 and <= 60);
        Assert.Contains(row.SpeedSpark, v => v > 0);
        Assert.DoesNotContain(row.SpeedSeries, p => p.BytesFetched == 9_000_000);
        Assert.Contains(row.SpeedSeries, p => p.BytesFetched == 2_000_000);
        Assert.All(
            row.SpeedSeries,
            p => Assert.True(p.Bucket >= nowMs - 365 * OneDay - 7 * OneDay));
    }
}
