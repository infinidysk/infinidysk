using System.Collections;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public class QueuePipeliningConfigTests
{
    [Fact]
    public void Unset_ReturnsDisabledAndDefaultDepth()
    {
        var config = new ConfigManager();
        Assert.False(config.IsQueuePipeliningEnabled());
        Assert.Equal(8, config.GetQueuePipeliningDepth());
    }

    [Fact]
    public void LegacyDbOnly_IsHonoredForEnabledAndDepth()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.UsenetPipeliningEnabled, ConfigValue = "true" },
            new ConfigItem { ConfigName = ConfigKeys.UsenetPipeliningDepth, ConfigValue = "12" },
        ]);

        Assert.True(config.IsQueuePipeliningEnabled());
        Assert.Equal(12, config.GetQueuePipeliningDepth());
        Assert.Equal("true", config.GetEffectiveConfigValue(ConfigKeys.UsenetQueuePipeliningEnabled));
        Assert.Equal("12", config.GetEffectiveConfigValue(ConfigKeys.UsenetQueuePipeliningDepth));
    }

    [Fact]
    public void NewDbWinsOverLegacyDb_WhenBothSet()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.UsenetQueuePipeliningEnabled, ConfigValue = "true" },
            new ConfigItem { ConfigName = ConfigKeys.UsenetPipeliningEnabled, ConfigValue = "false" },
            new ConfigItem { ConfigName = ConfigKeys.UsenetQueuePipeliningDepth, ConfigValue = "16" },
            new ConfigItem { ConfigName = ConfigKeys.UsenetPipeliningDepth, ConfigValue = "4" },
        ]);

        Assert.True(config.IsQueuePipeliningEnabled());
        Assert.Equal(16, config.GetQueuePipeliningDepth());
    }

    [Fact]
    public void EmptyNewDbValue_FallsBackToLegacyDb()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.UsenetQueuePipeliningEnabled, ConfigValue = "" },
            new ConfigItem { ConfigName = ConfigKeys.UsenetPipeliningEnabled, ConfigValue = "true" },
            new ConfigItem { ConfigName = ConfigKeys.UsenetQueuePipeliningDepth, ConfigValue = "  " },
            new ConfigItem { ConfigName = ConfigKeys.UsenetPipeliningDepth, ConfigValue = "12" },
        ]);

        Assert.True(config.IsQueuePipeliningEnabled());
        Assert.Equal(12, config.GetQueuePipeliningDepth());
        Assert.Equal("true", config.GetEffectiveConfigValue(ConfigKeys.UsenetQueuePipeliningEnabled));
        Assert.Equal("12", config.GetEffectiveConfigValue(ConfigKeys.UsenetQueuePipeliningDepth));
    }

    [Fact]
    public void LegacyEnvWinsOverConflictingDbNewAndLegacyRows()
    {
        var previous = Environment.GetEnvironmentVariable("NZBDAV_CONFIG__USENET__PIPELINING__ENABLED");
        try
        {
            var config = new ConfigManager();
            config.UpdateValues(
            [
                new ConfigItem { ConfigName = ConfigKeys.UsenetQueuePipeliningEnabled, ConfigValue = "false" },
                new ConfigItem { ConfigName = ConfigKeys.UsenetPipeliningEnabled, ConfigValue = "false" },
            ]);
            config.ApplyEnvironmentOverlay(ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
            {
                ["NZBDAV_CONFIG__USENET__PIPELINING__ENABLED"] = "true",
            }));

            Assert.True(config.IsQueuePipeliningEnabled());
            Assert.Equal("true", config.GetEffectiveConfigValue(ConfigKeys.UsenetQueuePipeliningEnabled));
            Assert.True(config.IsEnvironmentManaged(ConfigKeys.UsenetQueuePipeliningEnabled));
            Assert.Equal(
                "NZBDAV_CONFIG__USENET__PIPELINING__ENABLED",
                config.GetEnvironmentVariableName(ConfigKeys.UsenetQueuePipeliningEnabled));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NZBDAV_CONFIG__USENET__PIPELINING__ENABLED", previous);
        }
    }

    [Fact]
    public void NewEnvWinsOverLegacyEnvAndDb()
    {
        var previousEnabled = Environment.GetEnvironmentVariable("NZBDAV_CONFIG__USENET__QUEUE_PIPELINING__ENABLED");
        var previousLegacy = Environment.GetEnvironmentVariable("NZBDAV_CONFIG__USENET__PIPELINING__ENABLED");
        try
        {
            var config = new ConfigManager();
            config.UpdateValues(
            [
                new ConfigItem { ConfigName = ConfigKeys.UsenetQueuePipeliningEnabled, ConfigValue = "false" },
                new ConfigItem { ConfigName = ConfigKeys.UsenetPipeliningEnabled, ConfigValue = "false" },
            ]);
            config.ApplyEnvironmentOverlay(ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
            {
                ["NZBDAV_CONFIG__USENET__QUEUE_PIPELINING__ENABLED"] = "true",
                ["NZBDAV_CONFIG__USENET__PIPELINING__ENABLED"] = "false",
            }));

            Assert.True(config.IsQueuePipeliningEnabled());
            Assert.Equal(
                "NZBDAV_CONFIG__USENET__QUEUE_PIPELINING__ENABLED",
                config.GetEnvironmentVariableName(ConfigKeys.UsenetQueuePipeliningEnabled));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NZBDAV_CONFIG__USENET__QUEUE_PIPELINING__ENABLED", previousEnabled);
            Environment.SetEnvironmentVariable("NZBDAV_CONFIG__USENET__PIPELINING__ENABLED", previousLegacy);
        }
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("100", 64)]
    [InlineData("abc", 8)]
    public void Depth_IsClampedOrFallsBack(string value, int expected)
    {
        var config = new ConfigManager();
        config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.UsenetQueuePipeliningDepth, ConfigValue = value },
        ]);
        Assert.Equal(expected, config.GetQueuePipeliningDepth());
    }

    [Fact]
    public void LegacyKeys_StillValidate()
    {
        ConfigManager.ValidateConfigItems([
            new ConfigItem { ConfigName = ConfigKeys.UsenetPipeliningEnabled, ConfigValue = "true" },
            new ConfigItem { ConfigName = ConfigKeys.UsenetPipeliningDepth, ConfigValue = "8" },
        ]);
    }
}
