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

    private static ConfigItem ProviderItem() =>
        new()
        {
            ConfigName = ConfigKeys.UsenetProviders,
            ConfigValue = JsonSerializer.Serialize(new UsenetProviderConfig
            {
                Providers =
                [
                    new UsenetProviderConfig.ConnectionDetails
                    {
                        Type = ProviderType.Pooled,
                        Host = "nntp.example",
                        Port = 563,
                        UseSsl = true,
                        User = "u",
                        Pass = "p",
                        MaxConnections = 2,
                        Nickname = "timeout-lifecycle",
                    },
                ],
            }),
        };
}
