using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public sealed class SampleFilterConfigTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void ConfiguredValue_IsValidatedAndResolved(string configured, bool expected)
    {
        var item = new ConfigItem
        {
            ConfigName = ConfigKeys.ApiSampleFilterEnabled,
            ConfigValue = configured,
        };
        ConfigManager.ValidateConfigItems([item]);
        var config = new ConfigManager();
        config.UpdateValues([item]);

        Assert.Equal(expected, config.IsSampleFilterEnabled());
    }

    [Fact]
    public void InvalidValue_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.ApiSampleFilterEnabled,
                ConfigValue = "sometimes",
            },
        ]));
    }
}
