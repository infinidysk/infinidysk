using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class ReleaseQualityTests
{
    [Theory]
    [InlineData("Movie.4320p.WEB-DL", ReleaseQualityRanks.Resolution4320)]
    [InlineData("Movie.8K.WEB-DL", ReleaseQualityRanks.Resolution4320)]
    [InlineData("Movie.2160p.WEB-DL", ReleaseQualityRanks.Resolution2160)]
    [InlineData("Movie.4K.WEB-DL", ReleaseQualityRanks.Resolution2160)]
    [InlineData("Movie.UHD.WEB-DL", ReleaseQualityRanks.Resolution2160)]
    [InlineData("Movie.1080p.WEB-DL", ReleaseQualityRanks.Resolution1080)]
    [InlineData("Movie.1080i.HDTV", ReleaseQualityRanks.Resolution1080)]
    [InlineData("Movie.720p.WEB-DL", ReleaseQualityRanks.Resolution720)]
    [InlineData("Movie.576p.DVD", ReleaseQualityRanks.ResolutionSd)]
    [InlineData("Movie.480p.DVD", ReleaseQualityRanks.ResolutionSd)]
    [InlineData("Movie.SD.WEB-DL", ReleaseQualityRanks.ResolutionSd)]
    [InlineData("Movie.WEB-DL", ReleaseQualityRanks.ResolutionUnknown)]
    public void Parse_RecognizesResolutionAliases(string title, int expected)
    {
        Assert.Equal(expected, ReleaseQuality.Parse(title).Resolution);
    }

    [Theory]
    [InlineData("Movie.1080p.REMUX", ReleaseQualityRanks.SourceRemux)]
    [InlineData("Movie.1080p.BluRay", ReleaseQualityRanks.SourceBluRay)]
    [InlineData("Movie.1080p.Blu-Ray", ReleaseQualityRanks.SourceBluRay)]
    [InlineData("Movie.1080p.BDRip", ReleaseQualityRanks.SourceBluRay)]
    [InlineData("Movie.1080p.BRRip", ReleaseQualityRanks.SourceBluRay)]
    [InlineData("Movie.1080p.WEB-DL", ReleaseQualityRanks.SourceWebDl)]
    [InlineData("Movie.1080p.WEBDL", ReleaseQualityRanks.SourceWebDl)]
    [InlineData("Movie.1080p.WEBRip", ReleaseQualityRanks.SourceWebRip)]
    [InlineData("Movie.1080p.HDTV", ReleaseQualityRanks.SourceHdtv)]
    [InlineData("Movie.480p.DVDRip", ReleaseQualityRanks.SourceDvd)]
    [InlineData("Movie.480p.DVD", ReleaseQualityRanks.SourceDvd)]
    [InlineData("Movie.1080p", ReleaseQualityRanks.SourceUnknown)]
    [InlineData("Movie.1080p.CAM", ReleaseQualityRanks.SourceCam)]
    [InlineData("Movie.1080p.TS", ReleaseQualityRanks.SourceCam)]
    [InlineData("Movie.1080p.TELESYNC", ReleaseQualityRanks.SourceCam)]
    public void Parse_RecognizesSourceAliases(string title, int expected)
    {
        Assert.Equal(expected, ReleaseQuality.Parse(title).Source);
    }

    [Fact]
    public void Parse_IsCaseAndPunctuationInsensitive()
    {
        var dotted = ReleaseQuality.Parse("movie.2024.2160p.web-dl");
        var underscored = ReleaseQuality.Parse("MOVIE_2024_2160P_WEB_DL");
        Assert.Equal(ReleaseQualityRanks.Resolution2160, dotted.Resolution);
        Assert.Equal(ReleaseQualityRanks.SourceWebDl, dotted.Source);
        Assert.Equal(dotted, underscored);
    }

    [Fact]
    public void Parse_UnknownQuality_ReturnsZeroRanks()
    {
        Assert.Equal(ReleaseQualityRanks.Unknown, ReleaseQuality.Parse("Some.Untitled.Release"));
    }

    [Fact]
    public void Parse_CamRanksBelowUnknown()
    {
        Assert.True(ReleaseQuality.Parse("Movie.CAM").Source < ReleaseQualityRanks.SourceUnknown);
    }

    [Fact]
    public void Parse_DoesNotClassifyOrdinaryTitleText()
    {
        var ranks = ReleaseQuality.Parse("The.Web.Of.Fear");
        Assert.Equal(ReleaseQualityRanks.ResolutionUnknown, ranks.Resolution);
        Assert.Equal(ReleaseQualityRanks.SourceUnknown, ranks.Source);
    }

    [Fact]
    public void ResolutionDominatesSource()
    {
        var uhdHdtv = ReleaseQuality.Parse("Movie.2160p.HDTV");
        var hdRemux = ReleaseQuality.Parse("Movie.1080p.REMUX");
        Assert.True(uhdHdtv.Resolution > hdRemux.Resolution);
        Assert.True(uhdHdtv.Source < hdRemux.Source);
    }
}
