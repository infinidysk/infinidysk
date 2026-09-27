using NzbWebDAV.Services.Plex;
using NzbWebDAV.Services.Prefetch;

namespace NzbWebDAV.Tests.Services;

public sealed class PrefetchPolicyTests
{
    [Fact]
    public void SourcePreview_ExplainsMappingAndExclusionsWithoutClaimingImportedCoverage()
    {
        var item = Episode("next", 1, 2);
        PlexPathMapping[] mappings = [new("/plex", "/dav")];
        Assert.Equal("mapped", PrefetchPolicy.DescribeSourceCandidate(item, new(), mappings).Status);
        Assert.Equal("unmapped", PrefetchPolicy.DescribeSourceCandidate(item, new(), []).Status);
        Assert.Equal("excluded", PrefetchPolicy.DescribeSourceCandidate(item, new(), mappings, new() { ExcludedShows = ["show"] }).Status);
        Assert.Equal("episodes-required", PrefetchPolicy.DescribeSourceCandidate(item with { Type = "show", File = null }, new(), mappings).Status);
    }
    [Theory]
    [InlineData("/plex/movies/A.mkv", "/dav/A.mkv")]
    [InlineData("/plex/movies-other/A.mkv", null)]
    [InlineData("/plex/movies/../secret", null)]
    public void PathMapping_RequiresExactSegmentBoundary(string path, string? expected)
    {
        Assert.Equal(expected, PrefetchPathResolver.Map(path, [new("/plex/movies", "/dav")] )?.DavPath);
    }

    [Fact]
    public void ConflictingMappings_AreRejectedInsteadOfGuessing()
    {
        Assert.Null(PrefetchPathResolver.Map("/plex/A.mkv", [new("/plex", "/one"), new("/plex", "/two")]));
    }

    [Fact]
    public void HistoryPrediction_RequiresDistinctEpisodesAndServerScopedUserSelection()
    {
        var now = DateTimeOffset.UtcNow;
        var settings = new PrefetchSettings { MinEpisodesForPrediction = 2, Users = ["server:user"] };
        var first = Episode("first", 1, 1) with { ViewedAt = now.ToUnixTimeSeconds(), UserId = "user" };
        var second = Episode("second", 1, 2) with { ViewedAt = now.ToUnixTimeSeconds() + 1, UserId = "user" };
        Assert.Empty(PrefetchPolicy.HistoryCandidates([first, first], settings, "server", now));
        Assert.Empty(PrefetchPolicy.HistoryCandidates([first, second], settings, "other-server", now));
        Assert.Equal("second", Assert.Single(PrefetchPolicy.HistoryCandidates([first, second], settings, "server", now)).RatingKey);
        Assert.Empty(PrefetchPolicy.HistoryCandidates([first, second], settings with { ConfidenceThreshold = 0.9 }, "server", now));
    }

    [Fact]
    public void PartialResumeAndMinimumRanges_AreBoundedWithinMedia()
    {
        var settings = new PrefetchSettings { FullFileWarming = false, MinimumHeadMb = 4, MinimumTailMb = 4 };
        var ranges = PrefetchPolicy.Ranges(100_000_000, 90, 100, settings, minimum: false);
        Assert.Single(ranges);
        Assert.InRange(ranges[0].Start, 0, 99_999_999);
        Assert.InRange(ranges[0].Length, 1, 100_000_000 - ranges[0].Start);
        var minimum = PrefetchPolicy.Ranges(100_000_000, 0, 0, settings, minimum: true);
        Assert.Equal(2, minimum.Count);
        Assert.Equal(0, minimum[0].Start);
        Assert.Equal(100_000_000, minimum[1].Start + minimum[1].Length);
    }

    [Fact]
    public void NextEpisodes_CrossesSeasonBoundaryWithoutRewindingOrDuplicating()
    {
        var current = Episode("current", 1, 10);
        var next = PrefetchPolicy.NextEpisodes(current,
            [Episode("old", 1, 1), Episode("next", 2, 1), Episode("next", 2, 1), Episode("later", 2, 3)], 2);
        Assert.Equal(new[] { "next", "later" }, next.Select(item => item.RatingKey));
    }

    [Fact]
    public void ExclusionsAndMediaSwitches_AreAuthoritative()
    {
        var source = new PrefetchSource { ExcludedShows = ["show"] };
        Assert.False(PrefetchPolicy.IsEligible(Episode("next", 2, 1), new(), source));
        Assert.False(PrefetchPolicy.IsEligible(Episode("next", 2, 1), new() { TvEnabled = false }));
        Assert.True(PrefetchPolicy.IsEligible(Episode("next", 2, 1), new()));
    }

    private static PlexMediaItem Episode(string id, int season, int episode) =>
        new(id, "episode", id, "show", season, episode, "/plex/" + id + ".mkv", 0, 1000, null);
}
