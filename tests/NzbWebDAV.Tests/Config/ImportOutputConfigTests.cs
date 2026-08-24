using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public sealed class ImportOutputConfigTests
{
    [Fact]
    public void LegacySymlinkStrategy_EnablesOnlySymlinkOutput()
    {
        var config = new ConfigManager();

        Assert.True(config.IsSymlinkOutputEnabled());
        Assert.False(config.IsStrmOutputEnabled());
    }

    [Fact]
    public void LegacyStrmStrategy_EnablesOnlyStrmOutput()
    {
        var config = Config((ConfigKeys.ApiImportStrategy, "strm"));

        Assert.False(config.IsSymlinkOutputEnabled());
        Assert.True(config.IsStrmOutputEnabled());
    }

    [Fact]
    public void ExplicitSecondaryOutput_IsEnabledAlongsidePrimary()
    {
        var config = Config((ConfigKeys.ApiStrmOutputEnabled, "true"));

        Assert.True(config.IsSymlinkOutputEnabled());
        Assert.True(config.IsStrmOutputEnabled());
    }

    [Theory]
    [InlineData("both")]
    [InlineData("invalid")]
    public void UnknownPrimaryOutput_IsRejected(string value)
    {
        Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems(
        [
            new ConfigItem { ConfigName = ConfigKeys.ApiImportStrategy, ConfigValue = value },
        ]));
    }

    [Fact]
    public void InvalidOutputToggle_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems(
        [
            new ConfigItem { ConfigName = ConfigKeys.ApiStrmOutputEnabled, ConfigValue = "sometimes" },
        ]));
    }

    private static ConfigManager Config(params (string Key, string Value)[] items)
    {
        var config = new ConfigManager();
        config.UpdateValues(items
            .Select(x => new ConfigItem { ConfigName = x.Key, ConfigValue = x.Value })
            .ToList());
        return config;
    }
}
