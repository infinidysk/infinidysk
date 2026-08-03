using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public sealed class QueueAdmissionConfigTests
{
    [Fact]
    public void ResumeThreshold_ZeroUsesConfiguredMaximum()
    {
        var config = CreateConfig(maxItems: 50, resumeThreshold: 0);

        Assert.Equal(50, config.GetQueueMaxItems());
        Assert.Equal(50, config.GetQueueResumeThreshold());
    }

    [Fact]
    public void ValidateQueueAdmissionSettings_RejectsThresholdAboveMaximum()
    {
        var config = new ConfigManager();
        var items = new List<ConfigItem>
        {
            new() { ConfigName = ConfigKeys.QueueMaxItems, ConfigValue = "50" },
            new() { ConfigName = ConfigKeys.QueueResumeThreshold, ConfigValue = "51" },
        };

        ConfigManager.ValidateConfigItems(items);
        Assert.Throws<ArgumentException>(() => config.ValidateQueueAdmissionSettings(items));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("many")]
    public void ValidateConfigItems_RejectsInvalidAdmissionCounts(string value)
    {
        Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems(
        [
            new ConfigItem { ConfigName = ConfigKeys.QueueMaxItems, ConfigValue = value },
        ]));
    }

    private static ConfigManager CreateConfig(int maxItems, int resumeThreshold)
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.QueueMaxItems,
                ConfigValue = maxItems.ToString(),
            },
            new ConfigItem
            {
                ConfigName = ConfigKeys.QueueResumeThreshold,
                ConfigValue = resumeThreshold.ToString(),
            },
        ]);
        return config;
    }
}
