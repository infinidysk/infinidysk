using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Tests.TestUtils;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public sealed class MultiConnectionBatchLifecycleTests
{
    [Fact]
    public async Task CallerCancellationAfterBatchReturn_ReachesTransport()
    {
        var inner = new ControlledDecodedBodyBatchClient(
            callbackTiming: ControlledDecodedBodyBatchClient.CallbackTiming.AfterReturn);
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1, _ => ValueTask.FromResult<INntpClient>(inner));
        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, new ProviderCircuitBreaker("batch-cancel"), "batch-cancel");
        using var callerCts = new CancellationTokenSource();
        using var timeoutScope = callerCts.Token.SetContext(new StreamingTimeoutContext
        {
            PerSegmentTimeout = TimeSpan.FromMinutes(1),
            MaxRetries = 0,
        });

        var batch = await client.DecodedBodiesAsync(["a@test"], null, callerCts.Token);
        try
        {
            Assert.False(batch.Completion.IsCompleted);
            callerCts.Cancel();
            Assert.True(inner.LastCancellationToken.IsCancellationRequested);
        }
        finally
        {
            inner.FireCapturedCallback(ArticleBodyResult.Cancelled);
            inner.CompleteProducer();
            await batch.DrainAsync();
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public async Task CancelledBatches_ReleaseSaturatedTransferBudgetForQueue(int transferLimit)
    {
        var connections = Enumerable.Range(0, transferLimit)
            .Select(_ => new ControlledDecodedBodyBatchClient(
                callbackTiming: ControlledDecodedBodyBatchClient.CallbackTiming.AfterReturn))
            .ToArray();
        var created = 0;
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 15,
            _ => ValueTask.FromResult<INntpClient>(connections[Interlocked.Increment(ref created) - 1]));
        var breaker = new ProviderCircuitBreaker("saturated-batch-cancel");
        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, breaker, "saturated-batch-cancel",
            maxTransferConnections: transferLimit);
        using var callerCts = new CancellationTokenSource();
        using var timeoutScope = callerCts.Token.SetContext(new StreamingTimeoutContext
        {
            PerSegmentTimeout = TimeSpan.FromMinutes(1),
            MaxRetries = 0,
        });
        using var queueCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batches = new List<UsenetDecodedBodyBatch>();
        var registrations = new List<CancellationTokenRegistration>();
        var recorder = new ArticleBodyCompletionRecorder();
        try
        {
            foreach (var connection in connections)
            {
                batches.Add(await client.DecodedBodiesAsync(["a@test"], recorder.Invoke, callerCts.Token));
                registrations.Add(connection.LastCancellationToken.Register(() =>
                {
                    connection.FireCapturedCallback(ArticleBodyResult.Cancelled);
                    connection.CompleteProducer();
                }));
            }

            Assert.Equal(transferLimit, client.GetConnectionAdmissionSnapshot()!.ActiveTransferOperations);
            Assert.Equal(transferLimit, client.ActiveConnections);
            var queued = client.DecodedBodyAsync("queue@test", null, queueCts.Token);
            Assert.False(queued.IsCompleted);
            Assert.Equal(1, client.GetConnectionAdmissionSnapshot()!.WaitingTransferOperations);

            await callerCts.CancelAsync();
            var response = await queued.WaitAsync(TimeSpan.FromSeconds(5));
            using var stream = response.Stream;
            await Task.WhenAll(batches.Select(batch => batch.Completion)).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(response.Success);
            Assert.Equal(0, client.GetConnectionAdmissionSnapshot()!.ActiveTransferOperations);
            Assert.Equal(0, client.GetConnectionAdmissionSnapshot()!.WaitingTransferOperations);
            Assert.Equal(0, client.ActiveConnections);
            Assert.Equal(transferLimit, recorder.Count);
            Assert.Equal(ArticleBodyResult.Cancelled, recorder.Result);
            Assert.Equal(0, breaker.GetSnapshot().FailureCount);
        }
        finally
        {
            await queueCts.CancelAsync();
            foreach (var connection in connections)
            {
                connection.FireCapturedCallback(ArticleBodyResult.Cancelled);
                connection.CompleteProducer();
            }
            foreach (var registration in registrations)
                registration.Dispose();
            foreach (var batch in batches)
                await batch.DrainAsync();
        }
    }

    [Fact]
    public async Task ReturnedBatch_DisablesSetupDeadlineButRetainsCallerCancellation()
    {
        var inner = new ControlledDecodedBodyBatchClient(
            callbackTiming: ControlledDecodedBodyBatchClient.CallbackTiming.AfterReturn);
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1, _ => ValueTask.FromResult<INntpClient>(inner));
        using var client = new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, new ProviderCircuitBreaker("batch-deadline"), "batch-deadline");
        using var callerCts = new CancellationTokenSource();
        using var timeoutScope = callerCts.Token.SetContext(new StreamingTimeoutContext
        {
            PerSegmentTimeout = TimeSpan.FromSeconds(1),
            MaxRetries = 0,
        });

        var batch = await client.DecodedBodiesAsync(["a@test"], null, callerCts.Token);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1100));
            Assert.False(inner.LastCancellationToken.IsCancellationRequested);
            Assert.False(batch.Completion.IsCompleted);
            callerCts.Cancel();
            Assert.True(inner.LastCancellationToken.IsCancellationRequested);
        }
        finally
        {
            inner.FireCapturedCallback(ArticleBodyResult.Cancelled);
            inner.CompleteProducer();
            await batch.DrainAsync();
        }
    }

    [Fact]
    public async Task InnerCompletionOom_WaitsForCallbackBeforeReleasingConnection()
    {
        var oom = new OutOfMemoryException("inner-completion");
        var inner = new ControlledDecodedBodyBatchClient(
            callbackTiming: ControlledDecodedBodyBatchClient.CallbackTiming.AfterReturn,
            completionException: oom);
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1, _ => ValueTask.FromResult<INntpClient>(inner));
        using var client = new MultiConnectionNntpClient(
            pool,
            ProviderType.Pooled,
            new ProviderCircuitBreaker("batch-oom"),
            "batch-oom");

        var recorder = new ArticleBodyCompletionRecorder();
        var batch = await client.DecodedBodiesAsync(["a@test"], recorder.Invoke, CancellationToken.None);
        Assert.Equal(0, client.AvailableConnections);
        Assert.False(batch.Completion.IsCompleted);

        inner.FireCapturedCallback(ArticleBodyResult.NotRetrieved, "out-of-memory");
        var thrown = await Assert.ThrowsAsync<OutOfMemoryException>(
            () => batch.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(oom, thrown);
        Assert.Equal(1, client.AvailableConnections);
        Assert.Equal(1, recorder.Count);
        Assert.Equal(ArticleBodyResult.NotRetrieved, recorder.Result);
    }
}
