using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class MultiConnectionStatsPipelinedTests
{
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

    private sealed class ExistsStatClient : NntpClient
    {
        public override async IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            int depth,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            foreach (var segmentId in segmentIds)
            {
                yield return new PipelinedStatResult
                {
                    SegmentId = segmentId,
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
