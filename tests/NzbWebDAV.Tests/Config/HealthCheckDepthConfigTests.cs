using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public class HealthCheckDepthConfigTests
{
    [Theory]
    [InlineData("quick", HealthCheckDepth.Quick)]
    [InlineData("standard", ConfigManager.DefaultHealthCheckDepth)]
    [InlineData("enhanced", HealthCheckDepth.Enhanced)]
    [InlineData("deep", HealthCheckDepth.Deep)]
    [InlineData("complete", HealthCheckDepth.Complete)]
    // Validation accepts any casing, so the getter has to resolve it the same way
    // rather than falling through to the default and quietly under-checking.
    [InlineData("Quick", HealthCheckDepth.Quick)]
    [InlineData("Deep", HealthCheckDepth.Deep)]
    [InlineData("COMPLETE", HealthCheckDepth.Complete)]
    [InlineData("nonsense", ConfigManager.DefaultHealthCheckDepth)]
    public void GetHealthCheckDepth_ResolvesRegardlessOfCasing(string value, HealthCheckDepth expected)
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.RepairHealthcheckDepth,
                ConfigValue = value,
            },
        ]);

        Assert.Equal(expected, config.GetHealthCheckDepth());
    }

    [Theory]
    [InlineData("quick")]
    [InlineData("Quick")]
    [InlineData("Deep")]
    [InlineData("COMPLETE")]
    [InlineData("standard")]
    public void ValidateConfigItems_AcceptsAnyCasingTheGetterResolves(string value)
    {
        var items = new[]
        {
            new ConfigItem { ConfigName = ConfigKeys.RepairHealthcheckDepth, ConfigValue = value },
        };

        ConfigManager.ValidateConfigItems(items);
    }

    [Fact]
    public void ValidateConfigItems_RejectsAnUnknownDepth()
    {
        var items = new[]
        {
            new ConfigItem { ConfigName = ConfigKeys.RepairHealthcheckDepth, ConfigValue = "thorough" },
        };

        Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems(items));
    }
}
