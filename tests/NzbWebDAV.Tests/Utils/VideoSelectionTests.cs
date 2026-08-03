using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class VideoSelectionTests
{
    private static DavItem Video(string name, long fileSize) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        FileSize = fileSize,
        Type = DavItem.ItemType.UsenetFile,
        SubType = DavItem.ItemSubType.NzbFile,
        Path = "/" + name,
    };

    [Theory]
    [InlineData("Show.S01E02.unpack.mkv")]
    [InlineData("SHOW.S01E02.UNPACK.MKV")]
    [InlineData("show.rickroll.mkv")]
    public void IsProbablyDecoy_MatchesKnownDecoyPatterns(string name)
    {
        Assert.True(VideoSelection.IsProbablyDecoy(name));
    }

    [Theory]
    [InlineData("Unpacked.2017.mkv")] // leading-dot guard: "unpack" without ".unpack." must not match
    [InlineData("Show.S01E02.1080p.mkv")]
    [InlineData("Show.S01E02.WEB-DL.mkv")]
    public void IsProbablyDecoy_DoesNotMatchLegitimateNames(string name)
    {
        Assert.False(VideoSelection.IsProbablyDecoy(name));
    }

    // a. decoy `show.unpack.mkv` (8GB) + real `Show.S01E02.1080p.mkv` (2GB), entry S01E02 → real file.
    [Fact]
    public void SelectBest_EpisodeMatch_PrefersRealFileOverLargerDecoy()
    {
        var decoy = Video("show.unpack.mkv", 8_000_000_000);
        var real = Video("Show.S01E02.1080p.mkv", 2_000_000_000);
        var videos = new List<DavItem> { decoy, real };

        var best = VideoSelection.SelectBest(videos, season: 1, episode: 2, primaryTitle: "Show S01E02");

        Assert.Same(real, best);
    }

    // b. decoy + obfuscated real file (`abc123.mkv`), no episode tags, zero tokens → non-decoy chosen.
    [Fact]
    public void SelectBest_NoEpisodeTagsAndNoTokens_PrefersNonDecoyFallback()
    {
        var decoy = Video("release.unpack.mkv", 8_000_000_000);
        var real = Video("abc123.mkv", 2_000_000_000);
        var videos = new List<DavItem> { decoy, real };

        // A title that tokenizes to nothing (only single-char tokens are dropped by TokenizeName).
        var best = VideoSelection.SelectBest(videos, season: null, episode: null, primaryTitle: "a b c");

        Assert.Same(real, best);
    }

    // c. only file is `whatever.unpack.mkv` → still returned (fallback rule).
    [Fact]
    public void SelectBest_OnlyCandidateIsDecoy_StillReturnedRatherThanNull()
    {
        var decoy = Video("whatever.unpack.mkv", 1_000_000_000);
        var videos = new List<DavItem> { decoy };

        var best = VideoSelection.SelectBest(videos, season: null, episode: null, primaryTitle: null);

        Assert.Same(decoy, best);
    }

    [Fact]
    public void SelectBest_SkipsSampleFiles_EpisodeMatch()
    {
        var sample = Video("Show.S01E02.sample.mkv", 50_000_000);
        var real = Video("Show.S01E02.1080p.mkv", 2_000_000_000);
        var videos = new List<DavItem> { sample, real };

        var best = VideoSelection.SelectBest(videos, season: 1, episode: 2, primaryTitle: "Show S01E02");

        Assert.Same(real, best);
    }

    [Fact]
    public void SelectBest_KeepsLargestVideoEvenWhenNameContainsSample()
    {
        var feature = Video("Free.Samples.2012.1080p.mkv", 8_000_000_000);
        var videos = new List<DavItem> { feature };

        var best = VideoSelection.SelectBest(videos, season: null, episode: null, primaryTitle: "Free Samples 2012");

        Assert.Same(feature, best);
    }

    [Fact]
    public void SelectBest_SkipsSampleByNameWhenSizeUnknown()
    {
        var sample = new DavItem
        {
            Id = Guid.NewGuid(),
            Name = "Show.S01E02.sample.mkv",
            FileSize = null,
            Type = DavItem.ItemType.UsenetFile,
            SubType = DavItem.ItemSubType.NzbFile,
            Path = "/Show.S01E02.sample.mkv",
        };
        var real = Video("Show.S01E02.1080p.mkv", 2_000_000_000);
        var videos = new List<DavItem> { sample, real };

        var best = VideoSelection.SelectBest(videos, season: 1, episode: 2, primaryTitle: "Show S01E02");

        Assert.Same(real, best);
    }

    [Fact]
    public void SelectBest_SkipsSampleFiles_TokenOverlap()
    {
        var sample = Video("MyShow.Title.sample.mkv", 50_000_000);
        var real = Video("MyShow.Title.mkv", 2_000_000_000);
        var videos = new List<DavItem> { sample, real };

        var best = VideoSelection.SelectBest(videos, season: null, episode: null, primaryTitle: "MyShow Title");

        Assert.Same(real, best);
    }

    [Fact]
    public void SelectBest_TokenOverlap_SkipsDecoyEvenWhenLarger()
    {
        var decoy = Video("MyShow.Title.unpack.mkv", 8_000_000_000);
        var real = Video("MyShow.Title.mkv", 2_000_000_000);
        var videos = new List<DavItem> { decoy, real };

        var best = VideoSelection.SelectBest(videos, season: null, episode: null, primaryTitle: "MyShow Title");

        Assert.Same(real, best);
    }

    [Fact]
    public void SelectBest_EmptyVideoList_ReturnsNull()
    {
        var best = VideoSelection.SelectBest(new List<DavItem>(), season: null, episode: null, primaryTitle: "anything");

        Assert.Null(best);
    }
}
