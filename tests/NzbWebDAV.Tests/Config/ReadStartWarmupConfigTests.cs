using System.Collections;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public sealed class ReadStartWarmupConfigTests
{
    [Fact]
    public void Default_IsEnabled()
    {
        var config = new ConfigManager();

        Assert.True(config.IsReadStartWarmupEnabled());
        Assert.Equal(
            EffectiveConfigSource.Default,
            config.GetEffectiveSource(ConfigKeys.UsenetReadStartWarmupEnabled));
    }

    [Fact]
    public void EnvironmentOverlay_IsAuthoritative()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetReadStartWarmupEnabled,
                ConfigValue = "true",
            },
        ]);
        config.ApplyEnvironmentOverlay(ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
        {
            ["NZBDAV_CONFIG__USENET__READ_START_WARMUP__ENABLED"] = "false",
        }));

        Assert.False(config.IsReadStartWarmupEnabled());
        Assert.True(config.IsEnvironmentManaged(ConfigKeys.UsenetReadStartWarmupEnabled));
        Assert.Equal(
            EffectiveConfigSource.Environment,
            config.GetEffectiveSource(ConfigKeys.UsenetReadStartWarmupEnabled));
    }

    [Fact]
    public void InvalidStoredValue_RemainsEnabled()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetReadStartWarmupEnabled,
                ConfigValue = "invalid",
            },
        ]);

        Assert.True(config.IsReadStartWarmupEnabled());
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void Validation_AcceptsBooleans(string value)
    {
        ConfigManager.ValidateConfigItems(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetReadStartWarmupEnabled,
                ConfigValue = value,
            },
        ]);
    }

    [Fact]
    public void Validation_RejectsInvalidValue()
    {
        Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetReadStartWarmupEnabled,
                ConfigValue = "sometimes",
            },
        ]));
    }
}