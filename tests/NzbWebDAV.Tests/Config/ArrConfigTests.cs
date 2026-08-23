using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public class ArrConfigTests
{
    [Fact]
    public void LegacyJson_WithoutNameOrEnabled_DefaultsEnabledTrueAndNameNull()
    {
        const string json = """
            {"RadarrInstances":[{"Host":"http://Radarr:7878/","ApiKey":"k"}],"SonarrInstances":[],"QueueRules":[]}
            """;

        var parsed = JsonSerializer.Deserialize<ArrConfig>(json)!;
        var instance = Assert.Single(parsed.RadarrInstances);
        Assert.Null(instance.Name);
        Assert.True(instance.Enabled);
        Assert.Equal("http://Radarr:7878/", instance.Host);
        Assert.Equal(3, parsed.EffectiveQueueReplacementSearchLimit());
        Assert.Equal(TimeSpan.FromMinutes(30), parsed.EffectiveQueueReplacementSearchWindow());
    }

    [Fact]
    public void ReplacementSearchBudget_ClampsInvalidConfiguration()
    {
        var config = new ArrConfig
        {
            QueueReplacementSearchLimit = 99,
            QueueReplacementSearchWindowMinutes = 0,
        };

        Assert.Equal(10, config.EffectiveQueueReplacementSearchLimit());
        Assert.Equal(TimeSpan.FromMinutes(1), config.EffectiveQueueReplacementSearchWindow());
    }

    [Fact]
    public void GetArrClients_OmitsDisabledInstances_AndKeepsLegacyUnspecifiedEnabled()
    {
        var config = new ArrConfig
        {
            RadarrInstances =
            [
                new ArrConfig.ConnectionDetails { Host = "http://radarr-on", ApiKey = "a", Enabled = true },
                new ArrConfig.ConnectionDetails { Host = "http://radarr-off", ApiKey = "b", Enabled = false },
            ],
            SonarrInstances =
            [
                new ArrConfig.ConnectionDetails { Host = "http://sonarr-legacy", ApiKey = "c" },
            ],
        };

        var hosts = config.GetArrClients().Select(c => c.Host).ToList();
        Assert.Equal(2, hosts.Count);
        Assert.Contains("http://radarr-on", hosts);
        Assert.Contains("http://sonarr-legacy", hosts);
        Assert.DoesNotContain("http://radarr-off", hosts);
    }

    [Fact]
    public void GetEnabledInstances_UsesStableKeysAndSkipsDisabled()
    {
        var config = new ArrConfig
        {
            RadarrInstances =
            [
                new ArrConfig.ConnectionDetails
                {
                    Name = "Movies 4K",
                    Host = "http://Radarr:7878/",
                    ApiKey = "a",
                },
                new ArrConfig.ConnectionDetails { Host = "http://radarr-off", ApiKey = "b", Enabled = false },
            ],
            SonarrInstances =
            [
                new ArrConfig.ConnectionDetails { Host = "http://sonarr:8989", ApiKey = "c" },
            ],
        };

        var enabled = config.GetEnabledInstances().ToList();
        Assert.Equal(2, enabled.Count);
        Assert.Equal("radarr", enabled[0].AppType);
        Assert.Equal("Movies 4K", enabled[0].Details.Name);
        Assert.Equal(
            "radarr|http://radarr:7878",
            ArrConfig.MakeInstanceKey(enabled[0].AppType, enabled[0].Details.Host));
        Assert.Equal("sonarr", enabled[1].AppType);
        Assert.Equal(
            "sonarr|http://sonarr:8989",
            ArrConfig.MakeInstanceKey(enabled[1].AppType, enabled[1].Details.Host));
    }

    [Fact]
    public void IsArrHealthEnabled_DefaultsTrueAndParsesExplicitValues()
    {
        Assert.True(new ConfigManager().IsArrHealthEnabled());

        var disabled = new ConfigManager();
        disabled.UpdateValues([Item(ConfigKeys.ArrHealthEnabled, "false")]);
        Assert.False(disabled.IsArrHealthEnabled());

        var enabled = new ConfigManager();
        enabled.UpdateValues([Item(ConfigKeys.ArrHealthEnabled, "true")]);
        Assert.True(enabled.IsArrHealthEnabled());
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    public void ArrHealthEnabled_RejectsNonBooleanValues(string value)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            ConfigManager.ValidateConfigItems([Item(ConfigKeys.ArrHealthEnabled, value)]));
        Assert.Contains(ConfigKeys.ArrHealthEnabled, ex.Message);
    }

    private static ConfigItem Item(string name, string value) =>
        new() { ConfigName = name, ConfigValue = value };
}
