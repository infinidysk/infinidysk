using NzbWebDAV.Api.Controllers.GetOverviewStats;

namespace NzbWebDAV.Tests.Api;

public class GetOverviewStatsThroughputTests
{
    private const long OneMinute = 60_000;
    private const long OneHour = 3_600_000;

    [Fact]
    public void BuildThroughputFromMinutes_SumsBytesFetchedIntoBuckets()
    {
        var m0 = 1_700_000_000_000L - (1_700_000_000_000L % OneMinute);
        var m1 = m0 + OneMinute;
        var minutes = new[]
        {
            (m0, 10L, 1L, 0L, 100L, 1_000L),
            (m1, 5L, 0L, 2L, 50L, 500L),
            (m1, 1L, 0L, 0L, 10L, 250L), // same minute bucket as m1 when bucketSize=minute
        };

        var points = GetOverviewStatsController.BuildThroughputFromMinutes(minutes, OneMinute);

        Assert.Equal(2, points.Count);
        Assert.Equal(m0, points[0].Bucket);
        Assert.Equal(1_000, points[0].BytesFetched);
        Assert.Equal(100, points[0].BytesServed);
        Assert.Equal(m1, points[1].Bucket);
        Assert.Equal(750, points[1].BytesFetched);
        Assert.Equal(60, points[1].BytesServed);
    }

    [Fact]
    public void BuildThroughputFromHourly_PreservesProviderBytesFetched()
    {
        var h0 = 1_700_000_000_000L - (1_700_000_000_000L % OneHour);
        var h1 = h0 + OneHour;
        var hours = new[]
        {
            (h0, 20L, 2L, 1L, 4_000L),
            (h1, 8L, 0L, 0L, 1_500L),
        };
        var sessions = new[]
        {
            (h0 + 30_000, 200L),
            (h1 + 10_000, 50L),
        };

        var points = GetOverviewStatsController.BuildThroughputFromHourly(hours, sessions, OneHour);

        Assert.Equal(2, points.Count);
        Assert.Equal(4_000, points[0].BytesFetched);
        Assert.Equal(200, points[0].BytesServed);
        Assert.Equal(1_500, points[1].BytesFetched);
        Assert.Equal(50, points[1].BytesServed);
    }

    [Fact]
    public void BuildThroughputFromMinutes_AggregatesAcrossLargerBuckets()
    {
        var start = 1_700_000_000_000L - (1_700_000_000_000L % OneHour);
        var minutes = new[]
        {
            (start, 1L, 0L, 0L, 0L, 100L),
            (start + OneMinute, 1L, 0L, 0L, 0L, 250L),
            (start + OneHour, 1L, 0L, 0L, 0L, 400L),
        };

        var points = GetOverviewStatsController.BuildThroughputFromMinutes(minutes, OneHour);

        Assert.Equal(2, points.Count);
        Assert.Equal(350, points[0].BytesFetched);
        Assert.Equal(400, points[1].BytesFetched);
    }
}
