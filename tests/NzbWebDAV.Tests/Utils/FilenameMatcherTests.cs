using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class FilenameMatcherTests
{
    [Fact]
    public void YearCompatible_UnknownCanonicalYear_IsPermissive()
    {
        Assert.True(FilenameMatcher.YearCompatible(null, "Dune.1984.1080p.BluRay"));
    }

    [Fact]
    public void YearCompatible_ExactYear_Matches()
    {
        Assert.True(FilenameMatcher.YearCompatible(2024, "Dune.Part.Two.2024.2160p.WEB-DL"));
    }

    [Theory]
    [InlineData(2024, "Dune.Part.Two.2023.1080p")]
    [InlineData(2024, "Dune.Part.Two.2025.1080p")]
    public void YearCompatible_PlusOrMinusOne_Matches(int canonicalYear, string title)
    {
        Assert.True(FilenameMatcher.YearCompatible(canonicalYear, title));
    }

    [Fact]
    public void YearCompatible_WrongRemakeYear_IsRejected()
    {
        Assert.False(FilenameMatcher.YearCompatible(2024, "Dune.1984.1080p.BluRay"));
    }

    [Fact]
    public void YearCompatible_MissingReleaseYear_IsPermissive()
    {
        Assert.True(FilenameMatcher.YearCompatible(2024, "Dune.2160p.WEB-DL"));
    }

    [Fact]
    public void ParseReleaseYears_DoesNotTreatResolutionAsYear()
    {
        Assert.Empty(FilenameMatcher.ParseReleaseYears("Dune.2160p.WEB-DL"));
        Assert.Empty(FilenameMatcher.ParseReleaseYears("Movie.1080p.BluRay"));
        Assert.Empty(FilenameMatcher.ParseReleaseYears("Movie.1920x1080.WEB-DL"));
    }

    [Fact]
    public void YearCompatible_NumericTitle1917_UsesTheReleaseYear()
    {
        var titles = new[] { FilenameMatcher.NormalizeTitle("1917") };
        Assert.True(FilenameMatcher.YearCompatible(2019, "1917.2019.1080p.BluRay", titles));
        Assert.False(FilenameMatcher.YearCompatible(2019, "1917.2017.1080p.BluRay", titles));
        Assert.Equal(new[] { 1917, 2019 }, FilenameMatcher.ParseReleaseYears("1917.2019.1080p.BluRay"));
    }

    [Fact]
    public void YearCompatible_NumericTitle1984_UsesTheReleaseYear()
    {
        var titles = new[] { FilenameMatcher.NormalizeTitle("1984") };
        Assert.True(FilenameMatcher.YearCompatible(1984, "1984.1984.1080p", titles));
        Assert.False(FilenameMatcher.YearCompatible(1984, "1984.2023.1080p", titles));
    }

    [Fact]
    public void YearCompatible_BladeRunner2049_DoesNotTreatTitleTokenAsYear()
    {
        var titles = new[] { FilenameMatcher.NormalizeTitle("Blade Runner 2049") };
        Assert.True(FilenameMatcher.YearCompatible(2017, "Blade.Runner.2049.2017.1080p", titles));
        Assert.False(FilenameMatcher.YearCompatible(2017, "Blade.Runner.2049.2015.1080p", titles));
        Assert.True(FilenameMatcher.YearCompatible(2017, "Blade.Runner.2049.2160p.Remux", titles));
    }

    [Fact]
    public void YearCompatible_SeriesStartYearInCanonicalTitle_IsIgnored()
    {
        var titles = new[] { FilenameMatcher.NormalizeTitle("Doctor Who 2005") };
        Assert.True(FilenameMatcher.YearCompatible(2023, "Doctor.Who.2005.S01E01.720p", titles));
    }
}
