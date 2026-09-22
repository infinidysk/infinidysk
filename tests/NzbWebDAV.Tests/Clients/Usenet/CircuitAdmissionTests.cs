using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class CircuitAdmissionTests
{
    [Theory]
    [InlineData(false, false, ArticleBodyResult.Discarded)]
    [InlineData(false, true, ArticleBodyResult.Discarded)]
    [InlineData(true, false, ArticleBodyResult.Discarded)]
    [InlineData(true, true, ArticleBodyResult.Discarded)]
    [InlineData(false, false, ArticleBodyResult.NotRetrieved)]
    [InlineData(false, true, ArticleBodyResult.NotRetrieved)]
    [InlineData(true, false, ArticleBodyResult.NotRetrieved)]
    [InlineData(true, true, ArticleBodyResult.NotRetrieved)]
    public async Task BodyCompletion_ReplacesUnsafeConnection_ChargesOnlyProviderFailures(
        bool batched, bool halfOpen, ArticleBodyResult result)
    {
        var inner = new CompletionBodyClient();
        var breaker = halfOpen ? HalfOpen() : new ProviderCircuitBreaker("body-completion");
        var before = breaker.GetSnapshot();
        var cooldown = breaker.CurrentCooldown;
        using var pool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(inner));
        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, "body-completion");
        var callbacks = 0;
        ArticleBodyResult? observed = null;
        void OnCompleted(ArticleBodyResult completed, string? reason)
        {
            callbacks++;
            observed = completed;
        }
        UsenetDecodedBodyBatch? batch = null;
        if (batched)
            batch = await client.DecodedBodiesAsync(
                [new SegmentId("segment@example.com")], OnCompleted, CancellationToken.None);
        else
            await client.DecodedBodyAsync(
                new SegmentId("segment@example.com"), OnCompleted, CancellationToken.None);

        inner.Callback!(result, "diagnostic text is not a classification");
        inner.Callback(result, "duplicate");
        if (batch is not null)
            await batch.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, callbacks);
        Assert.Equal(ArticleBodyResult.NotRetrieved, observed);
        Assert.Equal(0, pool.LiveConnections);
        Assert.Equal(0, pool.IdleConnections);
        Assert.Equal(before.FailureCount + (result == ArticleBodyResult.NotRetrieved ? 1 : 0),
            breaker.GetSnapshot().FailureCount);
        if (result == ArticleBodyResult.Discarded)
        {
            Assert.Equal(before.State, breaker.GetSnapshot().State);
            Assert.Equal(cooldown, breaker.CurrentCooldown);
            Assert.True(breaker.TryAdmit(out var probe));
            breaker.ReleaseProbe(probe);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var recovered = await pool.GetConnectionLockAsync(SemaphorePriority.High, deadline.Token);
        }
        else if (halfOpen)
        {
            Assert.Equal(ProviderCircuitState.Open, breaker.GetSnapshot().State);
        }
    }

    [Theory]
    [InlineData(false, ArticleBodyResult.Cancelled)]
    [InlineData(false, ArticleBodyResult.NotRetrieved)]
    [InlineData(true, ArticleBodyResult.Cancelled)]
    [InlineData(true, ArticleBodyResult.NotRetrieved)]
    public async Task BodyAsync_CancelledHalfOpenCompletion_ReleasesProbe(bool batched, ArticleBodyResult result)
    {
        var inner = new CompletionBodyClient();
        var breaker = HalfOpen();
        using var pool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(inner));
        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, "cancelled-body-probe");
        using var cancellation = new CancellationTokenSource();
        UsenetDecodedBodyBatch? batch = null;
        if (batched)
            batch = await client.DecodedBodiesAsync(
                [new SegmentId("segment@example.com")], null, cancellation.Token);
        else
            await client.DecodedBodyAsync(new SegmentId("segment@example.com"), null, cancellation.Token);

        cancellation.Cancel();
        inner.Callback!(result);
        if (batch is not null)
            await batch.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(breaker.TryAdmit(out var probe));
        Assert.False(probe.IsNone);
        breaker.ReleaseProbe(probe);
    }

    [Fact]
    public async Task IdleReuse_CircuitTripBeforeCommit_ReturnsConnectionAndPermit()
    {
        var connection = new object();
        var breaker = new ProviderCircuitBreaker("idle-commit-race");
        using var pool = new ConnectionPool<object>(1, _ => ValueTask.FromResult(connection));
        (await pool.GetConnectionLockAsync(SemaphorePriority.High)).Dispose();
        var acquisition = breaker.BeginAcquisition(CircuitProbeLease.None);
        var tripped = false;
        pool.OnConnectionPoolChanged += (_, _) =>
        {
            if (tripped)
                return;
            tripped = true;
            breaker.RecordFailure();
            breaker.RecordFailure();
            breaker.RecordFailure();
        };

        await Assert.ThrowsAsync<CircuitAdmissionRejectedException>(() =>
            pool.GetConnectionLockAsync(
                SemaphorePriority.High, CancellationToken.None, null,
                System.Diagnostics.Stopwatch.GetTimestamp(), null, acquisition));

        Assert.Equal(1, pool.LiveConnections);
        Assert.Equal(1, pool.IdleConnections);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var recovered = await pool.GetConnectionLockAsync(SemaphorePriority.High, deadline.Token);
        Assert.Same(connection, recovered.Connection);
        Assert.True(recovered.WasReused);
    }

    [Fact]
    public async Task DateAsync_OpenCircuit_DoesNotSendCommand()
    {
        var inner = new CountingDateClient();
        var breaker = TripOpen();
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1, _ => ValueTask.FromResult<INntpClient>(inner));
        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, "open-date");

        var ex = await Assert.ThrowsAnyAsync<RetryableDownloadException>(
            () => client.DateAsync(CancellationToken.None));

        Assert.Contains("circuit", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, inner.DateCalls);
        Assert.Equal(ProviderCircuitState.Open, breaker.GetSnapshot().State);
    }

    [Fact]
    public async Task DateAsync_HalfOpenProbeInFlight_DoesNotSendSecondCommand()
    {
        var inner = new GatedDateClient();
        var breaker = HalfOpen();
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 2, _ => ValueTask.FromResult<INntpClient>(inner));
        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, "half-open-date");

        var first = client.DateAsync(CancellationToken.None);
        await inner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, inner.DateCalls);

        var ex = await Assert.ThrowsAnyAsync<RetryableDownloadException>(
            () => client.DateAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Contains("half-open", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, inner.DateCalls);

        inner.Release.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DateAsync_RetryAfterClosedAdmission_DoesNotSendWhenAnotherCallerOwnsProbe()
    {
        var breaker = new ProviderCircuitBreaker("stale-none-lease");
        var inner = new StealProbeOnFirstDateClient(breaker);
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 2, _ => ValueTask.FromResult<INntpClient>(inner));
        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, "stale-none-lease");

        // The stolen probe latches the breaker, so the local retry is skipped (a retry
        // would be rejected at admission anyway) and the original error surfaces.
        await Assert.ThrowsAsync<IOException>(
            () => client.DateAsync(CancellationToken.None));

        Assert.Equal(1, inner.DateCalls);
        Assert.Equal(ProviderCircuitState.HalfOpen, breaker.GetSnapshot().State);
        Assert.False(breaker.TryAdmit(out _));
    }

    [Fact]
    public async Task DecodedBodiesAsync_OpenCircuit_DoesNotSendCommand()
    {
        var inner = new CountingBodiesClient();
        var breaker = TripOpen();
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1, _ => ValueTask.FromResult<INntpClient>(inner));
        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, "open-bodies");

        var callbacks = 0;
        await Assert.ThrowsAnyAsync<RetryableDownloadException>(
            () => client.DecodedBodiesAsync(
                [new SegmentId("seg@example.com")],
                (result, _) =>
                {
                    Interlocked.Increment(ref callbacks);
                    Assert.Equal(ArticleBodyResult.NotRetrieved, result);
                },
                CancellationToken.None));

        Assert.Equal(0, inner.Calls);
        Assert.Equal(1, callbacks);
        Assert.Equal(ProviderCircuitState.Open, breaker.GetSnapshot().State);
    }

    [Fact]
    public async Task DecodedBodiesPipelinedAsync_OpenCircuit_DoesNotStartBatch()
    {
        var inner = new MissPipelinedBodyClient();
        var breaker = TripOpen();
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1, _ => ValueTask.FromResult<INntpClient>(inner));
        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, "open-pipe");

        await Assert.ThrowsAnyAsync<RetryableDownloadException>(async () =>
        {
            await foreach (var _ in client.DecodedBodiesPipelinedAsync(
                ["seg@example.com"], 1, CancellationToken.None))
            {
            }
        });

        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task DecodedBodiesPipelinedAsync_Miss_ClosesHalfOpenWithoutResettingLadder()
    {
        var inner = new MissPipelinedBodyClient();
        var breaker = HalfOpen();
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1, _ => ValueTask.FromResult<INntpClient>(inner));
        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, "miss-pipe");

        PipelinedBodyResult? result = null;
        await foreach (var item in client.DecodedBodiesPipelinedAsync(
            ["seg@example.com"], 1, CancellationToken.None))
            result = item;

        Assert.NotNull(result);
        Assert.False(result.Found);
        Assert.Equal(1, inner.Calls);
        Assert.Equal(ProviderCircuitState.Closed, breaker.GetSnapshot().State);
        Assert.Equal(TimeSpan.FromSeconds(120), breaker.CurrentCooldown);
        Assert.Equal(1, breaker.GetSnapshot().ArticleMissCount);
    }

    [Fact]
    public async Task DateAsync_CallerCancellationDuringHalfOpenProbe_ReleasesProbeSlot()
    {
        var inner = new GatedDateClient();
        var breaker = HalfOpen();
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1, _ => ValueTask.FromResult<INntpClient>(inner));
        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, "cancel-probe");
        using var cts = new CancellationTokenSource();

        var first = client.DateAsync(cts.Token);
        await inner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        // The abandoned probe is released immediately instead of blocking admission
        // until the abandon timeout expires.
        Assert.True(breaker.TryAdmit(out var probe));
        Assert.False(probe.IsNone);
    }

    [Fact]
    public async Task DecodedBodiesPipelinedAsync_NonDefinitiveMiss_RetripsHalfOpenAsFailure()
    {
        var inner = new MissPipelinedBodyClient(definitivelyMissing: false);
        var breaker = HalfOpen();
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1, _ => ValueTask.FromResult<INntpClient>(inner));
        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, "mismatch-pipe");

        await foreach (var _ in client.DecodedBodiesPipelinedAsync(
            ["seg@example.com"], 1, CancellationToken.None))
        {
        }

        // A segment-id mismatch is a protocol failure, not a clean miss: it must
        // re-trip the half-open circuit instead of closing it as article-not-found.
        // HalfOpen() records 3 failures to trip; the mismatch adds the 4th.
        var snapshot = breaker.GetSnapshot();
        Assert.Equal(ProviderCircuitState.Open, snapshot.State);
        Assert.Equal(2, snapshot.TripCount);
        Assert.Equal(4, snapshot.FailureCount);
        Assert.Equal(0, snapshot.ArticleMissCount);
    }

    private static ProviderCircuitBreaker TripOpen(string name = "admission")
    {
        var breaker = new ProviderCircuitBreaker(name);
        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordFailure();
        Assert.Equal(ProviderCircuitState.Open, breaker.GetSnapshot().State);
        return breaker;
    }

    private static ProviderCircuitBreaker HalfOpen(string name = "admission")
    {
        var breaker = TripOpen(name);
        breaker.ExpireCooldownForTests();
        Assert.Equal(ProviderCircuitState.HalfOpen, breaker.GetSnapshot().State);
        return breaker;
    }

    private abstract class StubNntpClient : NntpClient
    {
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

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }

    private sealed class CountingDateClient : StubNntpClient
    {
        public int DateCalls;

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref DateCalls);
            return Task.FromResult(OkDate());
        }
    }

    private sealed class GatedDateClient : StubNntpClient
    {
        public int DateCalls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref DateCalls);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return OkDate();
        }
    }

    private sealed class StealProbeOnFirstDateClient(ProviderCircuitBreaker breaker) : StubNntpClient
    {
        public int DateCalls;

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref DateCalls);
            if (n == 1)
            {
                breaker.RecordFailure();
                breaker.RecordFailure();
                breaker.RecordFailure();
                breaker.ExpireCooldownForTests();
                Assert.True(breaker.TryAdmit(out var stolen));
                Assert.False(stolen.IsNone);
                throw new IOException("first attempt failed after another caller claimed the probe");
            }

            return Task.FromResult(OkDate());
        }
    }

    private sealed class CompletionBodyClient : StubNntpClient
    {
        public ArticleBodyCompletionHandler? Callback { get; private set; }

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            Callback = onConnectionReadyAgain;
            return Task.FromResult(new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId,
                ResponseCode = 222,
                ResponseMessage = "222 body follows",
                Stream = null
            });
        }

        public override async Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds, ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            var response = await DecodedBodyAsync(segmentIds[0], onConnectionReadyAgain, cancellationToken);
            return new UsenetDecodedBodyBatch
            {
                Responses = [Task.FromResult(response)],
                Completion = Task.CompletedTask
            };
        }
    }

    private sealed class CountingBodiesClient : StubNntpClient
    {
        public int Calls;

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("NNTP BODY batch should not run when admission is rejected.");
        }
    }

    private sealed class MissPipelinedBodyClient(bool definitivelyMissing = true) : StubNntpClient
    {
        public int Calls;

        public override async IAsyncEnumerable<PipelinedBodyResult> DecodedBodiesPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            int depth,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            await Task.Yield();
            foreach (var segmentId in segmentIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new PipelinedBodyResult
                {
                    SegmentId = segmentId,
                    Found = false,
                    DefinitivelyMissing = definitivelyMissing,
                };
            }
        }
    }

    private static UsenetDateResponse OkDate() => new()
    {
        ResponseCode = (int)UsenetResponseType.DateAndTime,
        ResponseMessage = "111 20260804120000",
        DateTime = DateTimeOffset.Parse("2026-08-04T12:00:00Z"),
    };
}
