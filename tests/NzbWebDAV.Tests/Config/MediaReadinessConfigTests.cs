using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public sealed class MediaReadinessConfigTests
{
    private static ConfigManager CreateConfig(params ConfigItem[] items)
    {
        var config = new ConfigManager();
        if (items.Length > 0)
            config.UpdateValues(items.ToList());
        return config;
    }

    [Fact]
    public void MediaReadiness_EmptyCategoriesAndVideoDisabled_ChecksNothing()
    {
        var config = CreateConfig(
            new ConfigItem { ConfigName = ConfigKeys.ApiEnsureImportableVideo, ConfigValue = "false" });

        Assert.False(config.ShouldCheckMediaReadiness("tv"));
        Assert.False(config.ShouldCheckMediaReadiness("radarr"));
    }

    [Theory]
    [InlineData("tv")]
    [InlineData("movies")]
    [InlineData("radarr")]
    [InlineData("sonarr4k")]
    [InlineData("")]
    public void MediaReadiness_VideoEnabled_ChecksEveryCategory(string category)
    {
        var config = CreateConfig(
            new ConfigItem { ConfigName = ConfigKeys.ApiEnsureImportableVideo, ConfigValue = "true" });

        Assert.True(config.ShouldCheckMediaReadiness(category));
    }

    [Fact]
    public void MediaReadiness_VideoDefault_IsEnabled()
    {
        // ensure-importable-video defaults to true, so custom categories are checked by default.
        var config = CreateConfig();

        Assert.True(config.ShouldCheckMediaReadiness("radarr"));
    }

    [Fact]
    public void MediaReadiness_VideoDisabled_StillHonorsExplicitCategories()
    {
        var config = CreateConfig(
            new ConfigItem { ConfigName = ConfigKeys.ApiEnsureImportableVideo, ConfigValue = "false" },
            new ConfigItem { ConfigName = ConfigKeys.ApiEnsureArticleExistenceCategories, ConfigValue = "anime, Sonarr4K" });

        Assert.True(config.ShouldCheckMediaReadiness("anime"));
        Assert.True(config.ShouldCheckMediaReadiness("sonarr4k"));
        Assert.False(config.ShouldCheckMediaReadiness("tv"));
    }
}
