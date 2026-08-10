using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public class MetricsFetchRetentionConfigTests
{
    [Theory]
    [InlineData(null, 24)]
    [InlineData("", 24)]
    [InlineData("abc", 24)]
    [InlineData("24", 24)]
    [InlineData("0", 0)]
    [InlineData("12", 12)]
    [InlineData("8760", 8760)]
    [InlineData("10000", 8760)]
    public void GetMetricsFetchRetentionHours_ClampsAndFallsBack(string? value, int expected)
    {
        var config = new ConfigManager();
        if (value is not null)
        {
            config.UpdateValues([
                new ConfigItem
                {
                    ConfigName = ConfigKeys.MetricsFetchRetentionHours,
                    ConfigValue = value,
                },
            ]);
        }

        Assert.Equal(expected, config.GetMetricsFetchRetentionHours());
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    public void ValidateConfigItems_RejectsInvalidMetricsFetchRetentionHours(string value)
    {
        Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems([
            new ConfigItem
            {
                ConfigName = ConfigKeys.MetricsFetchRetentionHours,
                ConfigValue = value,
            },
        ]));
    }

    [Fact]
    public void ValidateConfigItems_AcceptsValidMetricsFetchRetentionHours()
    {
        ConfigManager.ValidateConfigItems([
            new ConfigItem
            {
                ConfigName = ConfigKeys.MetricsFetchRetentionHours,
                ConfigValue = "48",
            },
        ]);
    }

    [Fact]
    public void GetMetricsFetchRetentionHours_FallsBackToEnvironmentVariable()
    {
        const string envVar = "METRICS_FETCH_RETENTION_HOURS";
        var previous = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "6");
            var config = new ConfigManager();

            Assert.Equal(6, config.GetMetricsFetchRetentionHours());
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, previous);
        }
    }

    [Fact]
    public void GetMetricsFetchRetentionHours_ConfigTakesPrecedenceOverEnvironmentVariable()
    {
        const string envVar = "METRICS_FETCH_RETENTION_HOURS";
        var previous = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "6");
            var config = new ConfigManager();
            config.UpdateValues([
                new ConfigItem
                {
                    ConfigName = ConfigKeys.MetricsFetchRetentionHours,
                    ConfigValue = "48",
                },
            ]);

            Assert.Equal(48, config.GetMetricsFetchRetentionHours());
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, previous);
        }
    }
}
