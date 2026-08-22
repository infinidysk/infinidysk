using NzbWebDAV.Config;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class ProfileResultSorterTests
{
    [Fact]
    public void Sort_Off_ReproducesGrabsSizeDateOrder()
    {
        var items = new[]
        {
            new Release("Movie.2024.2160p.WEB-DL", Grabs: 1, Size: 10, Posted: DateTimeOffset.UnixEpoch),
            new Release("Movie.2024.1080p.BluRay", Grabs: 50, Size: 20, Posted: DateTimeOffset.UnixEpoch.AddDays(1)),
            new Release("Movie.2024.720p.WEB-DL", Grabs: 50, Size: 30, Posted: DateTimeOffset.UnixEpoch),
        };

        var sorted = Sort(items, ProfileConfig.QualitySortMode.Off, preferDownloaded: true);

        Assert.Equal(
            ["Movie.2024.720p.WEB-DL", "Movie.2024.1080p.BluRay", "Movie.2024.2160p.WEB-DL"],
            sorted.Select(x => x.Title));
    }

    [Fact]
    public void Sort_Resolution_IgnoresSourceRank()
    {
        var items = new[]
        {
            new Release("Movie.2024.1080p.REMUX", 100, 50, DateTimeOffset.UnixEpoch),
            new Release("Movie.2024.2160p.HDTV", 1, 10, DateTimeOffset.UnixEpoch),
            new Release("Movie.2024.1080p.WEB-DL", 500, 40, DateTimeOffset.UnixEpoch),
        };

        var sorted = Sort(items, ProfileConfig.QualitySortMode.Resolution, preferDownloaded: true);

        Assert.Equal(
            ["Movie.2024.2160p.HDTV", "Movie.2024.1080p.WEB-DL", "Movie.2024.1080p.REMUX"],
            sorted.Select(x => x.Title));
    }

    [Fact]
    public void Sort_ResolutionAndSource_AppliesSourceOnlyWithinResolution()
    {
        var items = new[]
        {
            new Release("Movie.2024.1080p.REMUX", 100, 50, DateTimeOffset.UnixEpoch),
            new Release("Movie.2024.2160p.HDTV", 1, 10, DateTimeOffset.UnixEpoch),
            new Release("Movie.2024.1080p.WEB-DL", 500, 40, DateTimeOffset.UnixEpoch),
        };

        var sorted = Sort(items, ProfileConfig.QualitySortMode.ResolutionAndSource);

        Assert.Equal(
            ["Movie.2024.2160p.HDTV", "Movie.2024.1080p.REMUX", "Movie.2024.1080p.WEB-DL"],
            sorted.Select(x => x.Title));
    }

    [Fact]
    public void Sort_FallbackMergedResults_UseTheSameOrdering()
    {
        var initial = new[]
        {
            new Release("Movie.2024.720p.WEB-DL", 1, 10, DateTimeOffset.UnixEpoch),
        };
        var fallback = new[]
        {
            new Release("Movie.2024.2160p.WEB-DL", 1, 10, DateTimeOffset.UnixEpoch),
            new Release("Movie.2024.1080p.BluRay", 1, 20, DateTimeOffset.UnixEpoch),
        };

        var sorted = Sort(initial.Concat(fallback), ProfileConfig.QualitySortMode.ResolutionAndSource);

        Assert.Equal(
            ["Movie.2024.2160p.WEB-DL", "Movie.2024.1080p.BluRay", "Movie.2024.720p.WEB-DL"],
            sorted.Select(x => x.Title));
    }

    [Fact]
    public void Sort_DoesNotReorderAfterWatchtowerBoost()
    {
        var ordinary = Sort(
            [
                new Release("Movie.2024.1080p.WEB-DL", 1, 10, DateTimeOffset.UnixEpoch),
                new Release("Movie.2024.2160p.WEB-DL", 1, 10, DateTimeOffset.UnixEpoch),
            ],
            ProfileConfig.QualitySortMode.Resolution);

        var watchtowerFirst = new Release("Movie.2024.720p.HDTV", 1, 5, DateTimeOffset.UnixEpoch);
        var merged = new[] { watchtowerFirst }.Concat(ordinary).ToList();

        Assert.Equal("Movie.2024.720p.HDTV", merged[0].Title);
        Assert.Equal("Movie.2024.2160p.WEB-DL", merged[1].Title);
    }

    private static List<Release> Sort(
        IEnumerable<Release> items,
        ProfileConfig.QualitySortMode mode,
        bool preferDownloaded = false) =>
        ProfileResultSorter.Sort(
            items,
            item => item.Title,
            item => item.Grabs,
            item => item.Size,
            item => item.Posted,
            mode,
            preferDownloaded);

    private sealed record Release(string Title, int Grabs, long Size, DateTimeOffset Posted);
}
