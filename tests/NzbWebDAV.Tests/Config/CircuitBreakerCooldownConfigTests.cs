using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public sealed class CircuitBreakerCooldownConfigTests
{
    [Fact]
    public void InitialCooldown_DefaultsToSixtySeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), new ConfigManager().GetCircuitBreakerInitialCooldown());
    }

    [Theory]
    [InlineData("15", 15)]
    [InlineData("300", 300)]
    [InlineData("1", 5)]
    [InlineData("-30", 5)]
    [InlineData("3600", 300)]
    [InlineData("2147483648", 300)]
    [InlineData("-2147483649", 5)]
    [InlineData("abc", 60)]
    [InlineData("", 60)]
    public void InitialCooldown_IsParsedAndClamped(string configured, int expectedSeconds)
    {
        var config = new ConfigManager();
        config.UpdateValues([Item(ConfigKeys.UsenetCircuitBreakerInitialCooldownSeconds, configured)]);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), config.GetCircuitBreakerInitialCooldown());
    }

    [Fact]
    public void MaxCooldown_DefaultsToFiveMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), new ConfigManager().GetCircuitBreakerMaxCooldown());
    }

    [Theory]
    [InlineData("60", 60)]
    [InlineData("3600", 3600)]
    [InlineData("1", 5)]
    [InlineData("7200", 3600)]
    [InlineData("2147483648", 3600)]
    [InlineData("-2147483649", 5)]
    [InlineData("abc", 300)]
    [InlineData("", 300)]
    public void MaxCooldown_IsParsedAndClamped(string configured, int expectedSeconds)
    {
        var config = new ConfigManager();
        config.UpdateValues([Item(ConfigKeys.UsenetCircuitBreakerMaxCooldownSeconds, configured)]);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), config.GetCircuitBreakerMaxCooldown());
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    public void ValidateConfigItems_AcceptsValuesOutsideInt32(string configured)
    {
        ConfigManager.ValidateConfigItems(
        [
            Item(ConfigKeys.UsenetCircuitBreakerInitialCooldownSeconds, configured),
            Item(ConfigKeys.UsenetCircuitBreakerMaxCooldownSeconds, configured),
        ]);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1.5")]
    public void ValidateConfigItems_RejectsValuesThatAreNotWholeNumbers(string configured)
    {
        Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems(
        [
            Item(ConfigKeys.UsenetCircuitBreakerInitialCooldownSeconds, configured),
        ]));
    }

    private static ConfigItem Item(string name, string value) =>
        new() { ConfigName = name, ConfigValue = value };
}
