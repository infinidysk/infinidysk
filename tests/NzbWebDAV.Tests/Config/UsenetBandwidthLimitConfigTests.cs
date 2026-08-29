using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public class UsenetBandwidthLimitConfigTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("0", 0)]
    [InlineData("  ", 0)]
    [InlineData("not-a-number", 0)]
    [InlineData("-1", 0)]
    [InlineData("Infinity", 0)]
    public void MissingBlankZeroOrInvalid_MeansUnlimited(string? value, long expected)
    {
        var config = new ConfigManager();
        if (value is not null)
        {
            config.UpdateValues([
                new ConfigItem { ConfigName = ConfigKeys.UsenetBandwidthLimitMbps, ConfigValue = value },
            ]);
        }

        Assert.Equal(expected, config.GetUsenetBandwidthLimitBytesPerSecond());
    }

    [Fact]
    public void ParsesMbpsToBytesPerSecond()
    {
        var config = new ConfigManager();
        config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.UsenetBandwidthLimitMbps, ConfigValue = "8" },
        ]);

        Assert.Equal(1_000_000, config.GetUsenetBandwidthLimitBytesPerSecond());
    }

    [Fact]
    public void SmallPositiveMbps_RemainsLimited()
    {
        var config = new ConfigManager();
        config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.UsenetBandwidthLimitMbps, ConfigValue = "0.000001" },
        ]);

        Assert.Equal(1, config.GetUsenetBandwidthLimitBytesPerSecond());
    }

    [Fact]
    public void CapsAtOneHundredGigabits()
    {
        var config = new ConfigManager();
        config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.UsenetBandwidthLimitMbps, ConfigValue = "200000" },
        ]);

        Assert.Equal(100_000L * 125_000L, config.GetUsenetBandwidthLimitBytesPerSecond());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1.5")]
    [InlineData("100")]
    public void ValidateConfigItems_AcceptsNonNegativeFiniteValues(string value)
    {
        ConfigManager.ValidateConfigItems([
            new ConfigItem { ConfigName = ConfigKeys.UsenetBandwidthLimitMbps, ConfigValue = value },
        ]);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void ValidateConfigItems_RejectsInvalidValues(string value)
    {
        Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems([
            new ConfigItem { ConfigName = ConfigKeys.UsenetBandwidthLimitMbps, ConfigValue = value },
        ]));
    }
}
