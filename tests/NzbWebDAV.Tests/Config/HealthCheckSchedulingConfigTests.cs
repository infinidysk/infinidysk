using System.Collections;
using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;

namespace NzbWebDAV.Tests.Config;

public sealed class HealthCheckSchedulingConfigTests
{
    [Theory]
    [InlineData(null, 1)]
    [InlineData("2", 2)]
    [InlineData("8", 8)]
    public void GetHealthCheckWorkers_UsesSafeBounds(string? configured, int expected)
    {
        var config = new ConfigManager();
        if (configured is not null)
        {
            config.UpdateValues([
                new ConfigItem
                {
                    ConfigName = ConfigKeys.RepairHealthcheckWorkers,
                    ConfigValue = configured,
                },
            ]);
        }

        Assert.Equal(expected, config.GetHealthCheckWorkers());
    }

    [Fact]
    public void HeadlessOverlay_MapsHealthCheckWorkers()
    {
        var config = new ConfigManager();
        config.ApplyEnvironmentOverlay(ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
        {
            ["NZBDAV_CONFIG__REPAIR__HEALTHCHECK_WORKERS"] = "3",
        }));

        Assert.Equal(3, config.GetHealthCheckWorkers());
        Assert.True(config.IsEnvironmentManaged(ConfigKeys.RepairHealthcheckWorkers));
        Assert.Equal(
            "NZBDAV_CONFIG__REPAIR__HEALTHCHECK_WORKERS",
            config.GetEnvironmentVariableName(ConfigKeys.RepairHealthcheckWorkers));
    }

    [Theory]
    [InlineData(ConfigKeys.RepairHealthcheckWorkers, "0")]
    [InlineData(ConfigKeys.RepairHealthcheckWorkers, "9")]
    public void ValidateConfigItems_RejectsSchedulingValuesOutsideRange(string key, string value)
    {
        Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems([
            new ConfigItem { ConfigName = key, ConfigValue = value },
        ]));
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("300", 10)]
    [InlineData("9223372036854775807", 10)]
    [InlineData("-50", 1)]
    public void GetHealthCheckConcurrency_AcceptsLegacyValuesAndClampsToPool(
        string configured,
        int expected)
    {
        var config = CreateProviderConfig(MakeProvider(ProviderType.Pooled));
        ConfigManager.ValidateConfigItems([
            new ConfigItem
            {
                ConfigName = ConfigKeys.RepairHealthcheckConcurrency,
                ConfigValue = configured,
            },
        ]);
        config.UpdateValues([
            new ConfigItem
            {
                ConfigName = ConfigKeys.RepairHealthcheckConcurrency,
                ConfigValue = configured,
            },
        ]);

        Assert.Equal(expected, config.GetHealthCheckConcurrency());
    }

    [Fact]
    public void HeadlessOverlay_AcceptsLegacyHealthCheckConcurrencyValue()
    {
        var config = CreateProviderConfig(MakeProvider(ProviderType.Pooled));
        config.ApplyEnvironmentOverlay(ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
        {
            ["NZBDAV_CONFIG__REPAIR__HEALTHCHECK_CONCURRENCY"] = "300",
        }));

        Assert.Equal(10, config.GetHealthCheckConcurrency());
        Assert.True(config.IsEnvironmentManaged(ConfigKeys.RepairHealthcheckConcurrency));
    }

    private static ConfigManager CreateProviderConfig(
        params UsenetProviderConfig.ConnectionDetails[] providers)
    {
        var config = new ConfigManager();
        config.UpdateValues([
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue = JsonSerializer.Serialize(new UsenetProviderConfig
                {
                    Providers = providers.ToList(),
                }),
            },
        ]);
        return config;
    }

    private static UsenetProviderConfig.ConnectionDetails MakeProvider(ProviderType type) => new()
    {
        ProviderId = Guid.NewGuid(),
        Type = type,
        Host = $"{Guid.NewGuid():N}.example",
        Port = 563,
        UseSsl = true,
        User = "user",
        Pass = "pass",
        MaxConnections = 10,
    };
}
