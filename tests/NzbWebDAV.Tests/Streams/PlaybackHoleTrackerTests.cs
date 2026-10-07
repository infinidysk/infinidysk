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
        var config = ToleranceConfig();
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
        PlaybackHoleTracker.SetDamageBudget(path, PlaybackDamageBudget.TryCreate(path, nzb, ToleranceConfig()));

        RecordIsolatedHole(path, nzb.SegmentIds[0]);
        Assert.True(PlaybackHoleTracker.ShouldFailFast(path, out _));
    }

    [Fact]
    public void DamageBudget_FailsRunsLongerThanTheConsecutiveCap()
    {
        var path = $"/view/run-{Guid.NewGuid():N}.mkv";
        var nzb = BudgetFile(segments: 1000);
        PlaybackHoleTracker.SetDamageBudget(path, PlaybackDamageBudget.TryCreate(path, nzb, ToleranceConfig()));

        Assert.Equal(3, PlaybackHoleTracker.ConsecutiveFillLimit(path));
        var miss = new UsenetArticleNotFoundException("run@test");
        for (var i = 10; i < 12; i++)
            PlaybackHoleTracker.RecordHole(path, nzb.SegmentIds[i], miss);
        Assert.False(PlaybackHoleTracker.ShouldFailFast(path, out _));

        PlaybackHoleTracker.RecordHole(path, nzb.SegmentIds[12], miss);
        Assert.True(PlaybackHoleTracker.ShouldFailFast(path, out _));
    }

    [Fact]
    public void ExceededBudget_SurvivesLaterHealthySegments()
    {
        var path = $"/view/buffered-{Guid.NewGuid():N}.mkv";
        var nzb = BudgetFile(segments: 1000);
        PlaybackHoleTracker.SetDamageBudget(path, PlaybackDamageBudget.TryCreate(path, nzb, TotalCapConfig(1)));

        RecordIsolatedHole(path, nzb.SegmentIds[100]);
        var tipping = new UsenetArticleNotFoundException(nzb.SegmentIds[200]);
        PlaybackHoleTracker.RecordHole(path, nzb.SegmentIds[200], tipping);
        // A consumer accepting an earlier prefetched healthy segment must not hide the cumulative failure.
        PlaybackHoleTracker.RecordGoodSegment(path);

        Assert.True(PlaybackHoleTracker.ShouldFailFast(path, out var failure));
        Assert.Same(tipping, failure);
    }

    [Fact]
    public void ExceededBudget_ExpiresWithTheObservationWindow()
    {
        var path = $"/view/expired-{Guid.NewGuid():N}.mkv";
        var clock = new ManualTimeProvider();
        PlaybackHoleTracker.Clock = clock;
        var nzb = BudgetFile(segments: 1000);
        var budget = PlaybackDamageBudget.TryCreate(path, nzb, TotalCapConfig(1));
        PlaybackHoleTracker.SetDamageBudget(path, budget);
        RecordIsolatedHole(path, nzb.SegmentIds[100]);
        RecordIsolatedHole(path, nzb.SegmentIds[200]);
        Assert.True(PlaybackHoleTracker.ShouldFailFast(path, out _));

        clock.Advance(PlaybackHoleTracker.CleanupThreshold + TimeSpan.FromSeconds(1));

        PlaybackHoleTracker.SetDamageBudget(path, budget);
        Assert.False(PlaybackHoleTracker.ShouldFailFast(path, out _));

        // Reopening slices the stream, which snapshots known holes; that must not drop the new budget.
        Assert.Null(PlaybackHoleTracker.SnapshotMissingSegmentIds(path));
        Assert.Equal(budget!.ConsecutiveFillLimit, PlaybackHoleTracker.ConsecutiveFillLimit(path));
        RecordIsolatedHole(path, nzb.SegmentIds[300]);
        Assert.False(PlaybackHoleTracker.ShouldFailFast(path, out _));
        RecordIsolatedHole(path, nzb.SegmentIds[400]);
        Assert.True(PlaybackHoleTracker.ShouldFailFast(path, out _));
    }

    [Fact]
    public void RepeatedReopens_DoNotExtendTheFailureWindow()
    {
        var path = $"/view/retry-{Guid.NewGuid():N}.mkv";
        var clock = new ManualTimeProvider();
        PlaybackHoleTracker.Clock = clock;
        var nzb = BudgetFile(segments: 1000);
        var budget = PlaybackDamageBudget.TryCreate(path, nzb, TotalCapConfig(1));
        PlaybackHoleTracker.SetDamageBudget(path, budget);
        RecordIsolatedHole(path, nzb.SegmentIds[100]);
        RecordIsolatedHole(path, nzb.SegmentIds[200]);

        for (var minute = 0; minute < 6; minute++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            PlaybackHoleTracker.SetDamageBudget(path, budget);
        }

        Assert.False(PlaybackHoleTracker.ShouldFailFast(path, out _));
    }

    [Fact]
    public void StaleBudget_SurvivesLookupsWithoutReinstall()
    {
        var path = $"/view/idle-{Guid.NewGuid():N}.mkv";
        var clock = new ManualTimeProvider();
        PlaybackHoleTracker.Clock = clock;
        var nzb = BudgetFile(segments: 1000);
        var budget = PlaybackDamageBudget.TryCreate(path, nzb, TotalCapConfig(1));
        PlaybackHoleTracker.SetDamageBudget(path, budget);

        clock.Advance(PlaybackHoleTracker.CleanupThreshold + TimeSpan.FromSeconds(1));

        Assert.False(PlaybackHoleTracker.IsKnownMissingSegment(path, nzb.SegmentIds[100]));
        Assert.Null(PlaybackHoleTracker.SnapshotMissingSegmentIds(path));
        RecordIsolatedHole(path, nzb.SegmentIds[100]);
        RecordIsolatedHole(path, nzb.SegmentIds[200]);
        Assert.True(PlaybackHoleTracker.ShouldFailFast(path, out _));
    }

    [Fact]
    public void RemovingBudget_ClearsItsFailure()
    {
        var path = $"/view/removed-{Guid.NewGuid():N}.mkv";
        var nzb = BudgetFile(segments: 1000);
        PlaybackHoleTracker.SetDamageBudget(path, PlaybackDamageBudget.TryCreate(path, nzb, TotalCapConfig(1)));
        RecordIsolatedHole(path, nzb.SegmentIds[100]);
        RecordIsolatedHole(path, nzb.SegmentIds[200]);
        Assert.True(PlaybackHoleTracker.ShouldFailFast(path, out _));

        PlaybackHoleTracker.SetDamageBudget(path, null);
        PlaybackHoleTracker.RecordGoodSegment(path);

        Assert.False(PlaybackHoleTracker.ShouldFailFast(path, out _));
    }

    [Fact]
    public void DamageBudget_CountsRecordedCorruption()
    {
        var path = $"/view/corrupt-{Guid.NewGuid():N}.mkv";
        var nzb = BudgetFile(segments: 1000);
        nzb.CorruptSegmentIndices = [100, 200, 300];
        PlaybackHoleTracker.SetDamageBudget(path, PlaybackDamageBudget.TryCreate(path, nzb, TotalCapConfig(3)));

        RecordIsolatedHole(path, nzb.SegmentIds[400]);

        Assert.True(PlaybackHoleTracker.ShouldFailFast(path, out _));
    }

    [Fact]
    public void LegacyFileWithoutSegmentRanges_HasNoBudget()
    {
        var path = $"/view/legacy-{Guid.NewGuid():N}.mkv";
        var nzb = BudgetFile(segments: 10);
        nzb.SegmentByteRanges = null;

        Assert.Null(PlaybackDamageBudget.TryCreate(path, nzb, ToleranceConfig()));
        Assert.False(PlaybackDamageBudget.Applies(path, nzb, ToleranceConfig()));
    }

    [Fact]
    public void IneligibleFile_KeepsFixedConsecutiveLimit()
    {
        var path = $"/view/plain-{Guid.NewGuid():N}.avi";
        Assert.Null(PlaybackDamageBudget.TryCreate(path, BudgetFile(segments: 10), ToleranceConfig()));
        Assert.Equal(GapFillLimits.MaxConsecutiveZeroFills, PlaybackHoleTracker.ConsecutiveFillLimit(path));
    }

    [Fact]
    public void ToleranceOff_HasNoBudget()
    {
        var path = $"/view/off-{Guid.NewGuid():N}.mkv";
        var config = new NzbWebDAV.Config.ConfigManager();
        config.UpdateValues([
            new NzbWebDAV.Database.Models.ConfigItem { ConfigName = NzbWebDAV.Config.ConfigKeys.RepairDegradedToleranceEnabled, ConfigValue = "false" },
        ]);
        Assert.Null(PlaybackDamageBudget.TryCreate(path, BudgetFile(segments: 10), config));
    }

    private static NzbWebDAV.Config.ConfigManager ToleranceConfig()
    {
        var config = new NzbWebDAV.Config.ConfigManager();
        config.UpdateValues([
            new NzbWebDAV.Database.Models.ConfigItem { ConfigName = NzbWebDAV.Config.ConfigKeys.RepairDegradedToleranceEnabled, ConfigValue = "true" },
        ]);
        return config;
    }

    private static NzbWebDAV.Config.ConfigManager TotalCapConfig(int maxTotalMissing)
    {
        var config = ToleranceConfig();
        config.UpdateValues([
            new NzbWebDAV.Database.Models.ConfigItem { ConfigName = NzbWebDAV.Config.ConfigKeys.RepairDegradedMaxTotalMissing, ConfigValue = maxTotalMissing.ToString() },
        ]);
        return config;
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

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void PeriodicSweep_KeepsBudgetOnlyWhileAStreamIsOpen(bool closeStream, bool expectBudget)
    {
        var path = $"/view/sweep-{Guid.NewGuid():N}.mkv";
        var clock = new ManualTimeProvider();
        PlaybackHoleTracker.Clock = clock;
        var nzb = BudgetFile(segments: 1000);
        var lease = PlaybackHoleTracker.SetDamageBudget(
            path, PlaybackDamageBudget.TryCreate(path, nzb, TotalCapConfig(1)));
        Assert.NotNull(lease);
        if (closeStream)
            lease.Dispose();
        clock.Advance(PlaybackHoleTracker.CleanupThreshold + TimeSpan.FromSeconds(1));

        var miss = new UsenetArticleNotFoundException("other@test");
        for (var i = 0; i < 256; i++)
            PlaybackHoleTracker.RecordHole($"/view/other-{Guid.NewGuid():N}.mkv", "other@test", miss);

        RecordIsolatedHole(path, nzb.SegmentIds[100]);
        RecordIsolatedHole(path, nzb.SegmentIds[200]);
        Assert.Equal(expectBudget, PlaybackHoleTracker.ShouldFailFast(path, out _));
        lease.Dispose();
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
