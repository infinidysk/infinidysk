using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class HealthCheckToleratedMissingTests
{
    [Theory]
    [InlineData("movie.mkv")]
    [InlineData("movie.mp4")]
    [InlineData("movie.iso")]
    [InlineData("movie.rar")]
    public void LenientOff_ToleratesNothing(string filename)
    {
        Assert.Equal(0, HealthCheckService.ToleratedMissingArticles(
            filename, 10_000, lenient: false, carriesOwnBytes: true));
    }

    [Theory]
    [InlineData("movie.mp4")]
    [InlineData("movie.m4v")]
    [InlineData("movie.mov")]
    [InlineData("movie.mkv")]
    [InlineData("movie.webm")]
    [InlineData("MOVIE.MKV")]
    public void PlainVideoContainers_AreEligible(string filename)
    {
        Assert.True(HealthCheckService.ToleratedMissingArticles(
            filename, 10_000, lenient: true, carriesOwnBytes: true) > 0);
    }

    [Theory]
    [InlineData("disc.iso")]
    [InlineData("disc.img")]
    [InlineData("playlist.m3u")]
    [InlineData("stream.ts")]
    [InlineData("movie.avi")]
    [InlineData("archive.rar")]
    [InlineData("archive.r01")]
    [InlineData("noextension")]
    public void EverythingElse_ToleratesNothing(string filename)
    {
        Assert.Equal(0, HealthCheckService.ToleratedMissingArticles(
            filename, 10_000, lenient: true, carriesOwnBytes: true));
    }

    [Theory]
    [InlineData("movie.mkv")]
    [InlineData("movie.mp4")]
    public void ArchivedOrEncryptedBytes_ToleratesNothing(string filename)
    {
        // The aggregators name an archive member after the file inside the archive, so the
        // extension alone would let a rar-packed release through.
        Assert.Equal(0, HealthCheckService.ToleratedMissingArticles(
            filename, 10_000, lenient: true, carriesOwnBytes: false));
    }

    [Theory]
    [InlineData(100, 2)]
    [InlineData(1_000, 20)]
    [InlineData(3_200, 64)]
    public void BelowTheCrossover_TheRatioBinds(int totalSegments, int expected)
    {
        Assert.Equal(expected, HealthCheckService.ToleratedMissingArticles(
            "movie.mkv", totalSegments, lenient: true, carriesOwnBytes: true));
    }

    [Theory]
    [InlineData(3_201)]
    [InlineData(32_000)]
    [InlineData(75_000)]
    public void AboveTheCrossover_TheCeilingBinds(int totalSegments)
    {
        Assert.Equal(64, HealthCheckService.ToleratedMissingArticles(
            "movie.mkv", totalSegments, lenient: true, carriesOwnBytes: true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveSegmentCount_ToleratesNothing(int totalSegments)
    {
        Assert.Equal(0, HealthCheckService.ToleratedMissingArticles(
            "movie.mkv", totalSegments, lenient: true, carriesOwnBytes: true));
    }

    [Fact]
    public void VerySmallFiles_ToleratesNothing()
    {
        // Two percent of 49 truncates to zero, so a tiny file keeps failing on its first miss.
        Assert.Equal(0, HealthCheckService.ToleratedMissingArticles(
            "movie.mkv", 49, lenient: true, carriesOwnBytes: true));
    }

    [Fact]
    public void NothingMissing_IsReadable()
    {
        Assert.Null(HealthCheckService.FindUnreadableDamage([], 1_000));
    }

    [Theory]
    [InlineData(new[] { 4 })]
    [InlineData(new[] { 4, 9, 400 })]
    [InlineData(new[] { 4, 5 })]
    [InlineData(new[] { 998, 4, 5, 100 })]
    public void ScatteredMissingArticlesAwayFromBothEnds_AreReadable(int[] missing)
    {
        Assert.Null(HealthCheckService.FindUnreadableDamage(missing, 1_000));
    }

    [Fact]
    public void FirstArticleMissing_IsUnreadable()
    {
        // A read fails outright on the first article rather than zero-filling it, so tolerating
        // this would pass a file that errors the moment anyone plays it from the start.
        var damage = HealthCheckService.FindUnreadableDamage([0], 1_000);

        Assert.NotNull(damage);
        Assert.Equal(0, damage.Value.Index);
    }

    [Fact]
    public void LastArticleMissing_IsUnreadable()
    {
        var damage = HealthCheckService.FindUnreadableDamage([999], 1_000);

        Assert.NotNull(damage);
        Assert.Equal(999, damage.Value.Index);
    }

    [Fact]
    public void TwoInARow_IsReadableButThreeIsNot()
    {
        // A read serves two zero-filled articles and drops the stream on the third, so the check
        // has to draw the line in the same place.
        Assert.Null(HealthCheckService.FindUnreadableDamage([40, 41], 1_000));

        var damage = HealthCheckService.FindUnreadableDamage([41, 40, 42], 1_000);

        Assert.NotNull(damage);
        Assert.Equal(40, damage.Value.Index);
    }

    [Fact]
    public void NoMissingArticles_HasNoRun()
    {
        Assert.Equal((0, -1), HealthCheckService.LongestMissingRun([]));
    }

    [Theory]
    [InlineData(new[] { 5 }, 1, 5)]
    [InlineData(new[] { 5, 9, 20 }, 1, 5)]
    [InlineData(new[] { 5, 6 }, 2, 5)]
    [InlineData(new[] { 5, 6, 7 }, 3, 5)]
    [InlineData(new[] { 20, 3, 21, 2, 22 }, 3, 20)]
    [InlineData(new[] { 1, 2, 8, 9, 10, 11 }, 4, 8)]
    [InlineData(new[] { 7, 7, 8, 8, 9 }, 3, 7)]
    public void RunLengthAndStartCountConsecutivePositions(int[] missing, int length, int start)
    {
        // Positions arrive in completion order, so the unsorted cases matter.
        Assert.Equal((length, start), HealthCheckService.LongestMissingRun(missing));
    }

    [Fact]
    public void SampleIndexesCoverEverySegmentWhenNothingIsSampledAway()
    {
        var segments = Enumerable.Range(0, 500).Select(i => $"seg{i}").ToList();

        Assert.Equal(Enumerable.Range(0, 500), HealthCheckService.SampleSegmentIndexes(segments));
    }

    [Fact]
    public void SampleIndexesAddressTheSameSegmentsTheSamplerPicks()
    {
        var segments = Enumerable.Range(0, 200_000).Select(i => $"seg{i}").ToList();

        var indexes = HealthCheckService.SampleSegmentIndexes(segments);

        // The mapping is what lets a missing position become a file position, so pin it against
        // the sampler itself rather than only checking the indexes look plausible.
        Assert.Equal(HealthCheckService.SampleSegments(segments), indexes.Select(i => segments[i]));
        Assert.Equal(indexes.OrderBy(i => i), indexes);
    }
}
