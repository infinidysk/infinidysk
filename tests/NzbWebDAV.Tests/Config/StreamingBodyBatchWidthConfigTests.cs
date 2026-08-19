using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public class StreamingBodyBatchWidthConfigTests
{
    [Fact]
    public void UnsetOrEmpty_ReturnsDefaultFour()
    {
        var config = new ConfigManager();
        Assert.Equal(4, config.GetStreamingBodyBatchWidth());

        config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.UsenetStreamingBodyBatchWidth, ConfigValue = "" },
        ]);
        Assert.Equal(4, config.GetStreamingBodyBatchWidth());
    }

    [Theory]
    [InlineData("6", 6)]
    [InlineData("8", 8)]
    [InlineData("1", 1)]
    public void ValidValues_AreReturned(string value, int expected)
    {
        var config = new ConfigManager();
        config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.UsenetStreamingBodyBatchWidth, ConfigValue = value },
        ]);
        Assert.Equal(expected, config.GetStreamingBodyBatchWidth());
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("9", 8)]
    [InlineData("17", 8)]
    [InlineData("abc", 4)]
    public void InvalidValues_AreClampedOrDefault(string value, int expected)
    {
        var config = new ConfigManager();
        config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.UsenetStreamingBodyBatchWidth, ConfigValue = value },
        ]);
        Assert.Equal(expected, config.GetStreamingBodyBatchWidth());
    }

    [Theory]
    [InlineData("")]
    [InlineData("8")]
    public void ValidateConfigItems_AcceptsEmptyOrInRange(string value)
    {
        ConfigManager.ValidateConfigItems([
            new ConfigItem { ConfigName = ConfigKeys.UsenetStreamingBodyBatchWidth, ConfigValue = value },
        ]);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("9")]
    public void ValidateConfigItems_RejectsOutOfRangeOrNonNumeric(string value)
    {
        Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems([
            new ConfigItem { ConfigName = ConfigKeys.UsenetStreamingBodyBatchWidth, ConfigValue = value },
        ]));
    }
}
