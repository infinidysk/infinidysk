using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class MultiConnectionHealthAdmissionTests
{
    [Fact]
    public async Task HealthAdmission_IsSharedAcrossProvidersBeforePhysicalPoolAcquisition()
    {
        var config = new ConfigManager();
        config.UpdateValues([
            new ConfigItem
            {
                ConfigName = ConfigKeys.RepairHealthcheckConcurrency,
                ConfigValue = "1",
            },
        ]);
        using var gate = new HealthCheckConnectionGate(config);
        var state = new BlockingStatState();
        using var firstProvider = CreateClient(state);
        using var secondProvider = CreateClient(state);
        using var cts = new CancellationTokenSource();
        using var healthContext = cts.Token.SetContext(
            new HealthCheckAdmissionContext(gate, HealthCheckAdmissionPriority.Background));

        var requests = StartStats(firstProvider, 2, cts.Token)
            .Concat(StartStats(secondProvider, 2, cts.Token))
            .ToArray();
        await WaitForEnteredCount(state, expected: 1);

        Assert.Equal(1, Volatile.Read(ref state.Entered));
        Assert.Equal(1, firstProvider.LiveConnections + secondProvider.LiveConnections);
        Assert.Equal(1, gate.GetSnapshot().Active);

        state.ReleaseAll();
        await Task.WhenAll(requests).WaitAsync(TestTimeout);
        Assert.Equal(4, Volatile.Read(ref state.Entered));
        Assert.Equal(0, gate.GetSnapshot().Active);
    }

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private static MultiConnectionNntpClient CreateClient(BlockingStatState state)
    {
        var pool = new ConnectionPool<INntpClient>(
            maxConnections: 4,
            _ => ValueTask.FromResult<INntpClient>(new BlockingStatClient(state)));
        return new MultiConnectionNntpClient(
            pool,
            ProviderType.Pooled,
            new ProviderCircuitBreaker("health-admission-test"),
            "health-admission-test");
    }

    private static Task<UsenetStatResponse>[] StartStats(
        MultiConnectionNntpClient client,
        int count,
        CancellationToken ct) =>
        Enumerable.Range(0, count)
            .Select(i => client.StatAsync(
                new SegmentId($"segment-{i}"),
                ct))
            .ToArray();

    private static async Task WaitForEnteredCount(BlockingStatState state, int expected)
    {
        var deadline = DateTime.UtcNow + TestTimeout;
        while (Volatile.Read(ref state.Entered) < expected)
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Only {state.Entered} STAT operations entered; expected {expected}.");
            await Task.Delay(10);
        }
    }

    private sealed class BlockingStatState
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Entered;
        public Task WaitForRelease() => _release.Task;
        public void ReleaseAll() => _release.TrySetResult();
    }

    private sealed class BlockingStatClient(BlockingStatState state) : NntpClient
    {
        public override Task ConnectAsync(
            string host,
            int port,
            bool useSsl,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(
            string user,
            string pass,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public override async Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref state.Entered);
            await state.WaitForRelease().WaitAsync(cancellationToken);
            return new UsenetStatResponse
            {
                ResponseCode = 223,
                ResponseMessage = $"223 <{segmentId}>",
                ArticleExists = true,
            };
        }

        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }
}
