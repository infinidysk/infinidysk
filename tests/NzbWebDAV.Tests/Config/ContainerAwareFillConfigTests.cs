using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public sealed class ContainerAwareFillConfigTests
{
    [Fact]
    public void UnsetValue_DefaultsToEnabled()
    {
        var config = new ConfigManager();

        Assert.True(config.IsContainerAwareFillEnabled());
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void ConfiguredValue_IsValidatedAndResolved(string configured, bool expected)
    {
        var item = new ConfigItem
        {
            ConfigName = ConfigKeys.UsenetContainerAwareFill,
            ConfigValue = configured,
        };
        ConfigManager.ValidateConfigItems([item]);
        var config = new ConfigManager();
        config.UpdateValues([item]);

        Assert.Equal(expected, config.IsContainerAwareFillEnabled());
    }

    [Fact]
    public void InvalidValue_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetContainerAwareFill,
                ConfigValue = "sometimes",
            },
        ]));
    }
}
