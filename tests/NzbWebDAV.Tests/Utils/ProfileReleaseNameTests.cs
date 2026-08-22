using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class ProfileReleaseNameTests
{
    [Theory]
    [InlineData("Movie.2024.1080p", "Movie.2024.1080p")]
    [InlineData("Movie/Name", "Movie_Name")]
    [InlineData("  padded  ", "padded")]
    [InlineData("", "untitled")]
    [InlineData("   ", "untitled")]
    [InlineData(null, "untitled")]
    public void SanitizeFileName_ReplacesInvalidCharactersAndFallsBack(string? name, string expected)
    {
        Assert.Equal(expected, ProfileReleaseName.SanitizeFileName(name));
    }

    [Fact]
    public void ToNzbFileName_AppendsNzbExtension()
    {
        Assert.Equal("Movie.2024.1080p.nzb", ProfileReleaseName.ToNzbFileName("Movie.2024.1080p"));
        Assert.Equal("untitled.nzb", ProfileReleaseName.ToNzbFileName(""));
    }
}
