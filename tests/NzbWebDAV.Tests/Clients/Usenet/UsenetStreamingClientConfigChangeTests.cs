using System.Text.Json;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class UsenetStreamingClientConfigChangeTests
{
    [Fact]
    public void DisabledProvider_OpensNoWarmConnections_WhileWarmConnectionsEnabled()
    {
        var config = new ConfigManager();
        config.UpdateValues([ProviderItem(ProviderType.Disabled, maxConnections: 50, nickname: "disabled-remote")]);
        Assert.True(config.IsWarmConnectionsEnabled());

        using var metricsWriter = new MetricsWriter();
        using var client = CreateStreamingClient(config, metricsWriter);

        var provider = Assert.Single(client.GetProviderClientsForTests());
        Assert.Equal(ProviderType.Disabled, provider.ProviderType);
        Assert.Equal(0, provider.WarmConnectionFloor);

        var snapshot = Assert.Single(client.GetProviderConnectionSnapshots());
        Assert.Equal(0, snapshot.LiveConnections);
        Assert.Equal(0, snapshot.IdleConnections);
    }

    [Theory]
    [InlineData(ProviderType.Pooled, 2)]
    [InlineData(ProviderType.BackupAndStats, 2)]
    [InlineData(ProviderType.BackupOnly, 2)]
    [InlineData(ProviderType.Disabled, 0)]
    public void ResolveWarmConnectionFloor_SkipsOnlyDisabledProviders(ProviderType type, int expectedFloor)
    {
        var config = new ConfigManager();
        Assert.True(config.IsWarmConnectionsEnabled());

        var floor = UsenetStreamingClient.ResolveWarmConnectionFloor(config, MakeProvider(type, maxConnections: 50));

        Assert.Equal(expectedFloor, floor);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(12, 2)]
    [InlineData(50, 2)]
    public void GetWarmConnectionsFloor_DefaultsToTwoCappedByProviderWidth(int maxConnections, int expectedFloor)
    {
        var config = new ConfigManager();

        Assert.Equal(expectedFloor, config.GetWarmConnectionsFloor(maxConnections));
    }

    [Theory]
    [InlineData("6", 50, 6)]
    [InlineData("99", 50, 50)]
    [InlineData("0", 50, 1)]
    [InlineData("-3", 50, 1)]
    public void GetWarmConnectionsFloor_ExplicitValueIsClampedToProviderWidth(
        string configured, int maxConnections, int expectedFloor)
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetWarmConnectionsFloor,
                ConfigValue = configured,
            },
        ]);

        Assert.Equal(expectedFloor, config.GetWarmConnectionsFloor(maxConnections));
    }

    [Theory]
    [InlineData(ProviderType.Pooled)]
    [InlineData(ProviderType.Disabled)]
    public void ResolveWarmConnectionFloor_ReturnsZeroWhenWarmConnectionsDisabled(ProviderType type)
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetWarmConnectionsEnabled,
                ConfigValue = "false",
            },
        ]);

        var floor = UsenetStreamingClient.ResolveWarmConnectionFloor(config, MakeProvider(type, maxConnections: 50));

        Assert.Equal(0, floor);
    }

    [Fact]
    public void SavingTimeoutOrReconnectDelay_DoesNotRebuildPools_UntilProviderSave()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            ProviderItem(),
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetWarmConnectionsEnabled,
                ConfigValue = "false",
            },
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetNntpReadTimeoutSeconds,
                ConfigValue = "30",
            },
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetReconnectDelayMilliseconds,
                ConfigValue = "500",
            },
        ]);

        using var metricsWriter = new MetricsWriter();
        using var client = CreateStreamingClient(config, metricsWriter);
        var original = Assert.Single(client.GetProviderClientsForTests());
        var originalSnapshot = Assert.Single(client.GetProviderConnectionSnapshots());
        Assert.Equal(TimeSpan.FromSeconds(30), original.NntpReadTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(500), original.ReconnectDelay);
        Assert.Equal(0, originalSnapshot.LiveConnections);
        Assert.Equal(0, originalSnapshot.IdleConnections);

        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetNntpReadTimeoutSeconds,
                ConfigValue = "17",
            },
        ]);

        Assert.Same(original, Assert.Single(client.GetProviderClientsForTests()));
        var afterTimeout = Assert.Single(client.GetProviderConnectionSnapshots());
        Assert.Equal(0, afterTimeout.LiveConnections);
        Assert.Equal(0, afterTimeout.IdleConnections);
        Assert.Equal(TimeSpan.FromSeconds(30), original.NntpReadTimeout);

        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetReconnectDelayMilliseconds,
                ConfigValue = "0",
            },
        ]);

        Assert.Same(original, Assert.Single(client.GetProviderClientsForTests()));
        Assert.Equal(TimeSpan.FromMilliseconds(500), original.ReconnectDelay);

        config.UpdateValues([ProviderItem()]);

        var rebuilt = Assert.Single(client.GetProviderClientsForTests());
        Assert.NotSame(original, rebuilt);
        Assert.Equal(TimeSpan.FromSeconds(17), rebuilt.NntpReadTimeout);
        Assert.Equal(TimeSpan.Zero, rebuilt.ReconnectDelay);
        var rebuiltSnapshot = Assert.Single(client.GetProviderConnectionSnapshots());
        Assert.Equal(0, rebuiltSnapshot.LiveConnections);
        Assert.Equal(0, rebuiltSnapshot.IdleConnections);
    }

    private static UsenetStreamingClient CreateStreamingClient(
        ConfigManager config,
        MetricsWriter metricsWriter) =>
        new(
            config,
            new WebsocketManager(),
            new ProviderUsageTracker(),
            metricsWriter,
            new ProviderBytesTracker(),
            new StreamTraceBuffer(100),
            new ActiveReadRegistry());

    private static ConfigItem ProviderItem(
        ProviderType type = ProviderType.Pooled,
        int maxConnections = 2,
        string nickname = "timeout-lifecycle") =>
        new()
        {
            ConfigName = ConfigKeys.UsenetProviders,
            ConfigValue = JsonSerializer.Serialize(new UsenetProviderConfig
            {
                Providers =
                [
                    new UsenetProviderConfig.ConnectionDetails
                    {
                        Type = type,
                        Host = "nntp.example",
                        Port = 563,
                        UseSsl = true,
                        User = "u",
                        Pass = "p",
                        MaxConnections = maxConnections,
                        Nickname = nickname,
                    },
                ],
            }),
        };

    private static UsenetProviderConfig.ConnectionDetails MakeProvider(ProviderType type, int maxConnections) =>
        new()
        {
            Type = type,
            Host = "nntp.example",
            Port = 563,
            UseSsl = true,
            User = "u",
            Pass = "p",
            MaxConnections = maxConnections,
        };
}
