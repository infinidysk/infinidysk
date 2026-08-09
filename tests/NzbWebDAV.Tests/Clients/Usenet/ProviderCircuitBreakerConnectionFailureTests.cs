using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class ProviderCircuitBreakerConnectionFailureTests
{
    [Fact]
    public void RecordConnectionFailure_OnClosedCircuit_TripsImmediately()
    {
        var breaker = new ProviderCircuitBreaker("unreachable");

        breaker.RecordConnectionFailure("connect-timeout");

        var snapshot = breaker.GetSnapshot();
        Assert.Equal(ProviderCircuitState.Open, snapshot.State);
        Assert.Equal(1, snapshot.FailureCount);
        Assert.Equal(1, snapshot.TripCount);
        Assert.Contains("connection failure", snapshot.LastFailureReason);
    }

    [Fact]
    public async Task RunWithConnection_ConnectionFailureTripsImmediately()
    {
        var attempts = 0;
        var breaker = new ProviderCircuitBreaker("unreachable");
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            _ =>
            {
                Interlocked.Increment(ref attempts);
                throw new IOException("Provider is unreachable.");
            });
        using var client = new MultiConnectionNntpClient(
            pool,
            ProviderType.Pooled,
            breaker,
            "unreachable");

        await Assert.ThrowsAsync<IOException>(
            () => client.StatAsync("segment", CancellationToken.None));

        var snapshot = breaker.GetSnapshot();
        Assert.Equal(2, attempts);
        Assert.Equal(ProviderCircuitState.Open, snapshot.State);
        Assert.Equal(1, snapshot.FailureCount);
        Assert.Equal(1, snapshot.TripCount);
    }

    [Fact]
    public async Task RunWithConnection_ClientAbortMidConnect_DoesNotTripBreaker()
    {
        var breaker = new ProviderCircuitBreaker("aborted");
        using var cts = new CancellationTokenSource();
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            _ =>
            {
                // A client abort (seek/stop) mid-connect can surface as a
                // transport error rather than a cancellation exception.
                cts.Cancel();
                throw new IOException("Connection aborted.");
            });
        using var client = new MultiConnectionNntpClient(
            pool,
            ProviderType.Pooled,
            breaker,
            "aborted");

        await Assert.ThrowsAnyAsync<Exception>(
            () => client.StatAsync("segment", cts.Token));

        Assert.Equal(ProviderCircuitState.Closed, breaker.GetSnapshot().State);
        Assert.Equal(0, breaker.GetSnapshot().TripCount);
    }

    [Fact]
    public async Task DecodedBodiesAsync_ConnectionFailureTripsImmediately()
    {
        var attempts = 0;
        var breaker = new ProviderCircuitBreaker("unreachable-batch");
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            _ =>
            {
                Interlocked.Increment(ref attempts);
                throw new IOException("Provider is unreachable.");
            });
        using var client = new MultiConnectionNntpClient(
            pool,
            ProviderType.Pooled,
            breaker,
            "unreachable-batch");

        await Assert.ThrowsAsync<IOException>(
            () => client.DecodedBodiesAsync(
                new List<SegmentId> { "segment" },
                onConnectionReadyAgain: null,
                CancellationToken.None));

        var snapshot = breaker.GetSnapshot();
        Assert.Equal(2, attempts);
        Assert.Equal(ProviderCircuitState.Open, snapshot.State);
        Assert.Equal(1, snapshot.FailureCount);
        Assert.Equal(1, snapshot.TripCount);
    }

    [Fact]
    public async Task DecodedBodiesPipelinedAsync_ConnectionFailureTripsImmediately()
    {
        var breaker = new ProviderCircuitBreaker("unreachable-pipelined");
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            _ => throw new IOException("Provider is unreachable."));
        using var client = new MultiConnectionNntpClient(
            pool,
            ProviderType.Pooled,
            breaker,
            "unreachable-pipelined");

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var _ in client.DecodedBodiesPipelinedAsync(
                               ["segment"], depth: 1, CancellationToken.None))
            {
            }
        });

        var snapshot = breaker.GetSnapshot();
        Assert.Equal(ProviderCircuitState.Open, snapshot.State);
        Assert.Equal(1, snapshot.FailureCount);
        Assert.Equal(1, snapshot.TripCount);
    }

    [Fact]
    public async Task MultiProvider_ConcurrentRequestsAfterConnectionFailure_UseHealthyFallback()
    {
        var primaryAttempts = 0;
        var primaryBreaker = new ProviderCircuitBreaker("unreachable");
        using var primaryPool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            _ =>
            {
                Interlocked.Increment(ref primaryAttempts);
                throw new IOException("Primary provider is unreachable.");
            });
        using var backupPool = new ConnectionPool<INntpClient>(
            maxConnections: 8,
            _ => ValueTask.FromResult<INntpClient>(new SuccessfulStatClient()));
        using var primary = new MultiConnectionNntpClient(
            primaryPool,
            ProviderType.Pooled,
            primaryBreaker,
            "unreachable");
        using var backup = new MultiConnectionNntpClient(
            backupPool,
            ProviderType.BackupOnly,
            new ProviderCircuitBreaker("healthy-backup"),
            "healthy-backup");
        using var client = new MultiProviderNntpClient([primary, backup]);

        var firstResponse = await client.StatAsync("first", CancellationToken.None);
        Assert.True(firstResponse.ArticleExists);
        Assert.Equal(2, primaryAttempts);
        Assert.Equal(ProviderCircuitState.Open, primaryBreaker.GetSnapshot().State);

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 32)
                .Select(i => client.StatAsync($"concurrent-{i}", CancellationToken.None)));

        Assert.All(responses, response => Assert.True(response.ArticleExists));
        Assert.Equal(2, primaryAttempts);
    }

    [Fact]
    public void RecordConnectionFailure_OnClosedCircuit_UsesExistingCooldownLadder()
    {
        var transitions = new List<ProviderCircuitTransition>();
        var breaker = new ProviderCircuitBreaker("cooldown", transitions.Add);

        breaker.RecordConnectionFailure();
        breaker.ExpireCooldownForTests();
        Assert.False(breaker.IsTripped);
        breaker.RecordConnectionFailure();

        var openTransitions = transitions
            .Where(x => x.State == ProviderCircuitTransitionState.Open)
            .ToList();
        Assert.Equal(2, openTransitions.Count);
        Assert.Equal(TimeSpan.FromSeconds(60), openTransitions[0].Cooldown);
        Assert.Equal(TimeSpan.FromSeconds(120), openTransitions[1].Cooldown);
        Assert.Equal(TimeSpan.FromSeconds(240), breaker.CurrentCooldown);
    }

    [Fact]
    public void RecordConnectionFailure_WhileLatched_IsIgnored()
    {
        var breaker = new ProviderCircuitBreaker("latched");
        breaker.RecordConnectionFailure("first");
        var trippedUntil = breaker.TrippedUntilMs;
        var cooldown = breaker.CurrentCooldown;

        Parallel.For(0, 32, _ => breaker.RecordConnectionFailure("concurrent"));

        var snapshot = breaker.GetSnapshot();
        Assert.Equal(ProviderCircuitState.Open, snapshot.State);
        Assert.Equal(1, snapshot.FailureCount);
        Assert.Equal(1, snapshot.TripCount);
        Assert.Equal(trippedUntil, breaker.TrippedUntilMs);
        Assert.Equal(cooldown, breaker.CurrentCooldown);
    }

    [Fact]
    public void RecordConnectionFailure_WhileHalfOpen_RetripsAndReleasesProbe()
    {
        var breaker = new ProviderCircuitBreaker("half-open");
        breaker.RecordConnectionFailure();
        breaker.ExpireCooldownForTests();
        Assert.False(breaker.IsTripped);

        breaker.RecordConnectionFailure("still-unreachable");

        Assert.Equal(ProviderCircuitState.Open, breaker.GetSnapshot().State);
        Assert.Equal(TimeSpan.FromSeconds(240), breaker.CurrentCooldown);

        breaker.ExpireCooldownForTests();
        Assert.False(breaker.IsTripped);
    }

    [Fact]
    public void RecordConnectionFailure_AfterRecovery_TripsAgainFromInitialCooldown()
    {
        var transitions = new List<ProviderCircuitTransition>();
        var breaker = new ProviderCircuitBreaker("recovered", transitions.Add);
        breaker.RecordConnectionFailure();
        breaker.ExpireCooldownForTests();
        breaker.RecordSuccess();

        breaker.RecordConnectionFailure();

        var openTransitions = transitions
            .Where(x => x.State == ProviderCircuitTransitionState.Open)
            .ToList();
        Assert.Equal(2, openTransitions.Count);
        Assert.All(openTransitions, transition =>
            Assert.Equal(TimeSpan.FromSeconds(60), transition.Cooldown));
        Assert.Equal(ProviderCircuitState.Open, breaker.GetSnapshot().State);
        Assert.Equal(TimeSpan.FromSeconds(120), breaker.CurrentCooldown);
    }

    [Fact]
    public async Task NonBodyFailure_WhileLatched_ReleasesProbeSlot()
    {
        var breaker = new ProviderCircuitBreaker("stat-probe");
        breaker.RecordConnectionFailure();
        breaker.ExpireCooldownForTests();
        Assert.False(breaker.IsTripped);

        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            _ => ValueTask.FromResult<INntpClient>(new FailingStatClient()));
        using var client = new MultiConnectionNntpClient(
            pool,
            ProviderType.Pooled,
            breaker,
            "stat-probe");

        await Assert.ThrowsAsync<IOException>(
            () => client.StatAsync("segment", CancellationToken.None));

        Assert.Equal(ProviderCircuitState.Open, breaker.GetSnapshot().State);
        breaker.ExpireCooldownForTests();
        Assert.False(breaker.IsTripped);
    }

    [Fact]
    public async Task DecodedBodiesAsync_BodyCallbackFailure_TripReasonCarriesTransportDetail()
    {
        var breaker = new ProviderCircuitBreaker("reason-batch");
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            _ => ValueTask.FromResult<INntpClient>(new ReasonReportingBodyClient()));
        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, "reason-batch");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await client.DecodedBodiesAsync(
                new List<SegmentId> { "segment" },
                onConnectionReadyAgain: null,
                CancellationToken.None);
        }

        var snapshot = breaker.GetSnapshot();
        Assert.Equal(ProviderCircuitState.Open, snapshot.State);
        Assert.Contains("NotRetrieved", snapshot.LastFailureReason);
        Assert.Contains("SocketException", snapshot.LastFailureReason);
        Assert.Contains("ConnectionReset", snapshot.LastFailureReason);
    }

    [Fact]
    public async Task DecodedBodyAsync_BodyCallbackFailure_TripReasonCarriesTransportDetail()
    {
        var breaker = new ProviderCircuitBreaker("reason-single");
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            _ => ValueTask.FromResult<INntpClient>(new ReasonReportingBodyClient()));
        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, "reason-single");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await client.DecodedBodyAsync(
                "segment", onConnectionReadyAgain: null, CancellationToken.None);
        }

        var snapshot = breaker.GetSnapshot();
        Assert.Equal(ProviderCircuitState.Open, snapshot.State);
        Assert.Contains("NotRetrieved", snapshot.LastFailureReason);
        Assert.Contains("SocketException", snapshot.LastFailureReason);
        Assert.Contains("ConnectionReset", snapshot.LastFailureReason);
    }

    private sealed class ReasonReportingBodyClient : NntpClient
    {
        public const string Reason = "IOException (SocketException: ConnectionReset)";

        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
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
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            // A body that dies mid-drain: the response headers succeeded, but the
            // completion callback reports the transport failure with its reason.
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved, Reason);
            return Task.FromResult(new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "222 body follows",
                Stream = null,
            });
        }

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            var responses = segmentIds.Select(id =>
            {
                var completion = new TaskCompletionSource<UsenetDecodedBodyResponse>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                completion.SetException(new IOException("Connection reset by peer."));
                return completion.Task;
            }).ToArray();
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved, Reason);
            return Task.FromResult(new UsenetDecodedBodyBatch
            {
                Responses = responses,
            });
        }

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

    private sealed class FailingStatClient : NntpClient
    {
        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new IOException("Provider disconnected during STAT.");

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

    private sealed class SuccessfulStatClient : NntpClient
    {
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
                ResponseCode = (int)UsenetResponseType.ArticleExists,
                ResponseMessage = $"223 0 0 <{segmentId}>",
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
