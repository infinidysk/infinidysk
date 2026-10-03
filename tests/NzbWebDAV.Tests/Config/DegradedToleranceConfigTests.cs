using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public sealed class DegradedToleranceConfigTests
{
    [Fact]
    public void ToleranceEnabled_DefaultsToOffWhenRepairsAreOff()
    {
        var config = new ConfigManager();
        config.UpdateValues([Item(ConfigKeys.RepairEnable, "false")]);

        Assert.False(config.IsDegradedToleranceEnabled());
    }

    [Fact]
    public void ToleranceEnabled_DefaultsToOnWhenRepairsAreUnset()
    {
        Assert.True(new ConfigManager().IsDegradedToleranceEnabled());
    }

    [Fact]
    public void ToleranceEnabled_DefaultsToOnWhenRepairsAreOn()
    {
        var config = new ConfigManager();
        config.UpdateValues([Item(ConfigKeys.RepairEnable, "true")]);

        Assert.True(config.IsDegradedToleranceEnabled());
    }

    [Fact]
    public void ToleranceEnabled_RespectsExplicitDisable()
    {
        var config = new ConfigManager();
        config.UpdateValues([
            Item(ConfigKeys.RepairEnable, "true"),
            Item(ConfigKeys.RepairDegradedToleranceEnabled, "false"),
        ]);

        Assert.False(config.IsDegradedToleranceEnabled());
    }

    [Fact]
    public void CorruptionTracking_DefaultsToOffWhenRepairsAreOff()
    {
        var config = new ConfigManager();
        config.UpdateValues([Item(ConfigKeys.RepairEnable, "false")]);

        Assert.False(config.IsCorruptionTrackingEnabled());
    }

    [Fact]
    public void CorruptionTracking_DefaultsToOnWhenRepairsAreUnset()
    {
        Assert.True(new ConfigManager().IsCorruptionTrackingEnabled());
    }

    [Fact]
    public void CorruptionTracking_DefaultsToOnWhenRepairsAreOn()
    {
        var config = new ConfigManager();
        config.UpdateValues([Item(ConfigKeys.RepairEnable, "true")]);

        Assert.True(config.IsCorruptionTrackingEnabled());
    }

    [Fact]
    public void CorruptionTracking_RespectsExplicitDisable()
    {
        var config = new ConfigManager();
        config.UpdateValues([
            Item(ConfigKeys.RepairEnable, "true"),
            Item(ConfigKeys.RepairCorruptionTrackingEnabled, "false"),
        ]);

        Assert.False(config.IsCorruptionTrackingEnabled());
    }

    [Fact]
    public void MaxConsecutiveMissing_DefaultsToTwo()
    {
        Assert.Equal(2, new ConfigManager().GetDegradedMaxConsecutiveMissing());
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    // Clamped to the playback zero-fill bound (GapFillLimits.MaxConsecutiveZeroFills - 1):
    // a run the classifier calls degraded but playback refuses to serve is the worst of both.
    [InlineData("0", 1)]
    [InlineData("-5", 1)]
    [InlineData("99", 2)]
    [InlineData("abc", 2)]
    public void MaxConsecutiveMissing_IsParsedAndClamped(string configured, int expected)
    {
        var config = new ConfigManager();
        config.UpdateValues([Item(ConfigKeys.RepairDegradedMaxConsecutiveMissing, configured)]);

        Assert.Equal(expected, config.GetDegradedMaxConsecutiveMissing());
    }

    [Fact]
    public void MaxTotalMissing_DefaultsToFive()
    {
        Assert.Equal(5, new ConfigManager().GetDegradedMaxTotalMissing());
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("42", 42)]
    [InlineData("0", 1)]
    [InlineData("5000", 1000)]
    [InlineData("abc", 5)]
    public void MaxTotalMissing_IsParsedAndClamped(string configured, int expected)
    {
        var config = new ConfigManager();
        config.UpdateValues([Item(ConfigKeys.RepairDegradedMaxTotalMissing, configured)]);

        Assert.Equal(expected, config.GetDegradedMaxTotalMissing());
    }

    [Fact]
    public void MaxMissingBytePercent_DefaultsToOne()
    {
        Assert.Equal(1.0, new ConfigManager().GetDegradedMaxMissingBytePercent());
    }

    [Theory]
    [InlineData("0.5", 0.5)]
    [InlineData("2.5", 2.5)]
    [InlineData("0", 0.01)]
    [InlineData("99", 50.0)]
    [InlineData("abc", 1.0)]
    public void MaxMissingBytePercent_IsParsedAndClamped(string configured, double expected)
    {
        var config = new ConfigManager();
        config.UpdateValues([Item(ConfigKeys.RepairDegradedMaxMissingBytePercent, configured)]);

        Assert.Equal(expected, config.GetDegradedMaxMissingBytePercent());
    }

    private static ConfigItem Item(string name, string value) =>
        new() { ConfigName = name, ConfigValue = value };
}
