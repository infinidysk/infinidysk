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
    public void DamageBudget_PadsScatteredHolesUntilTotalCapIsReached()
    {
        var path = $"/view/budget-{Guid.NewGuid():N}.mkv";
        var nzb = BudgetFile(segments: 1000);
        var config = new NzbWebDAV.Config.ConfigManager();
        config.UpdateValues([
            new NzbWebDAV.Database.Models.ConfigItem { ConfigName = NzbWebDAV.Config.ConfigKeys.RepairDegradedMaxTotalMissing, ConfigValue = "3" },
        ]);
        PlaybackHoleTracker.SetDamageBudget(path, PlaybackDamageBudget.TryCreate(path, nzb, config));

        for (var i = 1; i <= 3; i++)
        {
            RecordIsolatedHole(path, nzb.SegmentIds[i * 100]);
            Assert.False(PlaybackHoleTracker.ShouldFailFast(path, out _));
        }

        RecordIsolatedHole(path, nzb.SegmentIds[400]);
        Assert.True(PlaybackHoleTracker.ShouldFailFast(path, out var failure));
        Assert.NotNull(failure);
    }

    [Fact]
    public void DamageBudget_FailsOnFirstSegmentHole()
    {
        var path = $"/view/head-{Guid.NewGuid():N}.mkv";
        var nzb = BudgetFile(segments: 100);
        PlaybackHoleTracker.SetDamageBudget(path, PlaybackDamageBudget.TryCreate(path, nzb, new NzbWebDAV.Config.ConfigManager()));

        RecordIsolatedHole(path, nzb.SegmentIds[0]);
        Assert.True(PlaybackHoleTracker.ShouldFailFast(path, out _));
    }

    [Fact]
    public void DamageBudget_AllowsLongerRunsThanTheFixedLimit()
    {
        var path = $"/view/run-{Guid.NewGuid():N}.mkv";
        var nzb = BudgetFile(segments: 1000);
        PlaybackHoleTracker.SetDamageBudget(path, PlaybackDamageBudget.TryCreate(path, nzb, new NzbWebDAV.Config.ConfigManager()));

        Assert.Equal(5, PlaybackHoleTracker.ConsecutiveFillLimit(path));
        var miss = new UsenetArticleNotFoundException("run@test");
        for (var i = 10; i < 14; i++)
            PlaybackHoleTracker.RecordHole(path, nzb.SegmentIds[i], miss);
        Assert.False(PlaybackHoleTracker.ShouldFailFast(path, out _));

        PlaybackHoleTracker.RecordHole(path, nzb.SegmentIds[14], miss);
        Assert.True(PlaybackHoleTracker.ShouldFailFast(path, out _));
    }

    [Fact]
    public void IneligibleFile_KeepsFixedConsecutiveLimit()
    {
        var path = $"/view/plain-{Guid.NewGuid():N}.avi";
        Assert.Null(PlaybackDamageBudget.TryCreate(path, BudgetFile(segments: 10), new NzbWebDAV.Config.ConfigManager()));
        Assert.Equal(GapFillLimits.MaxConsecutiveZeroFills, PlaybackHoleTracker.ConsecutiveFillLimit(path));
    }

    private static void RecordIsolatedHole(string path, string segmentId)
    {
        PlaybackHoleTracker.RecordGoodSegment(path);
        PlaybackHoleTracker.RecordHole(path, segmentId, new UsenetArticleNotFoundException(segmentId));
    }

    private static NzbWebDAV.Database.Models.DavNzbFile BudgetFile(int segments)
    {
        const long size = 700_000;
        return new NzbWebDAV.Database.Models.DavNzbFile
        {
            SegmentIds = Enumerable.Range(0, segments).Select(i => $"seg{i}@test").ToArray(),
            SegmentByteRanges = Enumerable.Range(0, segments)
                .Select(i => new NzbWebDAV.Models.LongRange(i * size, (i + 1) * size))
                .ToArray(),
        };
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
    public void StaleEntries_ExpireOnReadWithoutFurtherHoles()
    {
        var path = $"/view/stale-read-{Guid.NewGuid():N}.mkv";
        var clock = new ManualTimeProvider();
        PlaybackHoleTracker.Clock = clock;
        var miss = new UsenetArticleNotFoundException("stale@test");
        PlaybackHoleTracker.RecordHole(path, "stale@test", miss);
        Assert.True(PlaybackHoleTracker.IsKnownMissingSegment(path, "stale@test"));

        clock.Advance(PlaybackHoleTracker.CleanupThreshold + TimeSpan.FromSeconds(1));

        Assert.False(PlaybackHoleTracker.IsKnownMissingSegment(path, "stale@test"));
        Assert.Null(PlaybackHoleTracker.SnapshotMissingSegmentIds(path));
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
