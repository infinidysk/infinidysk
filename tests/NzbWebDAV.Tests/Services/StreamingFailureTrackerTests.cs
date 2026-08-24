using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class StreamingFailureTrackerTests
{
    [Fact]
    public void RecordFailure_IncrementsCount()
    {
        var tracker = new StreamingFailureTracker();
        var id = Guid.NewGuid();

        Assert.Equal(1, tracker.RecordFailure(id));
        Assert.Equal(2, tracker.RecordFailure(id));
        Assert.Equal(3, tracker.RecordFailure(id));
        Assert.Equal(3, tracker.GetFailureCount(id));
    }

    [Fact]
    public void GetFailureCount_ReturnsZeroForUnknownItem()
    {
        var tracker = new StreamingFailureTracker();
        Assert.Equal(0, tracker.GetFailureCount(Guid.NewGuid()));
    }

    [Fact]
    public void ClearFailure_ResetsCount()
    {
        var tracker = new StreamingFailureTracker();
        var id = Guid.NewGuid();
        tracker.RecordFailure(id);
        tracker.RecordFailure(id);

        tracker.ClearFailure(id);

        Assert.Equal(0, tracker.GetFailureCount(id));
    }

    [Fact]
    public void RecordFailure_TracksItemsIndependently()
    {
        var tracker = new StreamingFailureTracker();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        tracker.RecordFailure(a);
        tracker.RecordFailure(a);
        tracker.RecordFailure(b);

        Assert.Equal(2, tracker.GetFailureCount(a));
        Assert.Equal(1, tracker.GetFailureCount(b));
    }

    [Fact]
    public void AttributedFailures_DeduplicateOrdinalSegmentIds()
    {
        var tracker = new StreamingFailureTracker();
        var id = Guid.NewGuid();

        tracker.RecordAttributedFailure(id, "<segment@test>");
        tracker.RecordAttributedFailure(id, "<segment@test>");
        tracker.RecordAttributedFailure(id, "<SEGMENT@test>");

        var snapshot = tracker.GetSnapshot(id);
        Assert.Equal(3, snapshot.Count);
        Assert.True(snapshot.HasTargetableSegmentIds);
        Assert.Equal(["<segment@test>", "<SEGMENT@test>"], snapshot.SegmentIds);
    }

    [Fact]
    public void UnattributedFailure_MakesEntireStreakConservative()
    {
        var tracker = new StreamingFailureTracker();
        var id = Guid.NewGuid();

        tracker.RecordAttributedFailure(id, "<segment@test>");
        tracker.RecordUnattributedFailure(id);

        var snapshot = tracker.GetSnapshot(id);
        Assert.Equal(2, snapshot.Count);
        Assert.True(snapshot.HasUnattributedFailure);
        Assert.False(snapshot.HasTargetableSegmentIds);
        Assert.Equal(["<segment@test>"], snapshot.SegmentIds);
    }

    [Fact]
    public void ClearFailure_RemovesCountAndContextAtomically()
    {
        var tracker = new StreamingFailureTracker();
        var id = Guid.NewGuid();
        tracker.RecordAttributedFailure(id, "<segment@test>");

        tracker.ClearFailure(id);

        Assert.Equal(StreamingFailureSnapshot.Empty, tracker.GetSnapshot(id));
    }

    [Fact]
    public void AttributedFailureCap_PreservesCountAndTargetability()
    {
        var tracker = new StreamingFailureTracker();
        var id = Guid.NewGuid();

        for (var i = 0; i < 65; i++)
            tracker.RecordAttributedFailure(id, $"<{i}@test>");

        var snapshot = tracker.GetSnapshot(id);
        Assert.Equal(65, snapshot.Count);
        Assert.Equal(64, snapshot.SegmentIds.Length);
        Assert.True(snapshot.HasTargetableSegmentIds);
    }
}
