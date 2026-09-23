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

    private const long Minute0 = 1_700_000_000_000L - (1_700_000_000_000L % 60_000);

    [Fact]
    public void SampleFetchRate_KeepsHighestOneSecondRatePerMinute()
    {
        long timestamp = 1;
        var tracker = new ProviderBytesTracker(() => timestamp);

        tracker.SampleFetchRate(Minute0);
        tracker.Add("p1", 100_000_000);
        timestamp += Stopwatch.Frequency;
        tracker.SampleFetchRate(Minute0 + 1_000);

        tracker.Add("p1", 10_000_000);
        timestamp += Stopwatch.Frequency;
        tracker.SampleFetchRate(Minute0 + 2_000);

        Assert.Equal(100_000_000, tracker.PendingPeakSince(Minute0));
    }

    [Fact]
    public void SampleFetchRate_NormalizesByElapsedTime()
    {
        long timestamp = 1;
        var tracker = new ProviderBytesTracker(() => timestamp);

        tracker.SampleFetchRate(Minute0);
        tracker.Add("p1", 50_000_000);
        timestamp += Stopwatch.Frequency * 2;
        tracker.SampleFetchRate(Minute0 + 2_000);

        Assert.Equal(25_000_000, tracker.PendingPeakSince(Minute0));
    }

    [Fact]
    public void SampleFetchRate_DoesNotRecordPeakFromSubSecondWindow()
    {
        long timestamp = 1;
        var tracker = new ProviderBytesTracker(() => timestamp);

        tracker.SampleFetchRate(Minute0);
        tracker.Add("p1", 1_000_000);
        timestamp += Stopwatch.Frequency / 10;
        tracker.SampleFetchRate(Minute0 + 100);

        Assert.Equal(0, tracker.PendingPeakSince(Minute0));

        timestamp += Stopwatch.Frequency * 9 / 10;
        tracker.SampleFetchRate(Minute0 + 1_000);

        Assert.Equal(1_000_000, tracker.PendingPeakSince(Minute0));
    }

    [Fact]
    public void DrainClosedPeaks_PopsOnlyMinutesBeforeCutoff()
    {
        long timestamp = 1;
        var tracker = new ProviderBytesTracker(() => timestamp);
        var minute1 = Minute0 + 60_000;

        tracker.SampleFetchRate(Minute0);
        tracker.Add("p1", 1_000);
        timestamp += Stopwatch.Frequency;
        tracker.SampleFetchRate(Minute0 + 1_000);
        tracker.Add("p1", 5_000);
        timestamp += Stopwatch.Frequency;
        tracker.SampleFetchRate(minute1);

        var drained = tracker.DrainClosedPeaks(minute1);

        Assert.Equal([(Minute0, 1_000L)], drained);
        Assert.Equal(5_000, tracker.PendingPeakSince(minute1));
        Assert.Empty(tracker.DrainClosedPeaks(minute1));
    }

    [Fact]
    public void RestorePeaks_MergesDrainedValuesWithoutLosingConcurrentHigherPeak()
    {
        var tracker = new ProviderBytesTracker(() => 1);
        tracker.RestorePeaks([(Minute0, 500L)]);
        var drained = tracker.DrainClosedPeaks(Minute0 + 60_000);
        tracker.RestorePeaks([(Minute0, 700L)]);
        tracker.RestorePeaks(drained);

        Assert.Equal(700, tracker.PendingPeakSince(Minute0));
    }

    [Fact]
    public void ResetCounters_ClearsPeaksAndRebaselinesWithoutNegativeRate()
    {
        long timestamp = 1;
        var tracker = new ProviderBytesTracker(() => timestamp);

        tracker.SampleFetchRate(Minute0);
        tracker.Add("p1", 1_000_000);
        timestamp += Stopwatch.Frequency;
        tracker.SampleFetchRate(Minute0 + 1_000);
        Assert.Equal(1_000_000, tracker.PendingPeakSince(Minute0));

        tracker.ResetCounters();
        Assert.Equal(0, tracker.PendingPeakSince(0));

        timestamp += Stopwatch.Frequency;
        tracker.SampleFetchRate(Minute0 + 2_000);
        Assert.Equal(0, tracker.PendingPeakSince(0));

        tracker.Add("p1", 2_000);
        timestamp += Stopwatch.Frequency;
        tracker.SampleFetchRate(Minute0 + 3_000);
        Assert.Equal(2_000, tracker.PendingPeakSince(Minute0));
    }

    [Fact]
    public void ResetCounters_TracksBytesAddedBeforeNextSample()
    {
        long timestamp = 1;
        var tracker = new ProviderBytesTracker(() => timestamp);
        tracker.SampleFetchRate(Minute0);

        tracker.ResetCounters();
        tracker.Add("p1", 1_000);
        timestamp += Stopwatch.Frequency;
        tracker.SampleFetchRate(Minute0 + 1_000);

        Assert.Equal(1_000, tracker.PendingPeakSince(Minute0));
    }

    [Fact]
    public void ResetProvider_RebaselinesAfterRemovingProviderBytes()
    {
        long timestamp = 1;
        var tracker = new ProviderBytesTracker(() => timestamp);
        tracker.Add("p1", 10_000);
        tracker.SampleFetchRate(Minute0);

        tracker.ResetProvider("p1");
        tracker.Add("p2", 2_000);
        timestamp += Stopwatch.Frequency;
        tracker.SampleFetchRate(Minute0 + 1_000);

        Assert.Equal(2_000, tracker.PendingPeakSince(Minute0));
    }
}
