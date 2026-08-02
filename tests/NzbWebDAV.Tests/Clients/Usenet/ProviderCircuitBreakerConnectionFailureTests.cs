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
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }
}
