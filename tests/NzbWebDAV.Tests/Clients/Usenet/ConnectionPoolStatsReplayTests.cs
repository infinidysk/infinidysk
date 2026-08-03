using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Models;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class ConnectionPoolStatsReplayTests
{
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

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
