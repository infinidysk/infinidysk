using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services.Metrics;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class MultiConnectionConnectionBudgetTests
{
    [Fact]
    public async Task NullTransferLimitPreservesLegacySharedPoolWidth()
    {
        var state = new BlockingStatState();
        using var client = CreateClient(state, maxTransferConnections: null);
        var requests = StartStats(client, count: 4);

        await WaitForEnteredCount(state, expected: 4);

        state.ReleaseAll();
        await Task.WhenAll(requests).WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task ExplicitTransferLimitActivatesMetadataBudget()
    {
        var state = new BlockingStatState();
        using var client = CreateClient(state, maxTransferConnections: 4);
        var requests = StartStats(client, count: 4);

        await WaitForEnteredCount(state, expected: 2);
        Assert.Equal(2, Volatile.Read(ref state.Entered));

        state.ReleaseAll();
        await Task.WhenAll(requests).WaitAsync(TestTimeout);
        Assert.Equal(4, Volatile.Read(ref state.Entered));
    }

    [Fact]
    public async Task SuccessfulOperationReleasesAdmissionAndPhysicalPermit()
    {
        var state = new BlockingStatState();
        using var client = CreateClient(state, maxTransferConnections: 4);
        state.ReleaseAll();

        await client.StatAsync(new SegmentId("success"), CancellationToken.None);

        var admission = Assert.IsType<ProviderConnectionAdmissionSnapshot>(
            client.GetConnectionAdmissionSnapshot());
        Assert.Equal(0, admission.ActiveMetadataOperations);
        Assert.Equal(0, admission.WaitingMetadataOperations);
        Assert.Equal(0, client.ActiveConnections);
        Assert.Equal(1, client.IdleConnections);
    }

    [Fact]
    public async Task CancelledOperationReleasesAdmissionAndPhysicalPermit()
    {
        var state = new BlockingStatState();
        using var client = CreateClient(state, maxTransferConnections: 4);
        using var cancellation = new CancellationTokenSource();
        var request = client.StatAsync(
            new SegmentId("cancelled"),
            cancellation.Token);
        await WaitForEnteredCount(state, expected: 1);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        var admission = Assert.IsType<ProviderConnectionAdmissionSnapshot>(
            client.GetConnectionAdmissionSnapshot());
        Assert.Equal(0, admission.ActiveMetadataOperations);
        Assert.Equal(0, admission.WaitingMetadataOperations);
        Assert.Equal(0, client.ActiveConnections);

        state.ReleaseAll();
        await client.StatAsync(new SegmentId("after-cancellation"), CancellationToken.None)
            .WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task CancellationWhilePhysicalPoolIsBusyReleasesUnattachedAdmissionLease()
    {
        var state = new BlockingStatState();
        var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            _ => ValueTask.FromResult<INntpClient>(new BlockingStatClient(state)));
        using var client = new MultiConnectionNntpClient(
            pool,
            ProviderType.Pooled,
            new ProviderCircuitBreaker("budget-pool-wait-test"),
            "budget-pool-wait-test",
            maxTransferConnections: 1);
        using var heldConnection = await pool.GetConnectionLockAsync(
            SemaphorePriority.Low);
        using var cancellation = new CancellationTokenSource();

        var request = client.StatAsync(
            new SegmentId("pool-wait-cancelled"),
            cancellation.Token);
        await WaitUntilAsync(() =>
            client.GetConnectionAdmissionSnapshot()?.ActiveMetadataOperations == 1);
        Assert.Equal(0, Volatile.Read(ref state.Entered));

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        var admission = Assert.IsType<ProviderConnectionAdmissionSnapshot>(
            client.GetConnectionAdmissionSnapshot());
        Assert.Equal(0, admission.ActiveMetadataOperations);
        Assert.Equal(0, admission.WaitingMetadataOperations);
        Assert.Equal(1, client.ActiveConnections);
    }

    [Fact]
    public async Task PoolAcquisitionFailureReleasesAdmissionLease()
    {
        var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            _ => ValueTask.FromException<INntpClient>(
                new IOException("connection factory failed")));
        using var client = new MultiConnectionNntpClient(
            pool,
            ProviderType.Pooled,
            new ProviderCircuitBreaker("budget-failure-test"),
            "budget-failure-test",
            maxTransferConnections: 1);

        await Assert.ThrowsAsync<IOException>(
            () => client.StatAsync(
                new SegmentId("pool-failure"),
                CancellationToken.None));

        var admission = Assert.IsType<ProviderConnectionAdmissionSnapshot>(
            client.GetConnectionAdmissionSnapshot());
        Assert.Equal(0, admission.ActiveMetadataOperations);
        Assert.Equal(0, admission.WaitingMetadataOperations);
        Assert.Equal(0, client.ActiveConnections);
        Assert.Equal(1, client.AvailableConnections);
    }

    [Fact]
    public void ClassifyConnectionKind_CoversEveryOperation()
    {
        var expected = new Dictionary<NntpOperation, ProviderConnectionKind>
        {
            [NntpOperation.Admission] = ProviderConnectionKind.Metadata,
            [NntpOperation.Body] = ProviderConnectionKind.Transfer,
            [NntpOperation.Article] = ProviderConnectionKind.Transfer,
            [NntpOperation.Stat] = ProviderConnectionKind.Metadata,
            [NntpOperation.Head] = ProviderConnectionKind.Metadata,
            [NntpOperation.Date] = ProviderConnectionKind.Metadata,
            [NntpOperation.PipelinedBody] = ProviderConnectionKind.Transfer,
            [NntpOperation.PipelinedArticle] = ProviderConnectionKind.Transfer,
            [NntpOperation.PipelinedStat] = ProviderConnectionKind.Metadata,
            [NntpOperation.Control] = ProviderConnectionKind.Metadata,
        };

        Assert.Equal(Enum.GetValues<NntpOperation>().Length, expected.Count);
        foreach (var operation in Enum.GetValues<NntpOperation>())
        {
            Assert.True(
                expected.TryGetValue(operation, out var expectedKind),
                $"Unclassified operation: {operation}");
            Assert.Equal(
                expectedKind,
                MultiConnectionNntpClient.ClassifyConnectionKind(operation));
        }
    }

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private static MultiConnectionNntpClient CreateClient(
        BlockingStatState state,
        int? maxTransferConnections)
    {
        var pool = new ConnectionPool<INntpClient>(
            maxConnections: 4,
            _ => ValueTask.FromResult<INntpClient>(new BlockingStatClient(state)));
        return new MultiConnectionNntpClient(
            pool,
            ProviderType.Pooled,
            new ProviderCircuitBreaker("budget-test"),
            "budget-test",
            maxTransferConnections: maxTransferConnections);
    }

    private static Task<UsenetStatResponse>[] StartStats(
        MultiConnectionNntpClient client,
        int count) =>
        Enumerable.Range(0, count)
            .Select(i => client.StatAsync(
                new SegmentId($"segment-{i}"),
                CancellationToken.None))
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TestTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Timed out waiting for connection-budget state.");
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
