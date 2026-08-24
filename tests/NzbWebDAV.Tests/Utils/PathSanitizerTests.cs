using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class PathSanitizerTests
{
    public PathSanitizerTests()
    {
        PathSanitizer.SetWindowsSafePathsEnabled(true);
    }

    [Theory]
    [InlineData("Show: Title?", "Show_ Title_")]
    [InlineData("a<b>c", "a_b_c")]
    [InlineData("file|name", "file_name")]
    [InlineData("q*uery", "q_uery")]
    public void SanitizeComponent_ReplacesInvalidChars(string input, string expected)
    {
        Assert.Equal(expected, PathSanitizer.SanitizeComponent(input));
    }

    [Fact]
    public void SanitizeComponent_ReplacesControlChars()
    {
        Assert.Equal("a_b", PathSanitizer.SanitizeComponent("a\u0001b"));
    }

    [Theory]
    [InlineData("Season 01.", "Season 01")]
    [InlineData("name.  ", "name")]
    [InlineData("name.. ", "name")]
    [InlineData("Subs.", "Subs")]
    public void SanitizeComponent_TrimsTrailingDotsAndSpaces(string input, string expected)
    {
        Assert.Equal(expected, PathSanitizer.SanitizeComponent(input));
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("con.mkv", "_con.mkv")]
    [InlineData("LPT7.txt", "_LPT7.txt")]
    [InlineData("nul", "_nul")]
    public void SanitizeComponent_PrefixesReservedDeviceNames(string input, string expected)
    {
        Assert.Equal(expected, PathSanitizer.SanitizeComponent(input));
    }

    [Theory]
    [InlineData("", "untitled")]
    [InlineData("...", "untitled")]
    [InlineData("   ", "untitled")]
    public void SanitizeComponent_EmptyBecomesUntitled(string input, string expected)
    {
        Assert.Equal(expected, PathSanitizer.SanitizeComponent(input));
    }

    [Fact]
    public void SanitizeComponent_TruncatesLongNamesPreservingExtension()
    {
        var stem = new string('a', 300);
        var result = PathSanitizer.SanitizeComponent(stem + ".mkv");
        Assert.True(result.Length <= 240);
        Assert.EndsWith(".mkv", result);
    }

    [Fact]
    public void SanitizeComponent_WhenDisabled_OnlyReplacesSlashAndNul()
    {
        PathSanitizer.SetWindowsSafePathsEnabled(false);
        try
        {
            Assert.Equal("Show: Title?", PathSanitizer.SanitizeComponent("Show: Title?"));
            Assert.Equal("a_b", PathSanitizer.SanitizeComponent("a/b"));
            Assert.Equal("a_b", PathSanitizer.SanitizeComponent("a\0b"));
        }
        finally
        {
            PathSanitizer.SetWindowsSafePathsEnabled(true);
        }
    }

    [Fact]
    public void SanitizeComponent_WhenDisabled_StillTruncatesToComponentLimit()
    {
        PathSanitizer.SetWindowsSafePathsEnabled(false);
        try
        {
            var stem = new string('a', 300);
            var result = PathSanitizer.SanitizeComponent(stem + ".mkv");
            Assert.True(result.Length <= 240);
            Assert.EndsWith(".mkv", result);
        }
        finally
        {
            PathSanitizer.SetWindowsSafePathsEnabled(true);
        }
    }

    [Fact]
    public void SanitizeComponent_WhenDisabled_TruncationToEmptyBecomesUntitled()
    {
        PathSanitizer.SetWindowsSafePathsEnabled(false);
        try
        {
            Assert.Equal("untitled", PathSanitizer.SanitizeComponent(new string(' ', 241)));
        }
        finally
        {
            PathSanitizer.SetWindowsSafePathsEnabled(true);
        }
    }

    [Fact]
    public void GetJobName_SanitizesWindowsInvalidCharacters()
    {
        Assert.Equal("Show_ Title_", FilenameUtil.GetJobName("Show: Title?.nzb"));
    }
}
