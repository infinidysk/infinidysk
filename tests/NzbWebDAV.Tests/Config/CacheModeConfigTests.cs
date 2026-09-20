using System.Collections;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public sealed class CacheModeConfigTests
{
    [Fact]
    public void BlankLegacyValue_PreservesOffDefault()
    {
        var config = new ConfigManager();
        config.UpdateValues([new ConfigItem
        {
            ConfigName = ConfigKeys.UsenetSegmentCacheEnabled,
            ConfigValue = " ",
        }]);
        Assert.Equal(CacheMode.Off, config.GetCacheMode());
    }

    [Fact]
    public void BlankPersistedMode_IsUnset()
    {
        var config = new ConfigManager();
        config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.CacheMode, ConfigValue = " " },
            new ConfigItem { ConfigName = ConfigKeys.UsenetSegmentCacheEnabled, ConfigValue = "true" },
        ]);
        Assert.Equal(CacheMode.Segment, config.GetCacheMode());
        Assert.False(config.HasExplicitCacheMode());
    }

    [Fact]
    public void EnvironmentAlias_ConflictingWithExplicitMode_IsRejected()
    {
        var config = new ConfigManager();
        config.UpdateValues([new ConfigItem { ConfigName = "cache.mode", ConfigValue = "native" }]);
        var overlay = ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
        {
            ["NZBDAV_CONFIG__USENET__SEGMENT_CACHE__ENABLED"] = "true",
        });
        var error = Assert.Throws<ConfigEnvironmentException>(() => config.ApplyEnvironmentOverlay(overlay));
        Assert.Contains("NZBDAV_CONFIG__CACHE__MODE", error.Message);
        Assert.Contains("NZBDAV_CONFIG__USENET__SEGMENT_CACHE__ENABLED", error.Message);
    }

    [Fact]
    public void Mode_TrimmedAndCaseInsensitive()
    {
        var config = new ConfigManager();
        config.UpdateValues([new ConfigItem { ConfigName = "cache.mode", ConfigValue = " Native " }]);
        Assert.Equal(CacheMode.Native, config.GetCacheMode());
    }

    [Theory]
    [InlineData("off")]
    [InlineData("native")]
    public void ExplicitMode_DisablesLegacySegmentFlag(string mode)
    {
        var config = new ConfigManager();
        config.UpdateValues([
            new ConfigItem { ConfigName = "cache.mode", ConfigValue = mode },
            new ConfigItem { ConfigName = ConfigKeys.UsenetSegmentCacheEnabled, ConfigValue = "true" },
        ]);
        Assert.False(config.IsSegmentCacheEnabled());
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    public void InvalidMode_IsRejected(string mode) => Assert.Throws<ArgumentException>(() =>
        ConfigManager.ValidateConfigItems([new ConfigItem { ConfigName = "cache.mode", ConfigValue = mode }]));
}
