using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class ProviderRecoveryProbeTests
{
    [Fact]
    public async Task HalfOpenProvider_SuccessfulProbe_ClosesCircuit()
    {
        var breaker = HalfOpenBreaker();
        var dateClient = new DateProbeClient(UsenetResponseType.DateAndTime);
        using var client = CreateClient(
            breaker,
            _ => ValueTask.FromResult<INntpClient>(dateClient));

        await client.ProbeLatchedProvidersAsync(CancellationToken.None);

        Assert.Equal(1, dateClient.DateRequests);
        Assert.Equal(ProviderCircuitState.Closed, breaker.GetSnapshot().State);
    }

    [Fact]
    public async Task HalfOpenProvider_AuthenticationFailure_ReopensCircuitWithBackoff()
    {
        var breaker = HalfOpenBreaker();
        var cooldownBeforeProbe = breaker.CurrentCooldown;
        var attempts = 0;
        using var client = CreateClient(
            breaker,
            _ =>
            {
                Interlocked.Increment(ref attempts);
                return ValueTask.FromException<INntpClient>(
                    new CouldNotLoginToUsenetException("481 Access Denied"));
            });

        await client.ProbeLatchedProvidersAsync(CancellationToken.None);

        Assert.Equal(1, attempts);
        Assert.Equal(ProviderCircuitState.Open, breaker.GetSnapshot().State);
        Assert.True(breaker.CurrentCooldown > cooldownBeforeProbe);
    }

    [Fact]
    public async Task OpenProvider_IsNotProbedBeforeCooldownExpires()
    {
        var breaker = new ProviderCircuitBreaker("open");
        breaker.RecordConnectionFailure("auth");
        var attempts = 0;
        using var client = CreateClient(
            breaker,
            _ =>
            {
                Interlocked.Increment(ref attempts);
                return ValueTask.FromResult<INntpClient>(
                    new DateProbeClient(UsenetResponseType.DateAndTime));
            });

        await client.ProbeLatchedProvidersAsync(CancellationToken.None);

        Assert.Equal(0, attempts);
        Assert.Equal(ProviderCircuitState.Open, breaker.GetSnapshot().State);
    }

    [Fact]
    public async Task ClosedProvider_IsNotProbed()
    {
        var breaker = new ProviderCircuitBreaker("closed");
        var attempts = 0;
        using var client = CreateClient(
            breaker,
            _ =>
            {
                Interlocked.Increment(ref attempts);
                return ValueTask.FromResult<INntpClient>(
                    new DateProbeClient(UsenetResponseType.DateAndTime));
            });

        await client.ProbeLatchedProvidersAsync(CancellationToken.None);

        Assert.Equal(0, attempts);
        Assert.Equal(ProviderCircuitState.Closed, breaker.GetSnapshot().State);
    }

    [Theory]
    [InlineData(UsenetResponseType.CommandNotRecognized, "500 Unknown command")]
    [InlineData(UsenetResponseType.ServiceDiscontinued, "400 Unrecognized command")]
    public async Task HalfOpenProvider_UnsupportedDateResponse_ClosesCircuitWithoutResettingBackoff(
        UsenetResponseType responseType,
        string responseMessage)
    {
        var breaker = HalfOpenBreaker();
        var cooldownBeforeProbe = breaker.CurrentCooldown;
        var dateClient = new DateProbeClient(responseType, responseMessage);
        using var client = CreateClient(
            breaker,
            _ => ValueTask.FromResult<INntpClient>(dateClient));

        await client.ProbeLatchedProvidersAsync(CancellationToken.None);

        Assert.Equal(1, dateClient.DateRequests);
        Assert.Equal(ProviderCircuitState.Closed, breaker.GetSnapshot().State);
        Assert.Equal(cooldownBeforeProbe, breaker.CurrentCooldown);
    }

    [Theory]
    [InlineData(UsenetResponseType.ServiceDiscontinued, "400 Service temporarily unavailable")]
    [InlineData(UsenetResponseType.ServiceDiscontinued, "400 Unrecognized command; closing connection")]
    [InlineData(UsenetResponseType.AuthenticationRequired, "480 Authentication required")]
    [InlineData(UsenetResponseType.AuthenticationRejected, "481 Access Denied")]
    [InlineData(UsenetResponseType.AccessPermanentlyForbidden, "502 Permission denied")]
    public async Task HalfOpenProvider_RejectedDateResponse_DoesNotCloseCircuit(
        UsenetResponseType responseType,
        string responseMessage)
    {
        var breaker = HalfOpenBreaker();
        var cooldownBeforeProbe = breaker.CurrentCooldown;
        var dateClient = new DateProbeClient(responseType, responseMessage);
        using var client = CreateClient(
            breaker,
            _ => ValueTask.FromResult<INntpClient>(dateClient));

        await client.ProbeLatchedProvidersAsync(CancellationToken.None);

        Assert.Equal(1, dateClient.DateRequests);
        Assert.Equal(ProviderCircuitState.Open, breaker.GetSnapshot().State);
        Assert.True(breaker.CurrentCooldown > cooldownBeforeProbe);
    }

    [Theory]
    [InlineData(UsenetResponseType.DateAndTime, "111 20260804120000")]
    [InlineData(UsenetResponseType.CommandNotRecognized, "500 Unknown command")]
    [InlineData(UsenetResponseType.ServiceDiscontinued, "400 Unrecognized command")]
    public async Task Sweeper_AcceptedDateResponse_RetainsWarmConnection(
        UsenetResponseType responseType,
        string responseMessage)
    {
        var safetyTimeout = TimeSpan.FromSeconds(5);
        var dateClient = new DateProbeClient(responseType, responseMessage);
        var connectionAttempts = 0;
        await using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            connectionFactory: _ =>
            {
                Interlocked.Increment(ref connectionAttempts);
                return ValueTask.FromResult<INntpClient>(dateClient);
            },
            idleTimeout: TimeSpan.FromHours(1),
            warmConnectionFloor: 1,
            keepAlive: UsenetStreamingClient.KeepAliveAsync);

        // A manual borrow races the background floor refill, which can then park on the only permit.
        await WaitUntilAsync(() => pool.LiveConnections == 1 && pool.IdleConnections == 1, safetyTimeout);

        for (var sweepIndex = 0; sweepIndex < 2; sweepIndex++)
        {
            await pool.SweepOnceForTestsAsync().WaitAsync(safetyTimeout);
        }

        Assert.Equal(2, dateClient.DateRequests);
        Assert.Equal(1, Volatile.Read(ref connectionAttempts));
        Assert.Equal(1, pool.LiveConnections);
        Assert.Equal(1, pool.IdleConnections);
        Assert.Equal(0, pool.ActiveConnections);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition(), "Timed out waiting for connection-pool state.");
    }

    private static ProviderCircuitBreaker HalfOpenBreaker()
    {
        var breaker = new ProviderCircuitBreaker("recovering");
        breaker.RecordConnectionFailure("auth");
        breaker.ExpireCooldownForTests();
        Assert.Equal(ProviderCircuitState.HalfOpen, breaker.GetSnapshot().State);
        return breaker;
    }

    private static MultiProviderNntpClient CreateClient(
        ProviderCircuitBreaker breaker,
        Func<CancellationToken, ValueTask<INntpClient>> connectionFactory)
    {
        var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            connectionFactory);
        var provider = new MultiConnectionNntpClient(
            pool,
            ProviderType.Pooled,
            breaker,
            "recovering");
        return new MultiProviderNntpClient([provider]);
    }

    private sealed class DateProbeClient(
        UsenetResponseType responseType,
        string? responseMessage = null) : NntpClient
    {
        public int DateRequests { get; private set; }

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

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateRequests++;
            return Task.FromResult(new UsenetDateResponse
            {
                ResponseCode = (int)responseType,
                ResponseMessage = responseMessage ?? (
                    responseType == UsenetResponseType.DateAndTime
                        ? "111 20260804120000"
                        : "481 Access Denied"),
                DateTime = responseType == UsenetResponseType.DateAndTime
                    ? DateTimeOffset.Parse("2026-08-04T12:00:00Z")
                    : null,
            });
        }

        public override void Dispose()
        {
        }
    }
}
