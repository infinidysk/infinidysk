using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Models;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class ConnectionPoolStatsReplayTests
{
    [Fact]
    public async Task NewGeneration_ClearsRetiredProviderReplayState()
    {
        var websocketManager = new WebsocketManager();
        await websocketManager.SendMessage(
            WebsocketTopic.UsenetConnections,
            "4|8|8|8|60|8");

        _ = new ConnectionPoolStats(
            new UsenetProviderConfig
            {
                Providers =
                [
                    new UsenetProviderConfig.ConnectionDetails
                    {
                        Type = ProviderType.Pooled,
                        Host = "news.example.com",
                        Port = 563,
                        UseSsl = true,
                        User = "user",
                        Pass = "pass",
                        MaxConnections = 10,
                    },
                ],
            },
            websocketManager);

        Assert.Null(websocketManager.PeekLastMessage(WebsocketTopic.UsenetConnections));
    }

    [Fact]
    public async Task Flush_WithoutSubscribers_KeepsReplayStateFresh()
    {
        var websocketManager = new WebsocketManager();
        var connectionStats = new ConnectionPoolStats(
            new UsenetProviderConfig
            {
                Providers =
                [
                    new UsenetProviderConfig.ConnectionDetails
                    {
                        Type = ProviderType.Pooled,
                        Host = "news.example.com",
                        Port = 563,
                        UseSsl = true,
                        User = "user",
                        Pass = "pass",
                        MaxConnections = 10,
                    },
                ],
            },
            websocketManager);
        var onChanged = connectionStats.GetOnConnectionPoolChanged(0);

        // A pool event with zero subscribers (e.g. connections closing after
        // the last browser leaves) must still refresh the state-replay message,
        // otherwise a returning browser sees phantom stale connection counts.
        onChanged(
            this,
            new ConnectionPoolStats.ConnectionPoolChangedEventArgs(3, 1, 10));

        await WaitUntil(() => websocketManager.PeekLastMessage(WebsocketTopic.UsenetConnections) is not null);
        Assert.Equal(
            "0|3|1|3|10|1",
            websocketManager.PeekLastMessage(WebsocketTopic.UsenetConnections));
    }

    [Fact]
    public async Task EffectiveMax_ReflectedInTotalMax()
    {
        var websocketManager = new WebsocketManager();
        var connectionStats = new ConnectionPoolStats(
            new UsenetProviderConfig
            {
                Providers =
                [
                    new UsenetProviderConfig.ConnectionDetails
                    {
                        Type = ProviderType.Pooled,
                        Host = "news.example.com",
                        Port = 563,
                        UseSsl = true,
                        User = "user",
                        Pass = "pass",
                        MaxConnections = 150,
                    },
                ],
            },
            websocketManager);
        var onChanged = connectionStats.GetOnConnectionPoolChanged(0);

        // Simulate a learned-limit shrink: pool reports effective max 135.
        onChanged(
            this,
            new ConnectionPoolStats.ConnectionPoolChangedEventArgs(5, 2, 135));

        await WaitUntil(() => websocketManager.PeekLastMessage(WebsocketTopic.UsenetConnections) is not null);
        Assert.Equal(
            "0|5|2|5|135|2",
            websocketManager.PeekLastMessage(WebsocketTopic.UsenetConnections));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
