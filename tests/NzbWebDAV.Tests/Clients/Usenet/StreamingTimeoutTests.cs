using System.Diagnostics;
using System.Text;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Clients.Usenet;

[Collection(nameof(GlobalLoggerCollection))]
public class StreamingTimeoutTests
{
    [Fact]
    public async Task TransferAdmissionFailoverTimeoutRemovesWaiterWithoutPenalizingProvider()
    {
        var inner = new LateBodyCompletionClient();
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            _ => ValueTask.FromResult<INntpClient>(inner));
        var breaker = new ProviderCircuitBreaker("admission-timeout");
        using var client = new MultiConnectionNntpClient(
            pool,
            ProviderType.Pooled,
            breaker,
            "admission-timeout",
            maxTransferConnections: 1);

        UsenetDecodedBodyResponse? held = null;
        try
        {
            held = await client.DecodedBodyAsync("seg", CancellationToken.None);
            using var callerCts = new CancellationTokenSource();
            using var failoverContext = callerCts.Token.SetContext(
                new TransferAdmissionFailoverContext(
                    () => true,
                    TimeSpan.FromMilliseconds(50)));

            await Assert.ThrowsAsync<ProviderTransferAdmissionTimeoutException>(() =>
                client.DecodedBodyAsync("seg", callerCts.Token));
        }
        finally
        {
            inner.Complete(ArticleBodyResult.Cancelled);
            if (held?.Stream is not null)
                await held.Stream.DisposeAsync();
        }

        var snapshot = client.GetConnectionAdmissionSnapshot()!;
        Assert.Equal(0, snapshot.WaitingTransferOperations);
        Assert.Equal(0, breaker.GetSnapshot().FailureCount);
    }

    [Fact]
    public async Task PipelinedTransferAdmissionFailoverTimeoutDoesNotPenalizeProvider()
    {
        var inner = new LateBodyCompletionClient();
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            _ => ValueTask.FromResult<INntpClient>(inner));
        var breaker = new ProviderCircuitBreaker("pipelined-admission-timeout");
        using var client = new MultiConnectionNntpClient(
            pool,
            ProviderType.Pooled,
            breaker,
            "pipelined-admission-timeout",
            maxTransferConnections: 1);

        UsenetDecodedBodyResponse? held = null;
        try
        {
            held = await client.DecodedBodyAsync("seg", CancellationToken.None);
            using var callerCts = new CancellationTokenSource();
            using var failoverContext = callerCts.Token.SetContext(
                new TransferAdmissionFailoverContext(
                    () => true,
                    TimeSpan.FromMilliseconds(50)));

            async Task EnumerateAsync()
            {
                await foreach (var response in client.DecodedBodiesPipelinedAsync(
                                   ["seg"], depth: 1, callerCts.Token))
                {
                    _ = response;
                }
            }

            await Assert.ThrowsAsync<ProviderTransferAdmissionTimeoutException>(EnumerateAsync);
        }
        finally
        {
            inner.Complete(ArticleBodyResult.Cancelled);
            if (held?.Stream is not null)
                await held.Stream.DisposeAsync();
        }

        var snapshot = client.GetConnectionAdmissionSnapshot()!;
        Assert.Equal(0, snapshot.WaitingTransferOperations);
        Assert.Equal(0, breaker.GetSnapshot().FailureCount);
    }

    [Fact]
    public async Task RunWithConnection_CancellationAfterBodyReturn_ReleasesTransfer()
    {
        var inner = new LateBodyCompletionClient();
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1, _ => ValueTask.FromResult<INntpClient>(inner));
        var breaker = new ProviderCircuitBreaker("late-body-cancel");
        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, "late-body-cancel", maxTransferConnections: 1);
        using var callerCts = new CancellationTokenSource();
        using var timeoutScope = callerCts.Token.SetContext(new StreamingTimeoutContext
        {
            PerSegmentTimeout = TimeSpan.FromMinutes(1),
            MaxRetries = 0,
        });
        var recorder = new ArticleBodyCompletionRecorder();

        var response = await client.DecodedBodyAsync("seg", recorder.Invoke, callerCts.Token);
        using var stream = response.Stream;
        using var registration = inner.BodyToken.Register(() => inner.Complete(ArticleBodyResult.Cancelled));
        try
        {
            Assert.Equal(1, client.GetConnectionAdmissionSnapshot()!.ActiveTransferOperations);
            await callerCts.CancelAsync();
            Assert.True(inner.BodyToken.IsCancellationRequested);
            Assert.Equal(0, client.GetConnectionAdmissionSnapshot()!.ActiveTransferOperations);
            Assert.Equal(0, client.ActiveConnections);
            Assert.Equal(1, recorder.Count);
            Assert.Equal(ArticleBodyResult.Cancelled, recorder.Result);
            Assert.Equal(0, breaker.GetSnapshot().FailureCount);
        }
        finally
        {
            inner.Complete(ArticleBodyResult.Cancelled);
        }
    }

    [Fact]
    public async Task MissingArticle_ReturnsCleanMissWithoutReplacingConnection()
    {
        var breaker = new ProviderCircuitBreaker("missing-article");
        var inner = new FakeNntpClient(new Dictionary<string, byte[]>());
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            _ => ValueTask.FromResult<INntpClient>(inner));
        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, "missing-article");
        ArticleBodyResult? callbackResult = null;

        await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() =>
            client.DecodedBodyAsync(
                "missing",
                (result, _) => callbackResult = result,
                CancellationToken.None));

        Assert.Equal(ArticleBodyResult.NotFound, callbackResult);
        Assert.Equal(1, breaker.GetSnapshot().ArticleMissCount);
        Assert.Equal(0, breaker.GetSnapshot().FailureCount);
        Assert.Equal(1, pool.LiveConnections);
        Assert.Equal(1, pool.IdleConnections);
        Assert.Equal(0, pool.GetChurn().ConnectionsDestroyed);
    }

    [Fact]
    public async Task RunWithConnection_DisposedPool_DoesNotRetryOrPenalizeProvider()
    {
        var breaker = new ProviderCircuitBreaker("retired-pool");
        var created = 0;
        var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            _ =>
            {
                Interlocked.Increment(ref created);
                return ValueTask.FromResult<INntpClient>(new HangingNntpClient());
            });
        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, "retired-pool");
        using var heldConnection = await pool.GetConnectionLockAsync(SemaphorePriority.Low);

        var callbacks = 0;
        ArticleBodyResult? callbackResult = null;
        var request = client.DecodedBodyAsync(
            "seg",
            (result, _) =>
            {
                callbackResult = result;
                Interlocked.Increment(ref callbacks);
            },
            CancellationToken.None);
        await Task.Delay(50);

        await pool.DisposeAsync();

        var exception = await Assert.ThrowsAsync<NntpClientRetiredException>(() => request);
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
        Assert.Equal(1, created);
        Assert.Equal(1, callbacks);
        Assert.Equal(ArticleBodyResult.NotRetrieved, callbackResult);
        Assert.Equal(0, breaker.GetSnapshot().FailureCount);
    }

    [Fact]
    public async Task DecodedBodiesAsync_DisposedPool_DoesNotRetryOrPenalizeProvider()
    {
        var breaker = new ProviderCircuitBreaker("retired-batch-pool");
        var created = 0;
        var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            _ =>
            {
                Interlocked.Increment(ref created);
                return ValueTask.FromResult<INntpClient>(new HangingNntpClient());
            });
        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, "retired-batch-pool");
        using var heldConnection = await pool.GetConnectionLockAsync(SemaphorePriority.Low);

        var callbacks = 0;
        ArticleBodyResult? callbackResult = null;
        var request = client.DecodedBodiesAsync(
            ["seg-a", "seg-b"],
            (result, _) =>
            {
                callbackResult = result;
                Interlocked.Increment(ref callbacks);
            },
            CancellationToken.None);
        await Task.Delay(50);

        await pool.DisposeAsync();

        var exception = await Assert.ThrowsAsync<NntpClientRetiredException>(() => request);
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
        Assert.Equal(1, created);
        Assert.Equal(1, callbacks);
        Assert.Equal(ArticleBodyResult.NotRetrieved, callbackResult);
        Assert.Equal(0, breaker.GetSnapshot().FailureCount);
    }

    [Fact]
    public async Task RunWithConnection_AlreadyDisposedPool_TranslatesObjectDisposedException()
    {
        var breaker = new ProviderCircuitBreaker("already-retired-pool");
        var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            _ => ValueTask.FromResult<INntpClient>(new HangingNntpClient()));
        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, "already-retired-pool");
        await pool.DisposeAsync();

        ArticleBodyResult? callbackResult = null;
        var exception = await Assert.ThrowsAsync<NntpClientRetiredException>(() =>
            client.DecodedBodyAsync(
                "seg",
                (result, _) => callbackResult = result,
                CancellationToken.None));

        Assert.IsAssignableFrom<ObjectDisposedException>(exception.InnerException);
        Assert.Equal(ArticleBodyResult.NotRetrieved, callbackResult);
        Assert.Equal(0, breaker.GetSnapshot().FailureCount);
    }

    [Fact]
    public async Task MultiProvider_RetiredGeneration_DoesNotTryNextProvider()
    {
        var retiredBreaker = new ProviderCircuitBreaker("retired-primary");
        var retiredPool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            _ => ValueTask.FromResult<INntpClient>(new HangingNntpClient()));
        var retired = new MultiConnectionNntpClient(
            retiredPool, ProviderType.Pooled, retiredBreaker, "retired-primary", priority: 0);
        await retiredPool.DisposeAsync();

        var fallbackCreated = 0;
        var fallbackPool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            _ =>
            {
                Interlocked.Increment(ref fallbackCreated);
                return ValueTask.FromResult<INntpClient>(
                    new HealthyNntpClient(new Dictionary<string, byte[]>
                    {
                        ["seg"] = [1, 2, 3, 4],
                    }));
            });
        var fallback = new MultiConnectionNntpClient(
            fallbackPool,
            ProviderType.Pooled,
            new ProviderCircuitBreaker("fallback"),
            "fallback",
            priority: 1);
        using var client = new MultiProviderNntpClient([retired, fallback]);

        var callbacks = 0;
        ArticleBodyResult? callbackResult = null;
        await Assert.ThrowsAsync<NntpClientRetiredException>(() =>
            client.DecodedBodyAsync(
                "seg",
                (result, _) =>
                {
                    callbackResult = result;
                    Interlocked.Increment(ref callbacks);
                },
                CancellationToken.None));

        Assert.Equal(0, fallbackCreated);
        Assert.Equal(1, callbacks);
        Assert.Equal(ArticleBodyResult.NotRetrieved, callbackResult);
        Assert.Equal(0, retiredBreaker.GetSnapshot().FailureCount);
    }

    [Fact]
    public async Task PipelinedBody_AlreadyDisposedPool_ThrowsRetiredGenerationException()
    {
        var breaker = new ProviderCircuitBreaker("retired-pipeline");
        var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            _ => ValueTask.FromResult<INntpClient>(new HangingNntpClient()));
        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, "retired-pipeline");
        await pool.DisposeAsync();

        async Task EnumerateAsync()
        {
            await foreach (var _ in client.DecodedBodiesPipelinedAsync(
                               ["seg"], depth: 1, CancellationToken.None))
            {
                // Drain the pipeline; touching the retired pool throws mid-enumeration.
            }
        }

        var exception = await Assert.ThrowsAsync<NntpClientRetiredException>(EnumerateAsync);
        Assert.IsAssignableFrom<ObjectDisposedException>(exception.InnerException);
        Assert.Equal(0, breaker.GetSnapshot().FailureCount);
    }

    [Fact]
    public async Task RunWithConnection_WithStreamingTimeout_FailsFastAndRetriesOnFreshConnection()
    {
        HangingNntpClient? hanging = null;
        var created = 0;
        using var pool = new ConnectionPool<INntpClient>(maxConnections: 2, _ =>
        {
            var n = Interlocked.Increment(ref created);
            if (n == 1)
            {
                hanging = new HangingNntpClient();
                return ValueTask.FromResult<INntpClient>(hanging);
            }

            return ValueTask.FromResult<INntpClient>(
                new HealthyNntpClient(new Dictionary<string, byte[]>
                {
                    ["seg"] = [1, 2, 3, 4],
                }));
        });

        using var client = new MultiConnectionNntpClient(
            pool,
            ProviderType.Pooled,
            new ProviderCircuitBreaker("streaming-timeout"),
            "streaming-timeout");

        using var cts = new CancellationTokenSource();
        using var timeoutScope = cts.Token.SetContext(new StreamingTimeoutContext
        {
            PerSegmentTimeout = TimeSpan.FromMilliseconds(200),
            MaxRetries = 1,
        });

        var outerCallbacks = 0;
        var sw = Stopwatch.StartNew();
        var response = await client.DecodedBodyAsync(
            "seg",
            (_, _) => Interlocked.Increment(ref outerCallbacks),
            cts.Token);
        sw.Stop();

        Assert.True(response.Success);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"Expected fast failover, took {sw.Elapsed}");
        Assert.NotNull(hanging);
        Assert.Equal(1, hanging!.BodyRequestCount);
        Assert.Equal(1, hanging.CallbackCount);
        Assert.True(hanging.Disposed);
        Assert.Equal(1, outerCallbacks);
        Assert.Equal(2, created);
    }

    [Fact]
    public async Task RunWithConnection_WithoutStreamingTimeout_DoesNotCancelAfter()
    {
        var hanging = new HangingNntpClient();
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1, _ => ValueTask.FromResult<INntpClient>(hanging));

        using var client = new MultiConnectionNntpClient(
            pool,
            ProviderType.Pooled,
            new ProviderCircuitBreaker("no-streaming-timeout"),
            "no-streaming-timeout");

        using var cts = new CancellationTokenSource();
        var bodyTask = client.DecodedBodyAsync("seg", onConnectionReadyAgain: null, cts.Token);

        // WaitAsync abandons the await without cancelling the caller's token.
        // If CancelAfter had been applied, the hang would observe cancellation.
        await Assert.ThrowsAsync<TimeoutException>(() =>
            bodyTask.WaitAsync(TimeSpan.FromMilliseconds(300)));

        Assert.Equal(1, hanging.BodyRequestCount);
        Assert.False(hanging.SawCancellation);
        Assert.Equal(0, hanging.CallbackCount);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bodyTask);
        Assert.True(hanging.SawCancellation);
        Assert.Equal(1, hanging.CallbackCount);
    }

    [Fact]
    public async Task RunWithConnection_StreamingTimeoutExhausted_ThrowsTimeoutException()
    {
        var created = 0;
        using var pool = new ConnectionPool<INntpClient>(maxConnections: 2, _ =>
        {
            Interlocked.Increment(ref created);
            return ValueTask.FromResult<INntpClient>(new HangingNntpClient());
        });

        using var client = new MultiConnectionNntpClient(
            pool,
            ProviderType.Pooled,
            new ProviderCircuitBreaker("streaming-timeout-exhausted"),
            "streaming-timeout-exhausted");

        using var cts = new CancellationTokenSource();
        using var timeoutScope = cts.Token.SetContext(new StreamingTimeoutContext
        {
            PerSegmentTimeout = TimeSpan.FromMilliseconds(100),
            MaxRetries = 1,
        });

        var outerCallbacks = 0;
        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            client.DecodedBodyAsync("seg", (_, _) => Interlocked.Increment(ref outerCallbacks), cts.Token));
        sw.Stop();

        Assert.Contains("2 attempts", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(1, outerCallbacks);
        Assert.Equal(2, created);
    }

    [Fact]
    public async Task RunWithConnection_StreamingTimeoutExhausted_RecordsBreakerFailure()
    {
        var breaker = new ProviderCircuitBreaker("streaming-timeout-breaker");
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 2,
            _ => ValueTask.FromResult<INntpClient>(new HangingNntpClient()));

        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, "streaming-timeout-breaker");

        using var cts = new CancellationTokenSource();
        using var timeoutScope = cts.Token.SetContext(new StreamingTimeoutContext
        {
            PerSegmentTimeout = TimeSpan.FromMilliseconds(50),
            MaxRetries = 1,
        });

        // Three exhausted segments → consecutive-failure trip threshold (3).
        for (var i = 0; i < 3; i++)
        {
            Assert.False(breaker.IsTripped);
            await Assert.ThrowsAsync<TimeoutException>(() =>
                client.DecodedBodyAsync($"seg-{i}", onConnectionReadyAgain: null, cts.Token));
        }

        Assert.True(breaker.IsTripped);
        Assert.True(breaker.TrippedUntilMs > 0);
    }

    [Fact]
    public async Task RunWithConnection_StatCommandFailure_DoesNotTripBreaker()
    {
        var breaker = new ProviderCircuitBreaker("stat-failure");
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 2,
            _ => ValueTask.FromResult<INntpClient>(new HangingNntpClient()));

        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, "stat-failure");

        // HangingNntpClient throws from StatAsync. The loop runs well past the
        // trip threshold that body commands are held to.
        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                client.StatAsync($"seg-{i}", CancellationToken.None));
        }

        Assert.False(breaker.IsTripped);
        Assert.Equal(0, breaker.TrippedUntilMs);
    }

    [Fact]
    public async Task RunWithConnection_StatSuccess_ClosesBreakerOnlyAfterCooldown()
    {
        var breaker = new ProviderCircuitBreaker("stat-success-latched");
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 2,
            _ => ValueTask.FromResult<INntpClient>(
                new FakeNntpClient(new Dictionary<string, byte[]> { ["seg"] = [1, 2, 3] })));

        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, "stat-success-latched");

        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordFailure();
        Assert.True(breaker.IsLatched);

        var rejected = await Assert.ThrowsAnyAsync<RetryableDownloadException>(
            () => client.StatAsync("seg", CancellationToken.None));
        Assert.Contains("circuit", rejected.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(breaker.IsLatched);
        Assert.True(breaker.TrippedUntilMs > Environment.TickCount64);

        breaker.ExpireCooldownForTests();
        await client.StatAsync("seg", CancellationToken.None);

        Assert.False(breaker.IsLatched);
        Assert.Equal(0, breaker.TrippedUntilMs);
    }

    [Fact]
    public async Task RunWithConnection_StatSuccess_DoesNotClearFailureStreakWhileClosed()
    {
        var breaker = new ProviderCircuitBreaker("stat-success-closed");
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 2,
            _ => ValueTask.FromResult<INntpClient>(
                new FakeNntpClient(new Dictionary<string, byte[]> { ["seg"] = [1, 2, 3] })));

        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, "stat-success-closed");

        // Two failures leave the breaker closed and one short of tripping.
        breaker.RecordFailure();
        breaker.RecordFailure();
        Assert.False(breaker.IsLatched);

        await client.StatAsync("seg", CancellationToken.None);

        // The streak has to survive the stat, so the next failure still trips.
        breaker.RecordFailure();
        Assert.True(breaker.IsTripped);
    }

    [Fact]
    public async Task RunWithConnection_StreamingTimeoutThenSuccess_DoesNotTripBreaker()
    {
        var breaker = new ProviderCircuitBreaker("streaming-timeout-recover");
        var created = 0;
        using var pool = new ConnectionPool<INntpClient>(maxConnections: 2, _ =>
        {
            var n = Interlocked.Increment(ref created);
            if (n == 1)
                return ValueTask.FromResult<INntpClient>(new HangingNntpClient());
            return ValueTask.FromResult<INntpClient>(
                new HealthyNntpClient(new Dictionary<string, byte[]> { ["seg"] = [1, 2, 3] }));
        });

        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, "streaming-timeout-recover");

        using var cts = new CancellationTokenSource();
        using var timeoutScope = cts.Token.SetContext(new StreamingTimeoutContext
        {
            PerSegmentTimeout = TimeSpan.FromMilliseconds(50),
            MaxRetries = 1,
        });

        // Timeout then success on retry — exhaustion path never runs, so no
        // breaker failure is recorded for this segment.
        var response = await client.DecodedBodyAsync("seg", onConnectionReadyAgain: null, cts.Token);
        Assert.True(response.Success);
        Assert.False(breaker.IsTripped);
        Assert.Equal(0, breaker.TrippedUntilMs);
    }

    [Fact]
    public async Task DecodedBodiesAsync_StreamingTimeout_RetriesOnFreshConnection()
    {
        HangingPipelinedNntpClient? hanging = null;
        var created = 0;
        using var pool = new ConnectionPool<INntpClient>(maxConnections: 2, _ =>
        {
            if (Interlocked.Increment(ref created) == 1)
            {
                hanging = new HangingPipelinedNntpClient();
                return ValueTask.FromResult<INntpClient>(hanging);
            }

            return ValueTask.FromResult<INntpClient>(new HealthyPipelinedNntpClient());
        });
        var breaker = new ProviderCircuitBreaker("pipelined-timeout-retry");
        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, "pipelined-timeout-retry");
        using var cts = new CancellationTokenSource();
        using var timeoutScope = cts.Token.SetContext(new StreamingTimeoutContext
        {
            PerSegmentTimeout = TimeSpan.FromMilliseconds(50),
            MaxRetries = 1,
        });
        var callbacks = new List<ArticleBodyResult>();

        var batch = await client.DecodedBodiesAsync(
            [new SegmentId("one"), new SegmentId("two")],
            (result, _) => callbacks.Add(result),
            cts.Token);

        Assert.Equal(2, batch.Responses.Count);
        Assert.NotNull(hanging);
        Assert.True(hanging!.SawCancellation);
        Assert.True(hanging.Disposed);
        Assert.Equal(2, created);
        Assert.Equal([ArticleBodyResult.Retrieved], callbacks);
        Assert.Equal(0, breaker.GetSnapshot().FailureCount);
    }

    [Fact]
    public async Task DecodedBodiesAsync_StreamingTimeoutExhausted_ReportsNotRetrievedExactlyOnce()
    {
        var clients = new List<HangingPipelinedNntpClient>();
        using var pool = new ConnectionPool<INntpClient>(maxConnections: 2, _ =>
        {
            var connection = new HangingPipelinedNntpClient();
            clients.Add(connection);
            return ValueTask.FromResult<INntpClient>(connection);
        });
        var breaker = new ProviderCircuitBreaker("pipelined-timeout-exhausted");
        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, "pipelined-timeout-exhausted");
        using var cts = new CancellationTokenSource();
        using var timeoutScope = cts.Token.SetContext(new StreamingTimeoutContext
        {
            PerSegmentTimeout = TimeSpan.FromMilliseconds(50),
            MaxRetries = 1,
        });
        var callbacks = new List<ArticleBodyResult>();

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            client.DecodedBodiesAsync(
                [new SegmentId("one"), new SegmentId("two")],
                (result, _) => callbacks.Add(result),
                cts.Token));

        Assert.Contains("2 attempts", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, clients.Count);
        Assert.All(clients, connection =>
        {
            Assert.True(connection.SawCancellation);
            Assert.True(connection.Disposed);
        });
        Assert.Equal([ArticleBodyResult.NotRetrieved], callbacks);
        Assert.Equal(1, breaker.GetSnapshot().FailureCount);
    }

    [Fact]
    public async Task DecodedBodiesAsync_CallerCancellation_DoesNotRetryOrRecordBreakerFailure()
    {
        var hanging = new HangingPipelinedNntpClient();
        var created = 0;
        using var pool = new ConnectionPool<INntpClient>(maxConnections: 2, _ =>
        {
            Interlocked.Increment(ref created);
            return ValueTask.FromResult<INntpClient>(hanging);
        });
        var breaker = new ProviderCircuitBreaker("pipelined-caller-cancel");
        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, "pipelined-caller-cancel");
        using var cts = new CancellationTokenSource();
        using var timeoutScope = cts.Token.SetContext(new StreamingTimeoutContext
        {
            PerSegmentTimeout = TimeSpan.FromSeconds(5),
            MaxRetries = 3,
        });
        var callbacks = new List<ArticleBodyResult>();
        var batchTask = client.DecodedBodiesAsync(
            [new SegmentId("one")],
            (result, _) => callbacks.Add(result),
            cts.Token);

        await hanging.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batchTask);
        Assert.Equal(1, created);
        Assert.Equal([ArticleBodyResult.NotRetrieved], callbacks);
        Assert.Equal(0, breaker.GetSnapshot().FailureCount);
    }

    [Fact]
    public async Task DecodedBodiesAsync_TimeoutThenSuccess_DoesNotTripBreaker()
    {
        var created = 0;
        using var pool = new ConnectionPool<INntpClient>(maxConnections: 2, _ =>
            ValueTask.FromResult<INntpClient>(
                Interlocked.Increment(ref created) == 1
                    ? new HangingPipelinedNntpClient()
                    : new HealthyPipelinedNntpClient()));
        var breaker = new ProviderCircuitBreaker("pipelined-timeout-recovery");
        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, "pipelined-timeout-recovery");
        using var cts = new CancellationTokenSource();
        using var timeoutScope = cts.Token.SetContext(new StreamingTimeoutContext
        {
            PerSegmentTimeout = TimeSpan.FromMilliseconds(50),
            MaxRetries = 1,
        });

        await client.DecodedBodiesAsync(
            [new SegmentId("one")],
            onConnectionReadyAgain: null,
            cts.Token);

        Assert.False(breaker.IsTripped);
        Assert.Equal(0, breaker.GetSnapshot().FailureCount);
    }

    [Fact]
    public async Task DownloadSemaphoreWait_CancelsWithinStreamingReadDeadline()
    {
        // Mirrors WebDAV linking RequestAborted + CancelAfter(streaming-read-timeout)
        // into AcquireExclusiveConnectionAsync's WaitAsync — a held permit must not hang forever.
        using var semaphore = new PrioritizedSemaphore(initialAllowed: 1, maxAllowed: 1);
        await semaphore.WaitAsync(SemaphorePriority.High);

        using var readCts = new CancellationTokenSource();
        readCts.CancelAfter(TimeSpan.FromMilliseconds(200));
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => semaphore.WaitAsync(SemaphorePriority.High, readCts.Token));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"Expected deadline cancel within ~200ms, took {sw.Elapsed}");
        // Holding the original permit — release must still succeed (no leak from cancelled waiter).
        semaphore.Release();
        await semaphore.WaitAsync(SemaphorePriority.High).WaitAsync(TimeSpan.FromSeconds(1));
        semaphore.Release();
    }

    [Fact]
    public async Task MultiProvider_StreamingTimeout_FailsOverToBackup()
    {
        var primaryCreated = 0;
        var backupCreated = 0;
        var primaryPool = new ConnectionPool<INntpClient>(
            maxConnections: 2,
            _ =>
            {
                Interlocked.Increment(ref primaryCreated);
                return ValueTask.FromResult<INntpClient>(new HangingNntpClient());
            });
        var backupPool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            _ =>
            {
                Interlocked.Increment(ref backupCreated);
                return ValueTask.FromResult<INntpClient>(
                    new HealthyNntpClient(new Dictionary<string, byte[]>
                    {
                        ["seg"] = [1, 2, 3, 4],
                    }));
            });
        var primary = new MultiConnectionNntpClient(
            primaryPool,
            ProviderType.Pooled,
            new ProviderCircuitBreaker("news.primary.example"),
            "news.primary.example",
            priority: 0);
        var backup = new MultiConnectionNntpClient(
            backupPool,
            ProviderType.BackupOnly,
            new ProviderCircuitBreaker("news.backup.example"),
            "news.backup.example",
            priority: 1);
        using var client = new MultiProviderNntpClient([primary, backup]);

        using var cts = new CancellationTokenSource();
        using var timeoutScope = cts.Token.SetContext(new StreamingTimeoutContext
        {
            PerSegmentTimeout = TimeSpan.FromMilliseconds(50),
            MaxRetries = 1,
        });

        var response = await client.DecodedBodyAsync("seg", onConnectionReadyAgain: null, cts.Token);

        Assert.Equal(UsenetResponseType.ArticleRetrievedBodyFollows, response.ResponseType);
        Assert.Equal(2, primaryCreated);
        Assert.Equal(1, backupCreated);
        if (response.Stream != null)
            await response.Stream.DisposeAsync();
    }

    [Fact]
    public async Task MultiProvider_PipelinedStreamingTimeout_FailsOverToBackup()
    {
        var primaryCreated = 0;
        var backupCreated = 0;
        var primaryPool = new ConnectionPool<INntpClient>(
            maxConnections: 2,
            _ =>
            {
                Interlocked.Increment(ref primaryCreated);
                return ValueTask.FromResult<INntpClient>(new HangingPipelinedNntpClient());
            });
        var backupPool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            _ =>
            {
                Interlocked.Increment(ref backupCreated);
                return ValueTask.FromResult<INntpClient>(new HealthyPipelinedNntpClient());
            });
        var primary = new MultiConnectionNntpClient(
            primaryPool,
            ProviderType.Pooled,
            new ProviderCircuitBreaker("news.primary.example"),
            "news.primary.example",
            priority: 0);
        var backup = new MultiConnectionNntpClient(
            backupPool,
            ProviderType.BackupOnly,
            new ProviderCircuitBreaker("news.backup.example"),
            "news.backup.example",
            priority: 1);
        using var client = new MultiProviderNntpClient([primary, backup]);

        using var cts = new CancellationTokenSource();
        using var timeoutScope = cts.Token.SetContext(new StreamingTimeoutContext
        {
            PerSegmentTimeout = TimeSpan.FromMilliseconds(50),
            MaxRetries = 1,
        });

        var batch = await client.DecodedBodiesAsync(
            [new SegmentId("one")], onConnectionReadyAgain: null, cts.Token);
        Assert.Equal(
            UsenetResponseType.ArticleRetrievedBodyFollows,
            (await batch.Responses[0]).ResponseType);
        Assert.Equal(2, primaryCreated);
        Assert.Equal(1, backupCreated);
    }

    [Fact]
    public async Task RunWithConnection_StreamingTimeoutExhausted_WarningIncludesProvider()
    {
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Warning()
            .WriteTo.Sink(sink)
            .CreateLogger();

        try
        {
            using var pool = new ConnectionPool<INntpClient>(
                maxConnections: 2,
                _ => ValueTask.FromResult<INntpClient>(new HangingNntpClient()));
            using var client = new MultiConnectionNntpClient(
                pool,
                ProviderType.Pooled,
                new ProviderCircuitBreaker("news.verycheapprovider.com"),
                "news.verycheapprovider.com");

            using var cts = new CancellationTokenSource();
            using var timeoutScope = cts.Token.SetContext(new StreamingTimeoutContext
            {
                PerSegmentTimeout = TimeSpan.FromMilliseconds(50),
                MaxRetries = 0,
            });

            await Assert.ThrowsAsync<TimeoutException>(() =>
                client.DecodedBodyAsync("seg", onConnectionReadyAgain: null, cts.Token));
        }
        finally
        {
            Log.Logger = previous;
        }

        Assert.Contains(sink.Events, e =>
            e.Level == LogEventLevel.Warning
            && e.RenderMessage().Contains("news.verycheapprovider.com", StringComparison.Ordinal)
            && e.RenderMessage().Contains("No retries left", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ConnectionPool_LateFactoryFailure_OnlyCancellationOmitsStack(
        bool deadlineExpires,
        bool factoryCancelled)
    {
        var sink = new CollectingSink();
        var previous = Log.Logger;
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Warning()
            .WriteTo.Sink(sink)
            .CreateLogger();
        Log.Logger = logger;

        try
        {
            var lateFactory = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = 0;
            var openTimeout = deadlineExpires ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(5);
            await using var pool = new ConnectionPool<object>(
                maxConnections: 1,
                _ => Interlocked.Increment(ref attempts) == 1
                    ? new ValueTask<object>(lateFactory.Task)
                    : ValueTask.FromResult(new object()),
                diagnosticName: "news.late-factory.example",
                connectionOpenTimeout: () => openTimeout);
            using var caller = new CancellationTokenSource();
            var borrow = pool.GetConnectionLockAsync(SemaphorePriority.High, caller.Token);
            Assert.Equal(1, pool.PendingConnectionCreations);

            if (deadlineExpires)
                await Assert.ThrowsAsync<ConnectionOpenTimeoutException>(() => borrow.WaitAsync(TimeSpan.FromSeconds(5)));
            else
            {
                await caller.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => borrow.WaitAsync(TimeSpan.FromSeconds(5)));
            }

            openTimeout = TimeSpan.FromSeconds(5);
            Exception failure = factoryCancelled
                ? new OperationCanceledException("Connection opening was cancelled.")
                : new InvalidOperationException("Unexpected factory failure.");
            lateFactory.SetException(failure);

            using var recovered = await pool.GetConnectionLockAsync(SemaphorePriority.High)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, pool.PendingConnectionCreations);
            Assert.Equal(1, pool.LiveConnections);
            Assert.Equal(2, attempts);

            var warning = Assert.Single(sink.Events, eventItem =>
                eventItem.Level == LogEventLevel.Warning
                && eventItem.Properties.TryGetValue("Provider", out var provider)
                && provider is ScalarValue { Value: "news.late-factory.example" });
            Assert.Equal(LogEventLevel.Warning, warning.Level);
            Assert.Equal("news.late-factory.example", Assert.IsType<ScalarValue>(warning.Properties["Provider"]).Value);
            if (factoryCancelled)
            {
                Assert.Null(warning.Exception);
                Assert.Equal(failure.Message, Assert.IsType<ScalarValue>(warning.Properties["Reason"]).Value);
                Assert.Contains("open attempt was cancelled", warning.RenderMessage(), StringComparison.Ordinal);
            }
            else
                Assert.Same(failure, warning.Exception);
        }
        finally
        {
            Log.Logger = previous;
        }
    }

    [Fact]
    public async Task ConnectionPool_HandshakeQueue_DoesNotConsumeOpenBudget()
    {
        var safetyTimeout = TimeSpan.FromSeconds(5);
        var budget = TimeSpan.FromSeconds(30);
        var started = 0;
        var firstThreeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactories = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = new ConnectionPool<object>(
            maxConnections: 4,
            connectionFactory: async cancellationToken =>
            {
                var attempt = Interlocked.Increment(ref started);
                if (attempt <= 3)
                {
                    if (attempt == 3)
                        firstThreeStarted.TrySetResult();
                    await releaseFactories.Task.WaitAsync(cancellationToken);
                }
                return new object();
            },
            connectionOpenTimeout: () => budget);
        var borrowers = new List<Task<ConnectionLock<object>>>();

        try
        {
            for (var index = 0; index < 3; index++)
                borrowers.Add(pool.GetConnectionLockAsync(SemaphorePriority.High));
            await firstThreeStarted.Task.WaitAsync(safetyTimeout);

            budget = TimeSpan.FromMilliseconds(250);
            var queued = pool.GetConnectionLockAsync(SemaphorePriority.High);
            borrowers.Add(queued);
            var observation = Task.Delay(TimeSpan.FromSeconds(1));
            Assert.Same(observation, await Task.WhenAny(queued, observation));
            Assert.False(queued.IsCompleted);
            Assert.Equal(3, Volatile.Read(ref started));
            Assert.Equal(3, pool.PendingConnectionCreations);

            releaseFactories.TrySetResult();
            var acquired = await Task.WhenAll(borrowers).WaitAsync(safetyTimeout);
            Assert.All(acquired, connection => Assert.False(connection.WasReused));
            Assert.Equal(4, Volatile.Read(ref started));
            Assert.Equal(4, pool.LiveConnections);
            Assert.Equal(0, pool.PendingConnectionCreations);
            Assert.Equal(0, pool.GetChurn().HandshakeFailures);
        }
        finally
        {
            releaseFactories.TrySetResult();
            foreach (var borrower in borrowers)
            {
                try
                {
                    using var connection = await borrower.WaitAsync(safetyTimeout);
                }
                catch (ConnectionOpenTimeoutException)
                {
                }
            }
        }
    }

    [Fact]
    public async Task ConnectionPool_CreationCapacity_DoesNotConsumeOpenBudget()
    {
        var safetyTimeout = TimeSpan.FromSeconds(5);
        var lateFactory = new TaskCompletionSource<INntpClient>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lateConnection = new HangingNntpClient();
        var attempts = 0;
        await using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            connectionFactory: _ => Interlocked.Increment(ref attempts) == 1
                ? new ValueTask<INntpClient>(lateFactory.Task)
                : ValueTask.FromResult<INntpClient>(
                    new FakeNntpClient(new Dictionary<string, byte[]>())),
            connectionOpenTimeout: () => TimeSpan.FromMilliseconds(250));
        Task<ConnectionLock<INntpClient>>? queued = null;

        try
        {
            var first = pool.GetConnectionLockAsync(SemaphorePriority.High);
            var timeout = await Assert.ThrowsAsync<ConnectionOpenTimeoutException>(
                () => first.WaitAsync(safetyTimeout));
            Assert.True(timeout.FactoryStarted);
            Assert.Equal("Factory", timeout.Phase);
            Assert.Equal(1, pool.PendingConnectionCreations);
            Assert.Equal(0, pool.LiveConnections);

            queued = pool.GetConnectionLockAsync(SemaphorePriority.High);
            var observation = Task.Delay(TimeSpan.FromSeconds(1));
            Assert.Same(observation, await Task.WhenAny(queued, observation));
            Assert.False(queued.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref attempts));
            Assert.Equal(1, pool.PendingConnectionCreations);

            lateFactory.TrySetResult(lateConnection);
            using var recovered = await queued.WaitAsync(safetyTimeout);
            Assert.True(lateConnection.Disposed);
            Assert.Equal(2, Volatile.Read(ref attempts));
            Assert.Equal(0, pool.PendingConnectionCreations);
            Assert.Equal(1, pool.LiveConnections);
            Assert.False(recovered.WasReused);
        }
        finally
        {
            lateFactory.TrySetResult(lateConnection);
            if (queued is not null)
            {
                try
                {
                    using var connection = await queued.WaitAsync(safetyTimeout);
                }
                catch (ConnectionOpenTimeoutException)
                {
                }
            }
        }
    }

    [Fact]
    public async Task ConnectionPool_QueuedCallerCancellation_DoesNotStartFactory()
    {
        var safetyTimeout = TimeSpan.FromSeconds(5);
        var started = 0;
        var firstThreeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactories = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = new ConnectionPool<object>(
            maxConnections: 4,
            connectionFactory: async cancellationToken =>
            {
                var attempt = Interlocked.Increment(ref started);
                if (attempt <= 3)
                {
                    if (attempt == 3)
                        firstThreeStarted.TrySetResult();
                    await releaseFactories.Task.WaitAsync(cancellationToken);
                }
                return new object();
            },
            connectionOpenTimeout: () => TimeSpan.FromMilliseconds(250));
        var holders = new List<Task<ConnectionLock<object>>>();
        using var caller = new CancellationTokenSource();
        Task<ConnectionLock<object>>? queued = null;

        try
        {
            for (var index = 0; index < 3; index++)
                holders.Add(pool.GetConnectionLockAsync(SemaphorePriority.High));
            await firstThreeStarted.Task.WaitAsync(safetyTimeout);

            queued = pool.GetConnectionLockAsync(SemaphorePriority.High, caller.Token);
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => queued.WaitAsync(safetyTimeout));
            Assert.Equal(3, Volatile.Read(ref started));
            Assert.Equal(3, pool.PendingConnectionCreations);

            releaseFactories.TrySetResult();
            var acquired = await Task.WhenAll(holders).WaitAsync(safetyTimeout);
            var fourth = pool.GetConnectionLockAsync(SemaphorePriority.High);
            using var recovered = await fourth.WaitAsync(safetyTimeout);
            Assert.Equal(4, Volatile.Read(ref started));
            Assert.Equal(0, pool.PendingConnectionCreations);
            foreach (var holder in acquired)
                holder.Dispose();
        }
        finally
        {
            releaseFactories.TrySetResult();
            foreach (var holder in holders)
            {
                try
                {
                    using var connection = await holder.WaitAsync(safetyTimeout);
                }
                catch (OperationCanceledException) when (caller.IsCancellationRequested)
                {
                }
            }
            if (queued is not null)
            {
                try
                {
                    using var connection = await queued.WaitAsync(safetyTimeout);
                }
                catch (OperationCanceledException) when (caller.IsCancellationRequested)
                {
                }
            }
        }
    }

    [Theory]
    [InlineData("body", false, false)]
    [InlineData("body", true, false)]
    [InlineData("body", false, true)]
    [InlineData("body", true, true)]
    [InlineData("batch", false, false)]
    [InlineData("batch", true, false)]
    [InlineData("batch", false, true)]
    [InlineData("batch", true, true)]
    [InlineData("pipeline", false, false)]
    [InlineData("pipeline", true, false)]
    [InlineData("pipeline", false, true)]
    [InlineData("pipeline", true, true)]
    public async Task ConnectionAcquisition_PreFactoryTimeout_DoesNotRecordFailureOrStrandProbe(
        string operation,
        bool retainLiveConnection,
        bool halfOpen)
    {
        const string provider = "news.admission.example";
        var breaker = new ProviderCircuitBreaker(provider) { Clock = () => 100_000L };
        if (halfOpen)
        {
            breaker.RecordConnectionFailure("initial", requiresFreshConnectionProbe: true);
            breaker.ExpireCooldownForTests();
        }

        var failure = new ConnectionOpenTimeoutException(
            provider, "HandshakeQueue", TimeSpan.FromSeconds(3), factoryStarted: false);
        var attempts = 0;
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 2,
            connectionFactory: _ =>
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (retainLiveConnection && attempt == 1)
                    return ValueTask.FromResult<INntpClient>(
                        new FakeNntpClient(new Dictionary<string, byte[]>()));
                throw failure;
            });
        using var retained = retainLiveConnection
            ? await pool.GetConnectionLockAsync(SemaphorePriority.High)
            : null;
        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, provider);
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var timeoutScope = caller.Token.SetContext(new StreamingTimeoutContext
        {
            PerSegmentTimeout = TimeSpan.FromSeconds(5),
            MaxRetries = 0,
        });
        var recorder = new ArticleBodyCompletionRecorder();
        var before = breaker.GetSnapshot();
        var cooldown = breaker.CurrentCooldown;
        var freshProbeRequired = breaker.RequiresFreshConnectionProbe;

        var actual = await Assert.ThrowsAsync<ConnectionOpenTimeoutException>(ExecuteAsync);

        Assert.Same(failure, actual);
        Assert.False(caller.IsCancellationRequested);
        var after = breaker.GetSnapshot();
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.FailureCount, after.FailureCount);
        Assert.Equal(before.TripCount, after.TripCount);
        Assert.Equal(before.ArticleMissCount, after.ArticleMissCount);
        Assert.Equal(before.LastFailureReason, after.LastFailureReason);
        Assert.Equal(cooldown, breaker.CurrentCooldown);
        Assert.Equal(freshProbeRequired, breaker.RequiresFreshConnectionProbe);
        Assert.Equal(retainLiveConnection ? 2 : 1, Volatile.Read(ref attempts));
        Assert.Equal(0, pool.PendingConnectionCreations);
        Assert.Equal(retainLiveConnection ? 1 : 0, pool.ActiveConnections);
        Assert.Equal(operation == "pipeline" ? 0 : 1, recorder.Count);
        if (operation != "pipeline")
            Assert.Equal(ArticleBodyResult.NotRetrieved, recorder.Result);

        Assert.True(breaker.TryAdmit(out var nextProbe));
        try
        {
            Assert.Equal(!halfOpen, nextProbe.IsNone);
        }
        finally
        {
            breaker.ReleaseProbe(nextProbe);
        }

        async Task ExecuteAsync()
        {
            switch (operation)
            {
                case "body":
                    await client.DecodedBodyAsync("synthetic-segment", recorder.Invoke, caller.Token);
                    break;
                case "batch":
                    await client.DecodedBodiesAsync(["synthetic-segment"], recorder.Invoke, caller.Token);
                    break;
                case "pipeline":
                    await using (var enumerator = client.DecodedBodiesPipelinedAsync(
                        ["synthetic-segment"], depth: 1, caller.Token).GetAsyncEnumerator(caller.Token))
                    {
                        await enumerator.MoveNextAsync();
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }
    }

    [Fact]
    public async Task RunWithConnection_FactoryOpenTimeout_StillTripsAndLogsWithoutStack()
    {
        const string provider = "news.factory.example";
        var sink = new CollectingSink();
        var previous = Log.Logger;
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Warning()
            .WriteTo.Sink(sink)
            .CreateLogger();
        var factory = new TaskCompletionSource<INntpClient>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            connectionFactory: _ => Interlocked.Increment(ref attempts) == 1
                ? new ValueTask<INntpClient>(factory.Task)
                : ValueTask.FromResult<INntpClient>(
                    new FakeNntpClient(new Dictionary<string, byte[]>())),
            diagnosticName: provider,
            connectionOpenProvider: provider,
            connectionOpenTimeout: () => TimeSpan.FromMilliseconds(250));
        var breaker = new ProviderCircuitBreaker(provider);
        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, provider);
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var timeoutScope = caller.Token.SetContext(new StreamingTimeoutContext
        {
            PerSegmentTimeout = TimeSpan.FromSeconds(5),
            MaxRetries = 0,
        });
        var recorder = new ArticleBodyCompletionRecorder();
        Log.Logger = logger;

        try
        {
            var timeout = await Assert.ThrowsAsync<ConnectionOpenTimeoutException>(() =>
                client.DecodedBodyAsync("synthetic-segment", recorder.Invoke, caller.Token));
            Assert.True(timeout.FactoryStarted);
            Assert.Equal("Factory", timeout.Phase);
            Assert.False(caller.IsCancellationRequested);
            Assert.Equal(ProviderCircuitState.Open, breaker.GetSnapshot().State);
            Assert.Equal(1, breaker.GetSnapshot().FailureCount);
            Assert.Equal(1, breaker.GetSnapshot().TripCount);
            Assert.True(breaker.RequiresFreshConnectionProbe);
            Assert.Equal(1, recorder.Count);
            Assert.Equal(ArticleBodyResult.NotRetrieved, recorder.Result);

            var warning = Assert.Single(sink.Events, logEvent =>
                logEvent.Level == LogEventLevel.Warning
                && logEvent.MessageTemplate.Text.StartsWith(
                    "Error getting connection-lock", StringComparison.Ordinal));
            Assert.Null(warning.Exception);
            Assert.Equal(provider, Assert.IsType<ScalarValue>(warning.Properties["Provider"]).Value);
            Assert.Equal(timeout.Message, Assert.IsType<ScalarValue>(warning.Properties["Reason"]).Value);
        }
        finally
        {
            factory.TrySetCanceled();
            try
            {
                using var recovered = await pool.GetConnectionLockAsync(SemaphorePriority.High)
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                Log.Logger = previous;
            }
        }
    }

    [Fact]
    public async Task ConnectionPoolGate_CancelsWithinStreamingReadDeadline()
    {
        var created = 0;
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1,
            _ =>
            {
                Interlocked.Increment(ref created);
                return ValueTask.FromResult<INntpClient>(new HangingNntpClient());
            });

        using var held = await pool.GetConnectionLockAsync(SemaphorePriority.High, CancellationToken.None);

        using var readCts = new CancellationTokenSource();
        readCts.CancelAfter(TimeSpan.FromMilliseconds(200));
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pool.GetConnectionLockAsync(SemaphorePriority.High, readCts.Token));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"Expected pool-gate cancel within ~200ms, took {sw.Elapsed}");
        Assert.Equal(1, created);
    }

    /// <summary>
    /// BODY that hangs until cancelled, firing NotRetrieved exactly once
    /// (in-flight cancel → connection not reusable).
    /// </summary>
    private sealed class HangingNntpClient : NntpClient
    {
        private int _callbackCount;

        public int BodyRequestCount { get; private set; }
        public int CallbackCount => Volatile.Read(ref _callbackCount);
        public bool SawCancellation { get; private set; }
        public bool Disposed { get; private set; }

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedBodyAsync(segmentId, null, cancellationToken);

        public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            BodyRequestCount++;
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("Hang was expected to be cancelled.");
            }
            catch (OperationCanceledException)
            {
                SawCancellation = true;
                // Mid-command cancel leaves the socket unclean → NotRetrieved (replace).
                Interlocked.Increment(ref _callbackCount);
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
                throw;
            }
        }

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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

        public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
            string segmentId, CancellationToken cancellationToken) =>
            Task.FromResult(new UsenetExclusiveConnection(null));

        public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
            IReadOnlyList<SegmentId> segmentIds, CancellationToken cancellationToken) =>
            Task.FromResult(new UsenetExclusiveConnection(null));

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken) =>
            DecodedBodyAsync(segmentId, exclusiveConnection.OnConnectionReadyAgain, cancellationToken);

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
            Disposed = true;
        }
    }

    private class HealthyNntpClient(IReadOnlyDictionary<string, byte[]> segments) : NntpClient
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
            DecodedBodyAsync(segmentId, null, cancellationToken);

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = segmentId.ToString();
            if (!segments.TryGetValue(key, out var bytes))
                throw new InvalidOperationException($"Missing segment {key}");

            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(new UsenetDecodedBodyResponse
            {
                SegmentId = key,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "222 ok",
                Stream = new YencStream(new MemoryStream(EncodeYenc(bytes), writable: false)),
            });
        }

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

        public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
            string segmentId, CancellationToken cancellationToken) =>
            Task.FromResult(new UsenetExclusiveConnection(null));

        public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
            IReadOnlyList<SegmentId> segmentIds, CancellationToken cancellationToken) =>
            Task.FromResult(new UsenetExclusiveConnection(null));

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken) =>
            DecodedBodyAsync(segmentId, exclusiveConnection.OnConnectionReadyAgain, cancellationToken);

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }

        private static byte[] EncodeYenc(ReadOnlySpan<byte> source)
        {
            using var output = new MemoryStream(source.Length + 128);
            output.Write(Encoding.ASCII.GetBytes(
                $"=ybegin line=128 size={source.Length} name=fake.bin\r\n"));
            foreach (var value in source)
                output.WriteByte(unchecked((byte)(value + 42)));
            output.Write(Encoding.ASCII.GetBytes("\r\n"));
            output.Write(Encoding.ASCII.GetBytes($"=yend size={source.Length}\r\n"));
            return output.ToArray();
        }
    }

    private sealed class LateBodyCompletionClient()
        : HealthyNntpClient(new Dictionary<string, byte[]> { ["seg"] = [1, 2, 3] })
    {
        private ArticleBodyCompletionHandler? _onCompleted;
        public CancellationToken BodyToken { get; private set; }

        public void Complete(ArticleBodyResult result) => _onCompleted?.Invoke(result);

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            BodyToken = cancellationToken;
            _onCompleted = onConnectionReadyAgain;
            return base.DecodedBodyAsync(segmentId, null, cancellationToken);
        }
    }

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get
            {
                lock (_events) return _events.ToArray();
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events) _events.Add(logEvent);
        }
    }

    private sealed class HangingPipelinedNntpClient()
        : HealthyNntpClient(new Dictionary<string, byte[]>())
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SawCancellation { get; private set; }
        public bool Disposed { get; private set; }

        public override async Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("Hang was expected to be cancelled.");
            }
            catch (OperationCanceledException)
            {
                SawCancellation = true;
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
                throw;
            }
        }

        public override void Dispose() => Disposed = true;
    }

    private sealed class HealthyPipelinedNntpClient()
        : HealthyNntpClient(new Dictionary<string, byte[]>())
    {
        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var responses = segmentIds.Select(segmentId =>
                Task.FromResult(new UsenetDecodedBodyResponse
                {
                    SegmentId = segmentId.ToString(),
                    ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                    ResponseMessage = "222 ok",
                    Stream = new CachedYencStream(
                        new UsenetYencHeader
                        {
                            FileName = "ok.bin",
                            FileSize = 1,
                            LineLength = 128,
                            PartNumber = 1,
                            TotalParts = 1,
                            PartOffset = 0,
                            PartSize = 1,
                        },
                        new MemoryStream([1], writable: false)),
                })).ToArray();
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(new UsenetDecodedBodyBatch { Responses = responses });
        }
    }
}
