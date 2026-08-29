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

    [Fact]
    public async Task CombinedAdmission_SuccessReleasesBothLeasesAndPhysicalPermit()
    {
        var config = CreateGateConfig();
        using var gate = new HealthCheckConnectionGate(config);
        var state = new BlockingStatState();
        state.ReleaseAll();
        using var client = CreateClient(state, maxTransferConnections: 4);
        using var cts = new CancellationTokenSource();
        using var healthContext = cts.Token.SetContext(
            new HealthCheckAdmissionContext(gate, HealthCheckAdmissionPriority.Background));

        await client.StatAsync(new SegmentId("combined-success"), cts.Token);

        AssertCombinedAdmissionReleased(client, gate);
        Assert.Equal(1, client.IdleConnections);
    }

    [Fact]
    public async Task CombinedAdmission_CancellationReleasesBothLeasesAndPhysicalPermit()
    {
        var config = CreateGateConfig();
        using var gate = new HealthCheckConnectionGate(config);
        var state = new BlockingStatState();
        using var client = CreateClient(state, maxTransferConnections: 4);
        using var cts = new CancellationTokenSource();
        using var healthContext = cts.Token.SetContext(
            new HealthCheckAdmissionContext(gate, HealthCheckAdmissionPriority.Background));
        var request = client.StatAsync(new SegmentId("combined-cancelled"), cts.Token);
        await WaitForEnteredCount(state, expected: 1);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        AssertCombinedAdmissionReleased(client, gate);
    }

    [Fact]
    public async Task CombinedAdmission_PoolFailureReleasesBothUnattachedLeases()
    {
        var config = CreateGateConfig();
        using var gate = new HealthCheckConnectionGate(config);
        var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            _ => ValueTask.FromException<INntpClient>(
                new IOException("combined connection factory failed")));
        using var client = new MultiConnectionNntpClient(
            pool,
            ProviderType.Pooled,
            new ProviderCircuitBreaker("combined-pool-failure"),
            "combined-pool-failure",
            maxTransferConnections: 1);
        using var cts = new CancellationTokenSource();
        using var healthContext = cts.Token.SetContext(
            new HealthCheckAdmissionContext(gate, HealthCheckAdmissionPriority.Background));

        await Assert.ThrowsAsync<IOException>(
            () => client.StatAsync(new SegmentId("combined-pool-failure"), cts.Token));

        AssertCombinedAdmissionReleased(client, gate);
        Assert.Equal(1, client.AvailableConnections);
    }

    [Fact]
    public async Task CombinedAdmission_CallbackAttachmentFailureReleasesEveryLeaseOnce()
    {
        var config = CreateGateConfig();
        using var gate = new HealthCheckConnectionGate(config);
        var state = new BlockingStatState();
        state.ReleaseAll();
        using var client = CreateClient(state, maxTransferConnections: 4);
        var physicalReleases = 0;
        client.AttachDisposeCallbackForTests = (connectionLock, callback) =>
        {
            connectionLock.AttachDisposeCallback(
                () => Interlocked.Increment(ref physicalReleases));
            connectionLock.AttachDisposeCallback(callback);
        };
        using var cts = new CancellationTokenSource();
        using var healthContext = cts.Token.SetContext(
            new HealthCheckAdmissionContext(gate, HealthCheckAdmissionPriority.Background));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StatAsync(new SegmentId("combined-attach-failure"), cts.Token));

        Assert.Equal(2, Volatile.Read(ref physicalReleases));
        Assert.Equal(0, Volatile.Read(ref state.Entered));
        AssertCombinedAdmissionReleased(client, gate);
        Assert.Equal(1, client.IdleConnections);
    }

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private static ConfigManager CreateGateConfig()
    {
        var config = new ConfigManager();
        config.UpdateValues([
            new ConfigItem
            {
                ConfigName = ConfigKeys.RepairHealthcheckConcurrency,
                ConfigValue = "1",
            },
        ]);
        return config;
    }

    private static MultiConnectionNntpClient CreateClient(
        BlockingStatState state,
        int? maxTransferConnections = null)
    {
        var pool = new ConnectionPool<INntpClient>(
            maxConnections: 4,
            _ => ValueTask.FromResult<INntpClient>(new BlockingStatClient(state)));
        return new MultiConnectionNntpClient(
            pool,
            ProviderType.Pooled,
            new ProviderCircuitBreaker("health-admission-test"),
            "health-admission-test",
            maxTransferConnections: maxTransferConnections);
    }

    private static void AssertCombinedAdmissionReleased(
        MultiConnectionNntpClient client,
        HealthCheckConnectionGate gate)
    {
        var admission = Assert.IsType<ProviderConnectionAdmissionSnapshot>(
            client.GetConnectionAdmissionSnapshot());
        Assert.Equal(0, admission.ActiveMetadataOperations);
        Assert.Equal(0, admission.WaitingMetadataOperations);
        Assert.Equal(0, gate.GetSnapshot().Active);
        Assert.Equal(0, gate.GetSnapshot().WaitingBackground);
        Assert.Equal(0, client.ActiveConnections);
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
