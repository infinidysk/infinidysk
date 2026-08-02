using System.Diagnostics;
using NzbWebDAV.Services.Metrics;

namespace NzbWebDAV.Tests.Services.Metrics;

public class ProviderBytesTrackerTests
{
    [Fact]
    public void GetRecentBytesPerMs_ExpiresUiFallbackWithoutClearingSchedulerEstimate()
    {
        long timestamp = 1;
        var tracker = new ProviderBytesTracker(() => timestamp);
        tracker.RecordSegmentThroughput("primary", 1_000_000, 1_000);

        Assert.Equal(1_000d, tracker.GetRecentBytesPerMs("primary", TimeSpan.FromSeconds(1)));

        timestamp += Stopwatch.Frequency * 2;

        Assert.Equal(0, tracker.GetRecentBytesPerMs("primary", TimeSpan.FromSeconds(1)));
        Assert.Equal(1_000d, tracker.GetBytesPerMs("primary"));
    }
}
