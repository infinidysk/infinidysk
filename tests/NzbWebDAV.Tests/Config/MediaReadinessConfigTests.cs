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
    public void MediaReadiness_EmptyCategoriesAndVideoDisabled_IsEmpty()
    {
        var config = CreateConfig(
            new ConfigItem { ConfigName = ConfigKeys.ApiEnsureImportableVideo, ConfigValue = "false" });

        Assert.Empty(config.GetMediaReadinessCategories());
    }

    [Fact]
    public void MediaReadiness_VideoEnabled_IncludesDefaultMediaCategories()
    {
        var config = CreateConfig(
            new ConfigItem { ConfigName = ConfigKeys.ApiEnsureImportableVideo, ConfigValue = "true" });

        var categories = config.GetMediaReadinessCategories();
        Assert.Contains("tv", categories);
        Assert.Contains("movies", categories);
        Assert.Contains("audio", categories);
    }

    [Fact]
    public void MediaReadiness_VideoDefault_IsEnabled()
    {
        // ensure-importable-video defaults to true, so media categories are ready by default.
        var config = CreateConfig();

        Assert.Contains("tv", config.GetMediaReadinessCategories());
    }

    [Fact]
    public void MediaReadiness_ExplicitCategories_AlwaysIncluded()
    {
        var config = CreateConfig(
            new ConfigItem { ConfigName = ConfigKeys.ApiEnsureImportableVideo, ConfigValue = "false" },
            new ConfigItem { ConfigName = ConfigKeys.ApiEnsureArticleExistenceCategories, ConfigValue = "anime, docs" });

        var categories = config.GetMediaReadinessCategories();
        Assert.Contains("anime", categories);
        Assert.Contains("docs", categories);
        Assert.DoesNotContain("tv", categories);
    }

    [Fact]
    public void MediaReadiness_VideoEnabled_MergesExplicitAndDefault()
    {
        var config = CreateConfig(
            new ConfigItem { ConfigName = ConfigKeys.ApiEnsureImportableVideo, ConfigValue = "true" },
            new ConfigItem { ConfigName = ConfigKeys.ApiEnsureArticleExistenceCategories, ConfigValue = "anime" });

        var categories = config.GetMediaReadinessCategories();
        Assert.Contains("anime", categories);
        Assert.Contains("tv", categories);
    }
}
