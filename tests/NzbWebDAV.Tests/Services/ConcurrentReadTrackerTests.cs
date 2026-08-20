using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Services;

public class ConcurrentReadTrackerTests
{
    [Fact]
    public void OverlappingRanges_RecordPrivateFallbackAndStartDistance()
    {
        var tracker = new ConcurrentReadTracker();
        using var first = tracker.BeginRead(
            "/content/movie.mkv", 0, ConcurrentReadRegion.StartRange);
        using var second = tracker.BeginRead(
            "/content/movie.mkv", 5_000_000_000, ConcurrentReadRegion.OffsetRange);

        var snapshot = tracker.Snapshot();

        Assert.Equal(2, snapshot.ReaderStarts);
        Assert.Equal(1, snapshot.OverlapEvents);
        Assert.Equal(1, snapshot.PrivateFallbacksNoRegistry);
        Assert.Equal(2, snapshot.PeakConcurrentReaders);
        Assert.Equal(1, snapshot.CurrentOverlappingPaths);
        Assert.Equal(1, snapshot.StartDistanceSamples);
        Assert.Equal(5_000_000_000, snapshot.TotalStartDistanceBytes);
        Assert.Equal(5_000_000_000, snapshot.MaxStartDistanceBytes);
    }

    [Fact]
    public void EnabledSharedStreams_CountFallbackOnlyOnPrivatePath()
    {
        var tracker = new ConcurrentReadTracker(configManager: new ConfigManager());
        using var first = tracker.BeginRead(
            "/content/movie.mkv", 0, ConcurrentReadRegion.Full);
        using var second = tracker.BeginRead(
            "/content/movie.mkv", 0, ConcurrentReadRegion.Full);

        Assert.Equal(1, tracker.Snapshot().OverlapEvents);
        Assert.Equal(0, tracker.Snapshot().PrivateFallbacksNoRegistry);

        tracker.RecordPrivateFallbackIfOverlapping();
        Assert.Equal(1, tracker.Snapshot().PrivateFallbacksNoRegistry);
        tracker.RecordPrivateFallbackIfOverlapping();
        Assert.Equal(1, tracker.Snapshot().PrivateFallbacksNoRegistry);
    }

    [Fact]
    public void DisabledSharedStreams_CountEveryOverlapAsFallback()
    {
        var config = new ConfigManager();
        config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.UsenetSharedStreamsEnabled, ConfigValue = "false" },
        ]);
        var tracker = new ConcurrentReadTracker(configManager: config);
        using var first = tracker.BeginRead(
            "/content/movie.mkv", 0, ConcurrentReadRegion.Full);
        using var second = tracker.BeginRead(
            "/content/movie.mkv", 0, ConcurrentReadRegion.Full);

        Assert.Equal(1, tracker.Snapshot().PrivateFallbacksNoRegistry);
        tracker.RecordPrivateFallbackIfOverlapping();
        Assert.Equal(1, tracker.Snapshot().PrivateFallbacksNoRegistry);
    }

    [Fact]
    public void SuffixRange_UpdateStartRecordsResolvedDistance()
    {
        var tracker = new ConcurrentReadTracker();
        using var first = tracker.BeginRead(
            "/content/movie.mkv", 0, ConcurrentReadRegion.StartRange);
        using var suffix = tracker.BeginRead(
            "/content/movie.mkv", null, ConcurrentReadRegion.SuffixRange);
        Assert.Equal(0, tracker.Snapshot().StartDistanceSamples);

        suffix.UpdateStart(10_000_000_000);

        var snapshot = tracker.Snapshot();
        Assert.Equal(1, snapshot.StartDistanceSamples);
        Assert.Equal(10_000_000_000, snapshot.MaxStartDistanceBytes);
    }

    [Fact]
    public void ViewAndWebdavEntryPointKeys_OverlapOnTheSameFile()
    {
        // /view passes the decoded, prefix-stripped path; the WebDAV GET handler
        // decodes its raw request path. Both must key the same file.
        var tracker = new ConcurrentReadTracker();
        using var viewRead = tracker.BeginRead(
            "content/My Movie.mkv", 0, ConcurrentReadRegion.Full);
        using var webdavRead = tracker.BeginRead(
            Uri.UnescapeDataString("/content/My%20Movie.mkv"), 0, ConcurrentReadRegion.Full);

        Assert.Equal(1, tracker.Snapshot().OverlapEvents);
    }

    [Fact]
    public void DifferentPaths_DoNotOverlap()
    {
        var tracker = new ConcurrentReadTracker();
        using var first = tracker.BeginRead("/content/a.mkv", 0, ConcurrentReadRegion.Full);
        using var second = tracker.BeginRead("/content/b.mkv", 0, ConcurrentReadRegion.Full);

        Assert.Equal(0, tracker.Snapshot().OverlapEvents);
    }

    [Fact]
    public void SimultaneousSameSegmentFetchesByDifferentReaders_CountAsDuplicate()
    {
        var tracker = new ConcurrentReadTracker();
        using var firstReader = tracker.BeginRead(
            "/content/movie.mkv", 0, ConcurrentReadRegion.Full);
        using var firstFetch = tracker.BeginSegmentFetch("segment-42");
        using var secondReader = tracker.BeginRead(
            "/content/movie.mkv", 0, ConcurrentReadRegion.Full);
        using var secondFetch = tracker.BeginSegmentFetch("segment-42");

        Assert.Equal(1, tracker.Snapshot().DuplicateInFlightSegmentFetches);
    }

    [Fact]
    public void SequentialOrSameReaderFetches_DoNotCountAsDuplicates()
    {
        var tracker = new ConcurrentReadTracker();
        using var reader = tracker.BeginRead(
            "/content/movie.mkv", 0, ConcurrentReadRegion.Full);

        using (tracker.BeginSegmentFetch("segment-42")) { }
        using (tracker.BeginSegmentFetch("segment-42")) { }
        using var firstConcurrent = tracker.BeginSegmentFetch("segment-43");
        using var secondConcurrent = tracker.BeginSegmentFetch("segment-43");

        Assert.Equal(0, tracker.Snapshot().DuplicateInFlightSegmentFetches);
    }

    [Fact]
    public void FetchOutsideReadContext_IsIgnored()
    {
        var tracker = new ConcurrentReadTracker();

        using var fetch = tracker.BeginSegmentFetch("segment-42");

        Assert.Equal(default, tracker.Snapshot());
    }

    [Fact]
    public async Task MultiProviderSetupFailure_ClosesTrackedFetchScope()
    {
        var tracker = new ConcurrentReadTracker();
        using var reader = tracker.BeginRead(
            "/content/movie.mkv", 0, ConcurrentReadRegion.Full);
        var segmentId = (SegmentId)"segment-42";
        string segmentKey = segmentId;
        using var existingFetch = tracker.BeginSegmentFetch(segmentKey);
        using var secondReader = tracker.BeginRead(
            "/content/movie.mkv", 0, ConcurrentReadRegion.Full);
        using var client = new MultiProviderNntpClient(
            [],
            concurrentReadTracker: tracker);

        await Assert.ThrowsAnyAsync<Exception>(() => client.DecodedBodyAsync(
            segmentId,
            onConnectionReadyAgain: null,
            CancellationToken.None));

        var snapshot = tracker.Snapshot();
        Assert.Equal(1, snapshot.DuplicateInFlightSegmentFetches);
        Assert.Equal(1, snapshot.CurrentInFlightSegmentFetches);
    }

    [Fact]
    public async Task MultiProviderBatchSetupFailure_ClosesEveryTrackedFetchScope()
    {
        var tracker = new ConcurrentReadTracker();
        var firstId = (SegmentId)"segment-1";
        var secondId = (SegmentId)"segment-2";
        string firstKey = firstId;
        string secondKey = secondId;
        using var firstReader = tracker.BeginRead(
            "/content/movie.mkv", 0, ConcurrentReadRegion.Full);
        using var firstFetch = tracker.BeginSegmentFetch(firstKey);
        using var secondFetch = tracker.BeginSegmentFetch(secondKey);
        using var secondReader = tracker.BeginRead(
            "/content/movie.mkv", 0, ConcurrentReadRegion.Full);
        using var client = new MultiProviderNntpClient(
            [],
            concurrentReadTracker: tracker);

        await Assert.ThrowsAnyAsync<Exception>(() => client.DecodedBodiesAsync(
            [firstId, secondId],
            onConnectionReadyAgain: null,
            CancellationToken.None));

        var snapshot = tracker.Snapshot();
        Assert.Equal(2, snapshot.DuplicateInFlightSegmentFetches);
        Assert.Equal(2, snapshot.CurrentInFlightSegmentFetches);
    }

    [Fact]
    public void Snapshot_TracksRegionsAndCompletedLifetime()
    {
        var clock = new ManualTimeProvider();
        var tracker = new ConcurrentReadTracker(clock);
        var full = tracker.BeginRead("/full", 0, ConcurrentReadRegion.Full);
        clock.Advance(TimeSpan.FromSeconds(2));
        full.Dispose();
        using var start = tracker.BeginRead("/start", 0, ConcurrentReadRegion.StartRange);
        using var offset = tracker.BeginRead("/offset", 100, ConcurrentReadRegion.OffsetRange);
        using var suffix = tracker.BeginRead("/suffix", null, ConcurrentReadRegion.SuffixRange);

        var snapshot = tracker.Snapshot();

        Assert.Equal(1, snapshot.CompletedReads);
        Assert.Equal(2_000, snapshot.TotalReadLifetimeMs);
        Assert.Equal(2_000, snapshot.MaxReadLifetimeMs);
        Assert.Equal(1, snapshot.FullReads);
        Assert.Equal(1, snapshot.StartRangeReads);
        Assert.Equal(1, snapshot.OffsetRangeReads);
        Assert.Equal(1, snapshot.SuffixRangeReads);
    }

    [Fact]
    public void ParallelBeginAndEnd_LeavesNoLivePaths()
    {
        var tracker = new ConcurrentReadTracker();

        Parallel.For(0, 1_000, _ =>
        {
            using var read = tracker.BeginRead(
                "/content/movie.mkv", 0, ConcurrentReadRegion.Full);
        });

        var snapshot = tracker.Snapshot();
        Assert.Equal(1_000, snapshot.ReaderStarts);
        Assert.Equal(1_000, snapshot.CompletedReads);
        Assert.Equal(0, snapshot.CurrentOverlappingPaths);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
