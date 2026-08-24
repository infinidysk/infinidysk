using NzbWebDAV.Exceptions;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Streams;

[Collection(nameof(PlaybackHoleTrackerCollection))]
public sealed class PlaybackHoleTrackerTests : IDisposable
{
    public PlaybackHoleTrackerTests() => PlaybackHoleTracker.ResetForTests();

    public void Dispose() => PlaybackHoleTracker.ResetForTests();

    [Fact]
    public void ThreeConsecutiveHoles_FailFast_TwoIsolatedHolesDoNot()
    {
        var path = $"/view/isolated-{Guid.NewGuid():N}.mkv";
        var miss = new UsenetArticleNotFoundException("a@test");
        PlaybackHoleTracker.RecordHole(path, "a@test", miss);
        PlaybackHoleTracker.RecordHole(path, "b@test", miss);
        Assert.False(PlaybackHoleTracker.ShouldFailFast(path, out _));

        PlaybackHoleTracker.RecordHole(path, "c@test", miss);
        Assert.True(PlaybackHoleTracker.ShouldFailFast(path, out var stored));
        Assert.Same(miss, stored);
        Assert.True(PlaybackHoleTracker.IsKnownMissingSegment(path, "b@test"));
    }

    [Fact]
    public void GoodSegment_ResetsConsecutiveWindow()
    {
        var path = $"/view/reset-{Guid.NewGuid():N}.mkv";
        var miss = new UsenetArticleNotFoundException("a@test");
        PlaybackHoleTracker.RecordHole(path, "a@test", miss);
        PlaybackHoleTracker.RecordHole(path, "b@test", miss);
        PlaybackHoleTracker.RecordGoodSegment(path);
        PlaybackHoleTracker.RecordHole(path, "c@test", miss);
        PlaybackHoleTracker.RecordHole(path, "d@test", miss);

        Assert.False(PlaybackHoleTracker.ShouldFailFast(path, out _));
        Assert.True(PlaybackHoleTracker.IsKnownMissingSegment(path, "a@test"));
    }

    [Fact]
    public void SlidingWindow_ExpiresOldHoles()
    {
        var path = $"/view/window-{Guid.NewGuid():N}.mkv";
        var clock = new ManualTimeProvider();
        PlaybackHoleTracker.Clock = clock;
        var miss = new UsenetArticleNotFoundException("a@test");
        PlaybackHoleTracker.RecordHole(path, "a@test", miss);
        PlaybackHoleTracker.RecordHole(path, "b@test", miss);
        PlaybackHoleTracker.RecordHole(path, "c@test", miss);
        Assert.True(PlaybackHoleTracker.ShouldFailFast(path, out _));

        clock.Advance(PlaybackHoleTracker.ConsecutiveWindow + TimeSpan.FromSeconds(1));
        Assert.False(PlaybackHoleTracker.ShouldFailFast(path, out _));
        Assert.True(PlaybackHoleTracker.IsKnownMissingSegment(path, "a@test"));
    }

    [Fact]
    public void StaleEntries_AreEvictedOnPeriodicCleanup()
    {
        var stale = $"/view/stale-{Guid.NewGuid():N}.mkv";
        var clock = new ManualTimeProvider();
        PlaybackHoleTracker.Clock = clock;
        var miss = new UsenetArticleNotFoundException("stale@test");
        PlaybackHoleTracker.RecordHole(stale, "stale@test", miss);
        clock.Advance(PlaybackHoleTracker.CleanupThreshold + TimeSpan.FromSeconds(1));

        for (var i = 0; i < 255; i++)
        {
            PlaybackHoleTracker.RecordHole(
                $"/view/other-{Guid.NewGuid():N}.mkv",
                "other@test",
                miss);
        }

        Assert.False(PlaybackHoleTracker.IsKnownMissingSegment(stale, "stale@test"));
    }

    [Fact]
    public void BasenameFileNames_AreNotTracked()
    {
        var miss = new UsenetArticleNotFoundException("movie@test");
        PlaybackHoleTracker.RecordHole("movie.mkv", "movie@test", miss);
        PlaybackHoleTracker.RecordHole("movie.mkv", "movie2@test", miss);
        PlaybackHoleTracker.RecordHole("movie.mkv", "movie3@test", miss);

        Assert.False(PlaybackHoleTracker.ShouldFailFast("movie.mkv", out _));
        Assert.False(PlaybackHoleTracker.IsKnownMissingSegment("movie.mkv", "movie@test"));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
