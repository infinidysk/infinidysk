using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Websocket;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class WrappingNntpClientRetirementTests
{
    [Fact]
    public async Task ReplaceUnderlyingClient_DrainsUntilInFlightZero()
    {
        var oldClient = new CountingDisposableClient { InFlightConnections = 1 };
        var wrapper = new TestWrappingClient(oldClient);
        var newClient = new CountingDisposableClient();

        var drainTask = wrapper.ReplaceUnderlyingClientForTestsAsync(newClient);

        Assert.False(oldClient.Disposed);
        Assert.True(oldClient.Retired);

        oldClient.InFlightConnections = 0;
        await drainTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(oldClient.Disposed);
        Assert.False(newClient.Disposed);
        Assert.Equal(0, wrapper.InFlightConnections);
    }

    [Fact]
    public async Task ReplaceUnderlyingClient_DoesNotForceDisposeInFlightWork()
    {
        var oldClient = new CountingDisposableClient { InFlightConnections = 5 };
        var wrapper = new TestWrappingClient(oldClient);
        var newClient = new CountingDisposableClient();

        var drainTask = wrapper.ReplaceUnderlyingClientForTestsAsync(newClient);
        await Task.Delay(500);

        Assert.False(oldClient.Disposed);

        oldClient.InFlightConnections = 0;
        await drainTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(oldClient.Disposed);
    }

    [Fact]
    public async Task ReplaceUnderlyingClient_BoundsStackedRetiringGenerations()
    {
        var oldest = new CountingDisposableClient { InFlightConnections = 1 };
        var wrapper = new TestWrappingClient(oldest);
        var replacements = Enumerable.Range(0, 5)
            .Select(_ => new CountingDisposableClient { InFlightConnections = 1 })
            .ToArray();

        var drains = replacements
            .Select(replacement => wrapper.ReplaceUnderlyingClientForTestsAsync(replacement))
            .ToArray();

        Assert.True(oldest.Disposed);
        Assert.All(replacements, replacement => Assert.False(replacement.Disposed));

        foreach (var replacement in replacements)
            replacement.InFlightConnections = 0;
        await Task.WhenAll(drains).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(replacements[..^1], replacement => Assert.True(replacement.Disposed));
        Assert.False(replacements[^1].Disposed);
    }

    [Fact]
    public async Task ReplaceUnderlyingClient_DeactivatesRetiredConnectionStatsImmediately()
    {
        var connectionStats = new ConnectionPoolStats(
            new UsenetProviderConfig(),
            new WebsocketManager());
        var oldClient = new MultiProviderNntpClient([], connectionPoolStats: connectionStats);
        var wrapper = new TestWrappingClient(oldClient);

        await wrapper.ReplaceUnderlyingClientForTestsAsync(new CountingDisposableClient());

        Assert.False(connectionStats.IsActive);
    }

    [Fact]
    public async Task ConnectionPoolStats_Deactivate_DropsScheduledAndFutureUpdates()
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
                        MaxConnections = 50,
                    },
                ],
            },
            websocketManager);
        var onChanged = connectionStats.GetOnConnectionPoolChanged(0);

        onChanged(
            this,
            new ConnectionPoolStats.ConnectionPoolChangedEventArgs(25, 5, 50));
        connectionStats.Deactivate();
        onChanged(
            this,
            new ConnectionPoolStats.ConnectionPoolChangedEventArgs(1, 0, 50));
        await Task.Delay(500);

        // Constructing the replacement generation clears stale provider snapshots.
        // Deactivation must not overwrite that reset with a retired pool update.
        Assert.Equal("reset", websocketManager.PeekLastMessage(WebsocketTopic.UsenetConnections));
    }

    private sealed class TestWrappingClient(INntpClient inner) : WrappingNntpClient(inner);

    private sealed class CountingDisposableClient : NntpClient, INntpConnectionStats
    {
        public int InFlightConnections { get; set; }
        public bool Disposed { get; private set; }
        public bool Retired { get; private set; }

        internal override void Retire() => Retired = true;

        public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds, ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetYencHeader> GetYencHeadersAsync(
            string segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<long> GetFileSizeAsync(NzbFile file, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
            Disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
