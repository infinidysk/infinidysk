using NzbWebDAV.Config;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class HealthCheckSampleSegmentsTests
{
    // Named for the default rather than the member, so these cases keep testing whatever
    // depth ships as the default.
    private const HealthCheckDepth Standard = ConfigManager.DefaultHealthCheckDepth;

    private static List<string> Segments(int count) =>
        Enumerable.Range(0, count).Select(i => $"seg-{i}").ToList();

    [Theory]
    [InlineData(100)]
    [InlineData(4000)]
    [InlineData(HealthCheckService.SampleFloor)]
    public void SampleSegments_ChecksSmallFilesInFull(int count)
    {
        var segments = Segments(count);

        Assert.Same(segments, HealthCheckService.SampleSegments(segments, Standard));
    }

    [Fact]
    public void SampleTarget_FloorAppliesBeforeAgingNotAfter()
    {
        const int count = HealthCheckService.SampleFloor;

        var whenNew = HealthCheckService.SampleTarget(count, Standard, TimeSpan.Zero);
        var whenOld = HealthCheckService.SampleTarget(count, Standard, TimeSpan.FromDays(3650));

        // A new file at the floor is checked in full, but the floor is the curve's
        // minimum rather than a guarantee that survives aging.
        Assert.Equal(count, whenNew);
        Assert.InRange(100.0 * whenOld / count, 31, 33);
    }

    [Fact]
    public void SampleSegments_CompleteChecksEverySegment()
    {
        var segments = Segments(50_000);

        Assert.Same(segments, HealthCheckService.SampleSegments(segments, HealthCheckDepth.Complete));
    }

    [Fact]
    public void SampleSegments_StratifiesLargeFilesAndPreservesOrder()
    {
        var segments = Segments(50_000);

        var sampled = HealthCheckService.SampleSegments(segments, Standard);

        Assert.True(sampled.Count < segments.Count);
        Assert.Equal("seg-0", sampled[0]);
        Assert.Equal("seg-49999", sampled[^1]);
        Assert.Equal(sampled, sampled.OrderBy(s => int.Parse(s["seg-".Length..])).ToList());
    }

    [Fact]
    public void SampleSegments_IncludesHeadAndTail()
    {
        var segments = Segments(50_000);

        var sampled = HealthCheckService.SampleSegments(segments, Standard);
        var indices = sampled.Select(s => int.Parse(s["seg-".Length..])).ToHashSet();

        for (var i = 0; i < 100; i++)
            Assert.Contains(i, indices);
        for (var i = 49_900; i < 50_000; i++)
            Assert.Contains(i, indices);
    }

    [Fact]
    public void ConcatenatedSegmentView_SkipsEmptyPartsWhilePreservingIndexes()
    {
        var segments = new HealthCheckService.ConcatenatedSegmentView(
        [
            ["first"],
            [],
            ["second", "third"],
        ]);

        Assert.Equal(["first", "second", "third"], segments);
        Assert.Equal("second", segments[1]);
        Assert.Equal("third", segments[2]);
    }

    [Fact]
    public void SampleSegments_CoverageTapersInsteadOfSteppingDown()
    {
        double Coverage(int count) =>
            (double)HealthCheckService.SampleSegments(Segments(count), Standard).Count / count;

        var justUnder = Coverage(HealthCheckService.SampleFloor - 100);
        var justOver = Coverage(HealthCheckService.SampleFloor + 100);
        Assert.True(justUnder - justOver < 0.05, $"step of {justUnder - justOver:P0} at the floor");

        var sizes = new[] { 10_000, 20_000, 40_000, 80_000 };
        var coverages = sizes.Select(Coverage).ToList();
        Assert.Equal(coverages.OrderByDescending(x => x), coverages);
    }

    [Theory]
    [InlineData(7_900, 100)]
    [InlineData(8_000, 100)]
    [InlineData(10_000, 80)]
    [InlineData(20_000, 40)]
    [InlineData(50_000, 20)]
    [InlineData(100_000, 14)]
    public void SampleTarget_StandardFollowsTheDocumentedCurve(int count, int expectedPercent)
    {
        var percent = 100.0 * HealthCheckService.SampleTarget(count, Standard) / count;

        Assert.InRange(percent, expectedPercent - 1, expectedPercent + 1);
    }

    [Theory]
    [InlineData(0, 40)]
    [InlineData(365, 40)]
    [InlineData(730, 28)]
    [InlineData(1825, 18)]
    [InlineData(3650, 13)]
    [InlineData(7300, 13)]
    public void SampleTarget_TapersWithAgeThenHolds(int ageDays, int expectedPercent)
    {
        const int count = 20_000;

        var target = HealthCheckService.SampleTarget(count, Standard, TimeSpan.FromDays(ageDays));

        Assert.InRange(100.0 * target / count, expectedPercent - 1, expectedPercent + 1);
    }

    [Fact]
    public void SampleTarget_CompleteIgnoresAge()
    {
        const int count = 20_000;

        var ancient = HealthCheckService.SampleTarget(count, HealthCheckDepth.Complete, TimeSpan.FromDays(7300));

        Assert.Equal(count, ancient);
    }

    [Fact]
    public void SampleSegments_CompleteIgnoresAge()
    {
        var segments = Segments(50_000);

        var sampled = HealthCheckService.SampleSegments(segments, HealthCheckDepth.Complete, TimeSpan.FromDays(7300));

        Assert.Same(segments, sampled);
    }

    [Fact]
    public void SampleTarget_UnknownAgeGetsFullDepth()
    {
        var known = HealthCheckService.SampleTarget(20_000, Standard, TimeSpan.Zero);
        var unknown = HealthCheckService.SampleTarget(20_000, Standard, age: null);

        Assert.Equal(known, unknown);
    }

    [Fact]
    public void SampleTarget_WithAgingDisabledMatchesANewRelease()
    {
        // Disabling the taper means the caller passes no age, so an ancient release is
        // sampled exactly as deeply as a fresh one of the same size.
        var aged = HealthCheckService.SampleTarget(20_000, Standard, TimeSpan.FromDays(3650));
        var ageless = HealthCheckService.SampleTarget(20_000, Standard, age: null);
        var fresh = HealthCheckService.SampleTarget(20_000, Standard, TimeSpan.Zero);

        Assert.Equal(fresh, ageless);
        Assert.True(aged < ageless, $"aged {aged} should sample less than ageless {ageless}");
    }

    [Theory]
    [InlineData(20_000)]
    [InlineData(50_000)]
    [InlineData(100_000)]
    public void SampleSegments_DeeperSettingsCheckMore(int count)
    {
        var segments = Segments(count);

        var standard = HealthCheckService.SampleSegments(segments, Standard).Count;
        var enhanced = HealthCheckService.SampleSegments(segments, HealthCheckDepth.Enhanced).Count;
        var deep = HealthCheckService.SampleSegments(segments, HealthCheckDepth.Deep).Count;

        Assert.True(standard < enhanced, $"standard {standard} !< enhanced {enhanced}");
        Assert.True(enhanced < deep, $"enhanced {enhanced} !< deep {deep}");
        Assert.True(deep <= count);
    }
}
