using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class XmlTextUtilTests
{
    [Theory]
    [InlineData("plain.mkv")]
    [InlineData("Piñata é à û.mkv")]
    [InlineData("tab\tnewline\ncr\r")]
    [InlineData("emoji \U0001F600.mkv")]
    [InlineData("private use \uE0C3\uE0B1.mkv")]
    public void ValidText_IsReturnedUnchanged(string input)
    {
        Assert.False(XmlTextUtil.ContainsInvalidXmlChars(input));
        Assert.Same(input, XmlTextUtil.ReplaceInvalidXmlChars(input));
    }

    [Theory]
    [InlineData("Pi\uFFFE\uE0C3\uE0B1ata.mkv", "Pi\uFFFD\uE0C3\uE0B1ata.mkv")]
    [InlineData("a\uFFFFb", "a\uFFFDb")]
    [InlineData("a\u0001b\u001Fc", "a\uFFFDb\uFFFDc")]
    [InlineData("\uFFFE", "\uFFFD")]
    public void InvalidChars_AreReplaced(string input, string expected)
    {
        Assert.True(XmlTextUtil.ContainsInvalidXmlChars(input));
        Assert.Equal(expected, XmlTextUtil.ReplaceInvalidXmlChars(input));
    }

    // Built at runtime: xUnit theory data is round-tripped through UTF-8, which mangles lone surrogates.
    [Fact]
    public void LoneSurrogates_AreReplaced()
    {
        var loneHigh = "lone" + '\uD800' + "high";
        var loneLow = "lone" + '\uDC00' + "low";

        Assert.True(XmlTextUtil.ContainsInvalidXmlChars(loneHigh));
        Assert.True(XmlTextUtil.ContainsInvalidXmlChars(loneLow));
        Assert.Equal("lone\uFFFDhigh", XmlTextUtil.ReplaceInvalidXmlChars(loneHigh));
        Assert.Equal("lone\uFFFDlow", XmlTextUtil.ReplaceInvalidXmlChars(loneLow));
    }

    [Fact]
    public void CustomReplacement_IsUsed()
    {
        Assert.Equal("a_b", XmlTextUtil.ReplaceInvalidXmlChars("a\uFFFEb", '_'));
    }
}
