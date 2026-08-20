using System.Text.RegularExpressions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class ExcludePatternParserTests
{
    [Fact]
    public void Parse_BareBody_IsCaseInsensitiveByDefault()
    {
        var parsed = ExcludePatternParser.Parse(@"\.iso$");
        Assert.NotNull(parsed);
        var pattern = parsed ?? throw new InvalidOperationException("expected pattern");
        Assert.Matches(pattern.Regex, "FILE.ISO");
        Assert.DoesNotMatch(pattern.Regex, "FILE.mkv");
    }

    [Fact]
    public void Parse_JsWrapper_SharesDedupKeyWithBareBody()
    {
        var bare = ExcludePatternParser.Parse(@"\.(iso|img)$");
        var wrapped = ExcludePatternParser.Parse(@"/\.(iso|img)$/i");
        Assert.NotNull(bare);
        Assert.NotNull(wrapped);
        var parsedBare = bare ?? throw new InvalidOperationException("expected bare pattern");
        var parsedWrapped = wrapped ?? throw new InvalidOperationException("expected wrapped pattern");
        Assert.Equal(parsedBare.Key, parsedWrapped.Key);
        Assert.Matches(parsedWrapped.Regex, "Movie.ISO");
    }

    [Fact]
    public void Parse_MsFlags_AreOrderIndependentInKey()
    {
        var ms = ExcludePatternParser.Parse("/x/ms");
        var sm = ExcludePatternParser.Parse("/x/sm");
        Assert.NotNull(ms);
        Assert.NotNull(sm);
        var parsedMs = ms ?? throw new InvalidOperationException("expected ms pattern");
        var parsedSm = sm ?? throw new InvalidOperationException("expected sm pattern");
        Assert.Equal(parsedMs.Key, parsedSm.Key);
        Assert.True((parsedMs.Regex.Options & RegexOptions.Multiline) != 0);
        Assert.True((parsedMs.Regex.Options & RegexOptions.Singleline) != 0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# comment")]
    [InlineData("[")]
    public void Parse_BlanksCommentsAndInvalid_ReturnNull(string? line)
    {
        Assert.Null(ExcludePatternParser.Parse(line));
    }

    [Fact]
    public void Parse_UnknownJsFlags_AreIgnored()
    {
        var parsed = ExcludePatternParser.Parse("/foo/gu");
        Assert.NotNull(parsed);
        var pattern = parsed ?? throw new InvalidOperationException("expected pattern");
        Assert.Equal("foo ", pattern.Key);
        Assert.Matches(pattern.Regex, "FOO");
    }
}
