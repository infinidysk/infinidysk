using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class MultiConnectionStatsPipelinedTests
{
    [Fact]
    public async Task StatsPipelinedAsync_HoldsOneAdmissionLeaseForFullEnumeration()
    {
        var inner = new ExistsStatClient();
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1, _ => ValueTask.FromResult<INntpClient>(inner));
        using var client = new MultiConnectionNntpClient(
            pool,
            ProviderType.Pooled,
            new ProviderCircuitBreaker("stat-pipeline-admission"),
            "stat-pipeline-admission",
            maxTransferConnections: 1);

        await using var enumerator = client.StatsPipelinedAsync(
                ["a@example", "b@example"], depth: 8, CancellationToken.None)
            .GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        var whileEnumerating = Assert.IsType<ProviderConnectionAdmissionSnapshot>(
            client.GetConnectionAdmissionSnapshot());
        Assert.Equal(1, whileEnumerating.ActiveMetadataOperations);
        Assert.Equal(0, client.AvailableConnections);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.False(await enumerator.MoveNextAsync());

        var afterEnumeration = Assert.IsType<ProviderConnectionAdmissionSnapshot>(
            client.GetConnectionAdmissionSnapshot());
        Assert.Equal(0, afterEnumeration.ActiveMetadataOperations);
        Assert.Equal(1, client.AvailableConnections);
    }

    [Fact]
    public async Task StatsPipelinedAsync_ReleasesAdmissionLeaseAfterEnumerationFailure()
    {
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            _ => ValueTask.FromResult<INntpClient>(new ExistsStatClient(failAfterFirst: true)));
        using var client = new MultiConnectionNntpClient(
            pool,
            ProviderType.Pooled,
            new ProviderCircuitBreaker("stat-pipeline-failure"),
            "stat-pipeline-failure",
            maxTransferConnections: 1);
        await using var enumerator = client.StatsPipelinedAsync(
                ["a@example", "b@example"], depth: 8, CancellationToken.None)
            .GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await enumerator.MoveNextAsync().AsTask());

        var afterFailure = Assert.IsType<ProviderConnectionAdmissionSnapshot>(
            client.GetConnectionAdmissionSnapshot());
        Assert.Equal(0, afterFailure.ActiveMetadataOperations);
    }

    [Fact]
    public async Task StatsPipelinedAsync_ReleasesAdmissionLeaseAfterCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            _ => ValueTask.FromResult<INntpClient>(
                new ExistsStatClient(waitForCancellationAfterFirst: true)));
        using var client = new MultiConnectionNntpClient(
            pool,
            ProviderType.Pooled,
            new ProviderCircuitBreaker("stat-pipeline-cancellation"),
            "stat-pipeline-cancellation",
            maxTransferConnections: 1);
        await using var enumerator = client.StatsPipelinedAsync(
                ["a@example", "b@example"], depth: 8, cancellation.Token)
            .GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await enumerator.MoveNextAsync().AsTask());

        var afterCancellation = Assert.IsType<ProviderConnectionAdmissionSnapshot>(
            client.GetConnectionAdmissionSnapshot());
        Assert.Equal(0, afterCancellation.ActiveMetadataOperations);
    }

    [Fact]
    public async Task StatsPipelinedAsync_DoesNotRecordCircuitBreakerSuccess()
    {
        var inner = new ExistsStatClient();
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1, _ => ValueTask.FromResult<INntpClient>(inner));

        var breaker = new ProviderCircuitBreaker("stat-pipeline");
        breaker.RecordFailure("seed-1");
        breaker.RecordFailure("seed-2");
        breaker.RecordFailure("seed-3");
        Assert.True(breaker.IsTripped);

        using var client = new MultiConnectionNntpClient(
            pool,
            ProviderType.Pooled,
            breaker,
            "stat-pipeline");

        var results = new List<PipelinedStatResult>();
        await foreach (var result in client.StatsPipelinedAsync(
                           ["a@example", "b@example"], depth: 8, CancellationToken.None))
        {
            results.Add(result);
        }

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Exists));
        // STAT must not feed the breaker — a successful sweep must not clear a trip.
        Assert.True(breaker.IsTripped);
    }

    private sealed class ExistsStatClient(
        bool failAfterFirst = false,
        bool waitForCancellationAfterFirst = false) : NntpClient
    {
        public override async IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            int depth,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            for (var index = 0; index < segmentIds.Count; index++)
            {
                if (index > 0 && failAfterFirst)
                    throw new InvalidOperationException("pipeline failure");
                if (index > 0 && waitForCancellationAfterFirst)
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

                yield return new PipelinedStatResult
                {
                    SegmentId = segmentIds[index],
                    Exists = true,
                };
            }
        }

        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            Task.FromResult(new UsenetStatResponse
            {
                ResponseCode = 223,
                ResponseMessage = $"223 <{segmentId}>",
                ArticleExists = true,
            });

        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }
}
