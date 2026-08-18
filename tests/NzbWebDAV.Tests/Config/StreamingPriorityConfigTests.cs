using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public class StreamingPriorityConfigTests
{
    [Theory]
    [InlineData("150", 100)]
    [InlineData("-5", 0)]
    [InlineData("abc", 80)]
    [InlineData("", 80)]
    [InlineData(null, 80)]
    [InlineData("80", 80)]
    public void GetStreamingPriority_ClampsAndFallsBack(string? value, int expected)
    {
        var config = new ConfigManager();
        if (value is not null)
        {
            config.UpdateValues(
            [
                new ConfigItem
                {
                    ConfigName = ConfigKeys.UsenetStreamingPriority,
                    ConfigValue = value,
                },
            ]);
        }

        Assert.Equal(expected, config.GetStreamingPriority().HighPriorityOdds);
    }

    [Theory]
    [InlineData("abc", 30)]
    [InlineData("", 30)]
    [InlineData(null, 30)]
    [InlineData("4", 5)]
    [InlineData("200", 120)]
    [InlineData("45", 45)]
    public void GetStreamingReadTimeout_ClampsAndFallsBack(string? value, int expectedSeconds)
    {
        var config = new ConfigManager();
        if (value is not null)
        {
            config.UpdateValues(
            [
                new ConfigItem
                {
                    ConfigName = ConfigKeys.UsenetStreamingReadTimeoutSeconds,
                    ConfigValue = value,
                },
            ]);
        }

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), config.GetStreamingReadTimeout());
    }

    [Theory]
    [InlineData("abc", 10L * 1024 * 1024 * 1024)]
    [InlineData("0", 1L * 1024 * 1024 * 1024)]
    [InlineData("2", 2L * 1024 * 1024 * 1024)]
    public void GetSegmentCacheMaxBytes_SafeParse(string value, long expected)
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetSegmentCacheMaxGb,
                ConfigValue = value,
            },
        ]);
        Assert.Equal(expected, config.GetSegmentCacheMaxBytes());
    }

    [Fact]
    public void IsSegmentCacheEnabled_DefaultsOnUntilExplicitlyDisabled()
    {
        var config = new ConfigManager();

        Assert.True(config.IsSegmentCacheEnabled());

        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetSegmentCacheEnabled,
                ConfigValue = "false",
            },
        ]);

        Assert.False(config.IsSegmentCacheEnabled());
    }

    [Fact]
    public void ValidateConfigItems_RejectsNonNumericStreamingPriority()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            ConfigManager.ValidateConfigItems(
            [
                new ConfigItem
                {
                    ConfigName = ConfigKeys.UsenetStreamingPriority,
                    ConfigValue = "nope",
                },
            ]));
        Assert.Contains("whole number", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
