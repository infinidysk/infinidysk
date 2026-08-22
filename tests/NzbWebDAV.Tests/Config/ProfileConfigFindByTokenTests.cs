using NzbWebDAV.Config;
using System.Text.Json;

namespace NzbWebDAV.Tests.Config;

public class ProfileConfigFindByTokenTests
{
    [Fact]
    public void FindByToken_ReturnsMatchingProfile()
    {
        var config = new ProfileConfig
        {
            Profiles =
            [
                new ProfileConfig.Profile { Token = "aaaaaaaaaaaaaaaaaaaaaaaa", Name = "A" },
                new ProfileConfig.Profile { Token = "bbbbbbbbbbbbbbbbbbbbbbbb", Name = "B" },
            ],
        };

        var match = config.FindByToken("bbbbbbbbbbbbbbbbbbbbbbbb");

        Assert.NotNull(match);
        Assert.Equal("B", match.Name);
    }

    [Fact]
    public void FindByToken_ReturnsNullOnMiss()
    {
        var config = new ProfileConfig
        {
            Profiles =
            [
                new ProfileConfig.Profile { Token = "aaaaaaaaaaaaaaaaaaaaaaaa", Name = "A" },
            ],
        };

        Assert.Null(config.FindByToken("cccccccccccccccccccccccc"));
    }

    [Fact]
    public void FindByToken_ReturnsFirstMatchWhenDuplicatesExist()
    {
        var config = new ProfileConfig
        {
            Profiles =
            [
                new ProfileConfig.Profile { Token = "dddddddddddddddddddddddd", Name = "First" },
                new ProfileConfig.Profile { Token = "dddddddddddddddddddddddd", Name = "Second" },
            ],
        };

        var match = config.FindByToken("dddddddddddddddddddddddd");

        Assert.NotNull(match);
        Assert.Equal("First", match.Name);
    }

    [Fact]
    public void ExistingProfileWithoutQualitySort_UsesDefaultOrdering()
    {
        var config = JsonSerializer.Deserialize<ProfileConfig>(
            """{"Profiles":[{"Token":"aaaaaaaaaaaaaaaaaaaaaaaa","Name":"Existing"}]}""");

        Assert.NotNull(config);
        Assert.Equal(ProfileConfig.QualitySortMode.Off, config.Profiles[0].QualitySort);
    }

    [Theory]
    [InlineData(ProfileConfig.QualitySortMode.Off, "Off")]
    [InlineData(ProfileConfig.QualitySortMode.Resolution, "Resolution")]
    [InlineData(ProfileConfig.QualitySortMode.ResolutionAndSource, "ResolutionAndSource")]
    public void QualitySort_RoundTripsAsString(ProfileConfig.QualitySortMode mode, string expected)
    {
        var config = new ProfileConfig
        {
            Profiles =
            [
                new ProfileConfig.Profile
                {
                    Token = "aaaaaaaaaaaaaaaaaaaaaaaa",
                    Name = "A",
                    QualitySort = mode,
                },
            ],
        };

        var json = JsonSerializer.Serialize(config);
        Assert.Contains($"\"QualitySort\":\"{expected}\"", json, StringComparison.Ordinal);

        var roundTrip = JsonSerializer.Deserialize<ProfileConfig>(json);
        Assert.NotNull(roundTrip);
        Assert.Equal(mode, roundTrip.Profiles[0].QualitySort);
    }
}
