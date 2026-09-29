using System.Collections;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public sealed class ConnectionOpenTimeoutConfigTests
{
    [Fact]
    public void GetConnectionOpenTimeout_DefaultsToFiveSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), new ConfigManager().GetConnectionOpenTimeout());
    }

    [Theory]
    [InlineData("", 5)]
    [InlineData("invalid", 5)]
    [InlineData("1.5", 5)]
    [InlineData("2147483648", 5)]
    [InlineData("-4", 1)]
    [InlineData("0", 1)]
    [InlineData("1", 1)]
    [InlineData("3", 3)]
    [InlineData("10", 10)]
    [InlineData("15", 15)]
    [InlineData("60", 15)]
    public void GetConnectionOpenTimeout_PreservesParsingAndClamp(string configured, int expectedSeconds)
    {
        var config = new ConfigManager();
        config.UpdateValues([Setting(configured)]);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), config.GetConnectionOpenTimeout());
        Assert.Equal(configured, config.GetPersistedConfigValue(ConfigKeys.UsenetConnectionOpenTimeoutSeconds));
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("10", 10)]
    [InlineData("15", 15)]
    public void GetConnectionOpenTimeout_EnvironmentValueControlsEffectiveTimeout(
        string configured, int expectedSeconds)
    {
        var config = new ConfigManager();
        config.UpdateValues([Setting("7")]);
        config.ApplyEnvironmentOverlay(ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
        {
            ["NZBDAV_CONFIG__USENET__CONNECTION_OPEN_TIMEOUT_SECONDS"] = configured,
        }));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), config.GetConnectionOpenTimeout());
        Assert.Equal("7", config.GetPersistedConfigValue(ConfigKeys.UsenetConnectionOpenTimeoutSeconds));
        Assert.True(config.IsEnvironmentManaged(ConfigKeys.UsenetConnectionOpenTimeoutSeconds));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("15")]
    [InlineData("60")]
    public void ValidateConfigItems_PreservesExistingWholeNumberAcceptance(string configured)
    {
        ConfigManager.ValidateConfigItems([Setting(configured)]);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("1.5")]
    public void ValidateConfigItems_RejectsNonIntegers(string configured)
    {
        Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems([Setting(configured)]));
    }

    private static ConfigItem Setting(string value) => new()
    {
        ConfigName = ConfigKeys.UsenetConnectionOpenTimeoutSeconds,
        ConfigValue = value,
    };
}
