using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class ConcurrentReadTrackerTests
{
    [Fact]
    public void SingleReader_NoOverlapDetected()
    {
        var tracker = new ConcurrentReadTracker();
        using (tracker.BeginRead("/content/movie.mkv")) { }

        Assert.Equal(0, tracker.OverlapEvents);
        Assert.Equal(1, tracker.PeakConcurrentReaders);
    }

    [Fact]
    public void TwoConcurrentReaders_RecordsOverlap()
    {
        var tracker = new ConcurrentReadTracker();
        using var reader1 = tracker.BeginRead("/content/movie.mkv");
        using var reader2 = tracker.BeginRead("/content/movie.mkv");

        Assert.Equal(1, tracker.OverlapEvents);
        Assert.Equal(2, tracker.PeakConcurrentReaders);
    }

    [Fact]
    public void DifferentPaths_NoOverlap()
    {
        var tracker = new ConcurrentReadTracker();
        using var reader1 = tracker.BeginRead("/content/movie1.mkv");
        using var reader2 = tracker.BeginRead("/content/movie2.mkv");

        Assert.Equal(0, tracker.OverlapEvents);
    }

    [Fact]
    public void DuplicateSegmentFetch_DetectedWhenConcurrent()
    {
        var tracker = new ConcurrentReadTracker();
        using var reader1 = tracker.BeginRead("/content/movie.mkv");
        using var reader2 = tracker.BeginRead("/content/movie.mkv");

        tracker.RecordSegmentFetch("/content/movie.mkv", "seg-42");
        tracker.RecordSegmentFetch("/content/movie.mkv", "seg-42");

        Assert.Equal(1, tracker.DuplicateSegmentFetches);
    }

    [Fact]
    public void DuplicateSegmentFetch_NotDetectedWithSingleReader()
    {
        var tracker = new ConcurrentReadTracker();
        using var reader1 = tracker.BeginRead("/content/movie.mkv");

        tracker.RecordSegmentFetch("/content/movie.mkv", "seg-42");
        tracker.RecordSegmentFetch("/content/movie.mkv", "seg-42");

        Assert.Equal(0, tracker.DuplicateSegmentFetches);
    }

    [Fact]
    public void EndRead_CleansUpState()
    {
        var tracker = new ConcurrentReadTracker();
        var reader = tracker.BeginRead("/content/movie.mkv");
        reader.Dispose();

        var snapshot = tracker.Snapshot();
        Assert.Equal(0, snapshot.CurrentOverlappingPaths);
    }

    [Fact]
    public void Snapshot_ReflectsLiveState()
    {
        var tracker = new ConcurrentReadTracker();
        using var reader1 = tracker.BeginRead("/content/a.mkv");
        using var reader2 = tracker.BeginRead("/content/a.mkv");
        using var reader3 = tracker.BeginRead("/content/b.mkv");

        var snapshot = tracker.Snapshot();
        Assert.Equal(1, snapshot.OverlapEvents);
        Assert.Equal(2, snapshot.PeakConcurrentReaders);
        Assert.Equal(1, snapshot.CurrentOverlappingPaths);
    }

    [Fact]
    public void ThreeConcurrentReaders_TracksIncreasingPeak()
    {
        var tracker = new ConcurrentReadTracker();
        using var r1 = tracker.BeginRead("/content/movie.mkv");
        using var r2 = tracker.BeginRead("/content/movie.mkv");
        using var r3 = tracker.BeginRead("/content/movie.mkv");

        Assert.Equal(3, tracker.PeakConcurrentReaders);
        Assert.Equal(2, tracker.OverlapEvents);
    }
}
