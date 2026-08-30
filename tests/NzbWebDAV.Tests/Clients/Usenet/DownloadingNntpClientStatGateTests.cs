using System.Collections.Concurrent;
using System.Text.Json;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.TestUtils;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class DownloadingNntpClientStatGateTests
{
    [Fact]
    public async Task StatAsync_RespectsMaxQueueConnections()
    {
        var gate = new ManualResetEventSlim(false);
        var inFlight = 0;
        var maxInFlight = 0;
        var fake = new BlockingStatNntpClient(gate, () =>
        {
            var current = Interlocked.Increment(ref inFlight);
            Interlocked.Exchange(ref maxInFlight, Math.Max(Volatile.Read(ref maxInFlight), current));
        }, () => Interlocked.Decrement(ref inFlight));

        var config = CreateConfig(maxQueueConnections: 2, maxDownloadConnections: 10);
        using var client = new DownloadingNntpClient(fake, config);

        var tasks = Enumerable.Range(0, 10)
            .Select(i => client.StatAsync(new SegmentId($"seg-{i}"), CancellationToken.None))
            .ToArray();

        await Task.Delay(100);
        Assert.True(Volatile.Read(ref maxInFlight) <= 2);

        gate.Set();
        await Task.WhenAll(tasks);

        Assert.True(maxInFlight <= 2);
        Assert.Equal(0, Volatile.Read(ref inFlight));
    }

    [Fact]
    public async Task QueueBudget_FollowsPresetChangesWithoutRestart()
    {
        var gate = new ManualResetEventSlim(false);
        var inFlight = 0;
        var fake = new BlockingStatNntpClient(gate,
            () => Interlocked.Increment(ref inFlight),
            () => Interlocked.Decrement(ref inFlight));

        // "low" is a quarter of the eight pooled connections.
        var config = CreatePresetConfig("low", poolConnections: 8);
        Assert.Equal(2, config.GetMaxQueueConnections());
        using var client = new DownloadingNntpClient(fake, config);

        var tasks = Enumerable.Range(0, 8)
            .Select(i => client.StatAsync(new SegmentId($"seg-{i}"), CancellationToken.None))
            .ToArray();

        await WaitUntilAsync(() => Volatile.Read(ref inFlight) == 2, TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        Assert.Equal(2, Volatile.Read(ref inFlight));

        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.UsenetMaxQueueConnectionsPreset, ConfigValue = "max" },
        ]);

        await WaitUntilAsync(() => Volatile.Read(ref inFlight) == 8, TimeSpan.FromSeconds(2));

        gate.Set();
        await Task.WhenAll(tasks);
        Assert.Equal(0, Volatile.Read(ref inFlight));
    }

    [Fact]
    public async Task StatAsync_CancellationWhileWaiting_DoesNotLeakPermit()
    {
        var holdFirst = new ManualResetEventSlim(false);
        var fake = new BlockingStatNntpClient(holdFirst, onEnter: null, onExit: null);
        var config = CreateConfig(maxQueueConnections: 1, maxDownloadConnections: 10);
        using var client = new DownloadingNntpClient(fake, config);

        var first = client.StatAsync(new SegmentId("held"), CancellationToken.None);
        await Task.Delay(50);

        using var cts = new CancellationTokenSource();
        var waiting = client.StatAsync(new SegmentId("waiting"), cts.Token);
        await Task.Delay(50);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);

        holdFirst.Set();
        await first;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await client.StatAsync(new SegmentId("after"), timeout.Token);
    }

    [Fact]
    public async Task HeadAsync_RespectsMaxQueueConnections()
    {
        var gate = new ManualResetEventSlim(false);
        var inFlight = 0;
        var maxInFlight = 0;
        var fake = new BlockingHeadNntpClient(gate, () =>
        {
            var current = Interlocked.Increment(ref inFlight);
            Interlocked.Exchange(ref maxInFlight, Math.Max(Volatile.Read(ref maxInFlight), current));
        }, () => Interlocked.Decrement(ref inFlight));

        var config = CreateConfig(maxQueueConnections: 2, maxDownloadConnections: 10);
        using var client = new DownloadingNntpClient(fake, config);

        var tasks = Enumerable.Range(0, 8)
            .Select(i => client.HeadAsync(new SegmentId($"seg-{i}"), CancellationToken.None))
            .ToArray();

        await Task.Delay(100);
        Assert.True(Volatile.Read(ref maxInFlight) <= 2);

        gate.Set();
        await Task.WhenAll(tasks);
        Assert.True(maxInFlight <= 2);
    }

    [Fact]
    public async Task StatAsync_PrimaryQueueContext_AdmittedBeforeSecondaryWaiters()
    {
        using var holdSecondary = new ManualResetEventSlim(false);
        using var holdPrimary = new ManualResetEventSlim(false);
        var entered = new ConcurrentQueue<string>();
        var fake = new SelectiveBlockingStatNntpClient(
            segmentId =>
            {
                entered.Enqueue(segmentId);
                return segmentId switch
                {
                    "secondary-held" => holdSecondary,
                    "primary-waiting" => holdPrimary,
                    _ => null,
                };
            });

        var config = CreateConfig(maxQueueConnections: 1, maxDownloadConnections: 10, poolConnections: 20);
        using var client = new DownloadingNntpClient(fake, config);

        var secondaryCtx = new QueueDownloadContext
        {
            IsPrimary = false,
            GetFanOutConcurrency = () => 1,
        };
        var primaryCtx = new QueueDownloadContext
        {
            IsPrimary = true,
            GetFanOutConcurrency = () => 1,
        };

        using var secondaryHeldCts = new CancellationTokenSource();
        using var secondaryHeldReg = secondaryHeldCts.Token.SetContext(secondaryCtx);
        var heldSecondary = client.StatAsync(new SegmentId("secondary-held"), secondaryHeldCts.Token);
        await WaitUntilAsync(() => entered.Contains("secondary-held"), TimeSpan.FromSeconds(2));

        using var primaryCts = new CancellationTokenSource();
        using var primaryReg = primaryCts.Token.SetContext(primaryCtx);
        var primary = client.StatAsync(new SegmentId("primary-waiting"), primaryCts.Token);

        using var secondaryWaitingCts = new CancellationTokenSource();
        using var secondaryWaitingReg = secondaryWaitingCts.Token.SetContext(secondaryCtx);
        var waitingSecondary = client.StatAsync(new SegmentId("secondary-waiting"), secondaryWaitingCts.Token);

        await Task.Delay(80);
        Assert.DoesNotContain("primary-waiting", entered);
        Assert.DoesNotContain("secondary-waiting", entered);

        // Free the held secondary; the waiting primary (High lane) should run next
        // and remain in-flight while we assert the Low-lane secondary is still waiting.
        holdSecondary.Set();
        await WaitUntilAsync(() => entered.Contains("primary-waiting"), TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        Assert.DoesNotContain("secondary-waiting", entered);

        holdPrimary.Set();
        await Task.WhenAll(primary, heldSecondary, waitingSecondary);

        var order = entered.ToArray();
        Assert.Equal(
            new[] { "secondary-held", "primary-waiting", "secondary-waiting" },
            order);
    }

    [Fact]
    public async Task DecodedBodiesPipelinedAsync_RecordsSemaphoreWait_UnderQueueContention()
    {
        var holdFirst = new ManualResetEventSlim(false);
        var entered = new ConcurrentQueue<string>();
        var fake = new BlockingPipelinedBodyNntpClient(holdFirst, entered);
        var config = CreateConfig(maxQueueConnections: 1, maxDownloadConnections: 10);
        using var client = new DownloadingNntpClient(fake, config);

        var heldCtx = new QueueDownloadContext
        {
            IsPrimary = true,
            GetFanOutConcurrency = () => 1,
        };
        var waitingCtx = new QueueDownloadContext
        {
            IsPrimary = false,
            GetFanOutConcurrency = () => 1,
        };

        using var heldCts = new CancellationTokenSource();
        using var heldReg = heldCts.Token.SetContext(heldCtx);
        var heldTask = CollectPipelinedBodiesAsync(client, ["held"], heldCts.Token);
        await WaitUntilAsync(() => entered.Contains("held"), TimeSpan.FromSeconds(2));

        using var waitingCts = new CancellationTokenSource();
        using var waitingReg = waitingCts.Token.SetContext(waitingCtx);
        var waitingTask = CollectPipelinedBodiesAsync(client, ["waiting"], waitingCts.Token);

        await Task.Delay(80);
        Assert.DoesNotContain("waiting", entered);
        Assert.Equal(0, waitingCtx.SemaphoreWaitMilliseconds);

        holdFirst.Set();
        await Task.WhenAll(heldTask, waitingTask);

        Assert.Contains("waiting", entered);
        Assert.True(
            waitingCtx.SemaphoreWaitMilliseconds > 0,
            $"Expected pipelined wait to record semaphore contention, got {waitingCtx.SemaphoreWaitMilliseconds}ms");
    }

    public enum CompletionApi { Body, Batch, Article }

    [Theory]
    [InlineData(CompletionApi.Body)]
    [InlineData(CompletionApi.Batch)]
    [InlineData(CompletionApi.Article)]
    public async Task DuplicateInnerCompletion_ReleasesPermitAndForwardsOnlyOnce(CompletionApi api)
    {
        var inner = new ManualCompletionNntpClient();
        var config = CreateConfig(maxQueueConnections: 1, maxDownloadConnections: 10);
        using var client = new DownloadingNntpClient(inner, config);
        var outerA = new ArticleBodyCompletionRecorder();

        var aTask = StartApi(client, api, "a", outerA.Invoke, CancellationToken.None);
        var aOp = await WaitForOpAsync(inner, 1);
        await DrainAsync(api, aTask);

        var bTask = StartApi(client, api, "b", null, CancellationToken.None);
        var cTask = StartApi(client, api, "c", null, CancellationToken.None);
        await WaitUntilAsync(() => !bTask.IsCompleted && !cTask.IsCompleted && inner.Ops.Count == 1, TimeSpan.FromSeconds(5));

        aOp.Callback!(ArticleBodyResult.Retrieved, null);
        aOp.Callback!(ArticleBodyResult.NotRetrieved, "SocketException");

        var bOp = await WaitForOpAsync(inner, 2);
        Assert.Equal(2, inner.Ops.Count);
        Assert.False(cTask.IsCompleted);
        Assert.Equal(1, outerA.Count);
        Assert.Equal(ArticleBodyResult.Retrieved, outerA.Result);
        Assert.Null(outerA.FailureReason);
        Assert.Equal(1, inner.MaxEntries);

        bOp.Callback!(ArticleBodyResult.Retrieved, null);
        var cOp = await WaitForOpAsync(inner, 3);
        cOp.Callback!(ArticleBodyResult.Retrieved, null);

        await DrainAsync(api, bTask);
        await DrainAsync(api, cTask);

        var dTask = StartApi(client, api, "d", null, CancellationToken.None);
        var dOp = await WaitForOpAsync(inner, 4);
        dOp.Callback!(ArticleBodyResult.Retrieved, null);
        await DrainAsync(api, dTask);
        Assert.Equal(1, inner.MaxEntries);
    }

    [Theory]
    [InlineData(CompletionApi.Body)]
    [InlineData(CompletionApi.Batch)]
    [InlineData(CompletionApi.Article)]
    public async Task DuplicateInnerCompletion_ConcurrentFire_ForwardsOneCompletePair(CompletionApi api)
    {
        var inner = new ManualCompletionNntpClient();
        var config = CreateConfig(maxQueueConnections: 1, maxDownloadConnections: 10);
        using var client = new DownloadingNntpClient(inner, config);
        var outerA = new ArticleBodyCompletionRecorder();

        var aTask = StartApi(client, api, "a", outerA.Invoke, CancellationToken.None);
        var aOp = await WaitForOpAsync(inner, 1);
        await DrainAsync(api, aTask);

        var bTask = StartApi(client, api, "b", null, CancellationToken.None);
        await WaitUntilAsync(() => !bTask.IsCompleted && inner.Ops.Count == 1, TimeSpan.FromSeconds(5));

        var first = (Result: ArticleBodyResult.Retrieved, Reason: (string?)null);
        var second = (Result: ArticleBodyResult.NotRetrieved, Reason: "SocketException");
        using var barrier = new Barrier(2);
        var fire1 = Task.Run(() =>
        {
            barrier.SignalAndWait();
            aOp.Callback!(first.Result, first.Reason);
        });
        var fire2 = Task.Run(() =>
        {
            barrier.SignalAndWait();
            aOp.Callback!(second.Result, second.Reason);
        });
        await Task.WhenAll(fire1, fire2);

        Assert.Equal(1, outerA.Count);
        Assert.True(
            (outerA.Result == first.Result && outerA.FailureReason == first.Reason)
            || (outerA.Result == second.Result && outerA.FailureReason == second.Reason),
            $"Mixed completion pair: {outerA.Result}, {outerA.FailureReason}");

        var bOp = await WaitForOpAsync(inner, 2);
        bOp.Callback!(ArticleBodyResult.Retrieved, null);
        await DrainAsync(api, bTask);
        Assert.Equal(1, inner.MaxEntries);
    }

    [Theory]
    [InlineData(CompletionApi.Body)]
    [InlineData(CompletionApi.Batch)]
    [InlineData(CompletionApi.Article)]
    public async Task ThrowingOuterCallback_DoesNotFaultSuccessfulTransport_AndPermitRecovers(CompletionApi api)
    {
        var inner = new ManualCompletionNntpClient();
        var config = CreateConfig(maxQueueConnections: 1, maxDownloadConnections: 10);
        using var client = new DownloadingNntpClient(inner, config);
        var outerA = new ArticleBodyCompletionRecorder(throwOnInvoke: true);

        var aTask = StartApi(client, api, "a", outerA.Invoke, CancellationToken.None);
        var aOp = await WaitForOpAsync(inner, 1);
        var bTask = StartApi(client, api, "b", null, CancellationToken.None);
        await WaitUntilAsync(() => !bTask.IsCompleted && inner.Ops.Count == 1, TimeSpan.FromSeconds(5));

        aOp.Callback!(ArticleBodyResult.Retrieved, null);
        await DrainAsync(api, aTask);
        Assert.Equal(1, outerA.Count);
        Assert.Equal(ArticleBodyResult.Retrieved, outerA.Result);

        var bOp = await WaitForOpAsync(inner, 2);
        bOp.Callback!(ArticleBodyResult.Retrieved, null);
        await DrainAsync(api, bTask);
    }

    [Theory]
    [InlineData(CompletionApi.Body, ArticleBodyResult.Retrieved, null)]
    [InlineData(CompletionApi.Body, ArticleBodyResult.Cancelled, null)]
    [InlineData(CompletionApi.Body, ArticleBodyResult.NotFound, null)]
    [InlineData(CompletionApi.Body, ArticleBodyResult.NotRetrieved, "SocketException")]
    [InlineData(CompletionApi.Batch, ArticleBodyResult.Retrieved, null)]
    [InlineData(CompletionApi.Batch, ArticleBodyResult.Cancelled, null)]
    [InlineData(CompletionApi.Batch, ArticleBodyResult.NotFound, null)]
    [InlineData(CompletionApi.Batch, ArticleBodyResult.NotRetrieved, "SocketException")]
    [InlineData(CompletionApi.Article, ArticleBodyResult.Retrieved, null)]
    [InlineData(CompletionApi.Article, ArticleBodyResult.Cancelled, null)]
    [InlineData(CompletionApi.Article, ArticleBodyResult.NotFound, null)]
    [InlineData(CompletionApi.Article, ArticleBodyResult.NotRetrieved, "SocketException")]
    public async Task TerminalStatus_IsForwardedOnceAndReleasesPermit(
        CompletionApi api, ArticleBodyResult result, string? failureReason)
    {
        var inner = new ManualCompletionNntpClient();
        var config = CreateConfig(maxQueueConnections: 1, maxDownloadConnections: 10);
        using var client = new DownloadingNntpClient(inner, config);
        var outer = new ArticleBodyCompletionRecorder();

        var aTask = StartApi(client, api, "a", outer.Invoke, CancellationToken.None);
        var aOp = await WaitForOpAsync(inner, 1);
        aOp.Callback!(result, failureReason);
        await DrainAsync(api, aTask);

        Assert.Equal(1, outer.Count);
        Assert.Equal(result, outer.Result);
        Assert.Equal(failureReason, outer.FailureReason);

        var bTask = StartApi(client, api, "b", null, CancellationToken.None);
        var bOp = await WaitForOpAsync(inner, 2);
        bOp.Callback!(ArticleBodyResult.Retrieved, null);
        await DrainAsync(api, bTask);
    }

    [Fact]
    public async Task CancellationWhileWaiting_ThrowingCallbackPreservesOperationCanceledException()
    {
        var inner = new ManualCompletionNntpClient();
        var config = CreateConfig(maxQueueConnections: 1, maxDownloadConnections: 10);
        using var client = new DownloadingNntpClient(inner, config);

        var aTask = client.DecodedBodyAsync(new SegmentId("a"), null, CancellationToken.None);
        var aOp = await WaitForOpAsync(inner, 1);

        using var cts = new CancellationTokenSource();
        var outerB = new ArticleBodyCompletionRecorder(throwOnInvoke: true);
        var bTask = client.DecodedBodyAsync(new SegmentId("b"), outerB.Invoke, cts.Token);
        await WaitUntilAsync(() => !bTask.IsCompleted && inner.Ops.Count == 1, TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bTask);
        Assert.Equal(1, outerB.Count);
        Assert.Equal(ArticleBodyResult.NotRetrieved, outerB.Result);
        Assert.Single(inner.Ops);

        aOp.Callback!(ArticleBodyResult.Retrieved, null);
        await DrainAsync(CompletionApi.Body, aTask);

        var cTask = client.DecodedBodyAsync(new SegmentId("c"), null, CancellationToken.None);
        var cOp = await WaitForOpAsync(inner, 2);
        cOp.Callback!(ArticleBodyResult.Retrieved, null);
        await DrainAsync(CompletionApi.Body, cTask);
    }

    [Fact]
    public async Task CleanMiss_ForwardsNotFoundOnceAndPermitRecovers()
    {
        var inner = new ManualCompletionNntpClient
        {
            AutoComplete = true,
            AutoResult = ArticleBodyResult.NotFound,
            AutoReason = null,
            Success = false,
        };
        var config = CreateConfig(maxQueueConnections: 1, maxDownloadConnections: 10);
        using var client = new DownloadingNntpClient(inner, config);
        var outer = new ArticleBodyCompletionRecorder();

        var miss = await client.DecodedBodyAsync(new SegmentId("missing"), outer.Invoke, CancellationToken.None);
        Assert.Equal((int)UsenetResponseType.NoArticleWithThatMessageId, miss.ResponseCode);
        Assert.Equal(1, outer.Count);
        Assert.Equal(ArticleBodyResult.NotFound, outer.Result);
        Assert.Null(outer.FailureReason);
        Assert.Single(inner.Ops);

        inner.AutoComplete = false;
        inner.Success = true;
        var recovered = client.DecodedBodyAsync(new SegmentId("recovered"), null, CancellationToken.None);
        var recoveredOp = await WaitForOpAsync(inner, 2);
        recoveredOp.Callback!(ArticleBodyResult.Retrieved, null);
        await DrainAsync(CompletionApi.Body, recovered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquireExclusiveConnectionAsync_DuplicateInvoke_ReleasesPermitOnce(bool useListOverload)
    {
        var inner = new ManualCompletionNntpClient();
        var config = CreateConfig(maxQueueConnections: 1, maxDownloadConnections: 10);
        using var client = new DownloadingNntpClient(inner, config);

        var exclusive = useListOverload
            ? await client.AcquireExclusiveConnectionAsync(
                (IReadOnlyList<SegmentId>)[new SegmentId("held")], CancellationToken.None)
            : await client.AcquireExclusiveConnectionAsync("held", CancellationToken.None);

        var bTask = client.DecodedBodyAsync(new SegmentId("b"), null, CancellationToken.None);
        var cTask = client.DecodedBodyAsync(new SegmentId("c"), null, CancellationToken.None);
        await WaitUntilAsync(() => !bTask.IsCompleted && !cTask.IsCompleted && inner.Ops.Count == 0, TimeSpan.FromSeconds(5));

        exclusive.OnConnectionReadyAgain!(ArticleBodyResult.Retrieved, null);
        exclusive.OnConnectionReadyAgain!(ArticleBodyResult.NotRetrieved, "SocketException");

        var bOp = await WaitForOpAsync(inner, 1);
        Assert.Single(inner.Ops);
        Assert.False(cTask.IsCompleted);

        bOp.Callback!(ArticleBodyResult.Retrieved, null);
        var cOp = await WaitForOpAsync(inner, 2);
        cOp.Callback!(ArticleBodyResult.Retrieved, null);
        await DrainAsync(CompletionApi.Body, bTask);
        await DrainAsync(CompletionApi.Body, cTask);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquireExclusiveConnectionAsync_ConcurrentInvoke_ReleasesPermitOnce(bool useListOverload)
    {
        var inner = new ManualCompletionNntpClient();
        var config = CreateConfig(maxQueueConnections: 1, maxDownloadConnections: 10);
        using var client = new DownloadingNntpClient(inner, config);

        var exclusive = useListOverload
            ? await client.AcquireExclusiveConnectionAsync(
                (IReadOnlyList<SegmentId>)[new SegmentId("held")], CancellationToken.None)
            : await client.AcquireExclusiveConnectionAsync("held", CancellationToken.None);

        var bTask = client.DecodedBodyAsync(new SegmentId("b"), null, CancellationToken.None);
        await WaitUntilAsync(() => !bTask.IsCompleted && inner.Ops.Count == 0, TimeSpan.FromSeconds(5));

        using var barrier = new Barrier(2);
        await Task.WhenAll(
            Task.Run(() =>
            {
                barrier.SignalAndWait();
                exclusive.OnConnectionReadyAgain!(ArticleBodyResult.Retrieved, null);
            }),
            Task.Run(() =>
            {
                barrier.SignalAndWait();
                exclusive.OnConnectionReadyAgain!(ArticleBodyResult.NotRetrieved, "SocketException");
            }));

        var bOp = await WaitForOpAsync(inner, 1);
        Assert.Single(inner.Ops);
        bOp.Callback!(ArticleBodyResult.Retrieved, null);
        await DrainAsync(CompletionApi.Body, bTask);
    }

    [Fact]
    public async Task DuplicateInnerCompletion_NullOuterCallback_StillReleasesPermitOnce()
    {
        var inner = new ManualCompletionNntpClient();
        var config = CreateConfig(maxQueueConnections: 1, maxDownloadConnections: 10);
        using var client = new DownloadingNntpClient(inner, config);

        var aTask = client.DecodedBodyAsync(new SegmentId("a"), null, CancellationToken.None);
        var aOp = await WaitForOpAsync(inner, 1);
        var bTask = client.DecodedBodyAsync(new SegmentId("b"), null, CancellationToken.None);
        await WaitUntilAsync(() => !bTask.IsCompleted && inner.Ops.Count == 1, TimeSpan.FromSeconds(5));

        aOp.Callback!(ArticleBodyResult.Retrieved, null);
        aOp.Callback!(ArticleBodyResult.NotRetrieved, "SocketException");
        await DrainAsync(CompletionApi.Body, aTask);

        var bOp = await WaitForOpAsync(inner, 2);
        Assert.Equal(2, inner.Ops.Count);
        bOp.Callback!(ArticleBodyResult.Retrieved, null);
        await DrainAsync(CompletionApi.Body, bTask);
    }

    private static Task StartApi(
        DownloadingNntpClient client,
        CompletionApi api,
        string segmentId,
        ArticleBodyCompletionHandler? callback,
        CancellationToken cancellationToken) => api switch
    {
        CompletionApi.Body => client.DecodedBodyAsync(new SegmentId(segmentId), callback, cancellationToken),
        CompletionApi.Batch => client.DecodedBodiesAsync([new SegmentId(segmentId)], callback, cancellationToken),
        CompletionApi.Article => client.DecodedArticleAsync(new SegmentId(segmentId), callback, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(api)),
    };

    private static async Task DrainAsync(CompletionApi api, Task task)
    {
        switch (api)
        {
            case CompletionApi.Body:
            {
                var body = await (Task<UsenetDecodedBodyResponse>)task;
                if (body.Stream != null)
                {
                    await using (body.Stream)
                        await body.Stream.CopyToAsync(Stream.Null);
                }

                break;
            }
            case CompletionApi.Batch:
            {
                var batch = await (Task<UsenetDecodedBodyBatch>)task;
                foreach (var responseTask in batch.Responses)
                {
                    var response = await responseTask;
                    if (response.Stream != null)
                    {
                        await using (response.Stream)
                            await response.Stream.CopyToAsync(Stream.Null);
                    }
                }

                break;
            }
            case CompletionApi.Article:
            {
                var article = await (Task<UsenetDecodedArticleResponse>)task;
                await using (article.Stream)
                    await article.Stream.CopyToAsync(Stream.Null);
                break;
            }
        }
    }

    private static async Task<PendingOp> WaitForOpAsync(ManualCompletionNntpClient inner, int count)
    {
        await WaitUntilAsync(() => inner.Ops.Count >= count, TimeSpan.FromSeconds(5));
        var op = inner.Ops[count - 1];
        await op.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return op;
    }

    private static async Task<List<PipelinedBodyResult>> CollectPipelinedBodiesAsync(
        DownloadingNntpClient client,
        IReadOnlyList<string> segmentIds,
        CancellationToken cancellationToken)
    {
        var results = new List<PipelinedBodyResult>();
        await foreach (var result in client.DecodedBodiesPipelinedAsync(
                           segmentIds, depth: 1, cancellationToken).ConfigureAwait(false))
        {
            results.Add(result);
        }

        return results;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail($"Condition not met within {timeout.TotalSeconds:0.#}s");
    }

    private static ConfigManager CreateConfig(
        int maxQueueConnections,
        int maxDownloadConnections,
        int poolConnections = 50)
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue =
                    $$"""{"providers":[{"host":"nntp.example","port":563,"useSsl":true,"user":"u","pass":"p","maxConnections":{{poolConnections}},"type":1}]}""",
            },
            new ConfigItem { ConfigName = ConfigKeys.UsenetMaxQueueConnections, ConfigValue = maxQueueConnections.ToString() },
            new ConfigItem { ConfigName = ConfigKeys.UsenetMaxDownloadConnections, ConfigValue = maxDownloadConnections.ToString() },
        ]);
        return config;
    }

    // Provider JSON is serialized from the model rather than hand-written: config
    // deserialization is case-sensitive, so camelCased literals bind to nothing and
    // the pooled total silently collapses to 1.
    private static ConfigManager CreatePresetConfig(string preset, int poolConnections)
    {
        var providers = JsonSerializer.Serialize(new UsenetProviderConfig
        {
            Providers =
            [
                new UsenetProviderConfig.ConnectionDetails
                {
                    Type = ProviderType.Pooled,
                    Host = "nntp.example",
                    Port = 563,
                    UseSsl = true,
                    User = "u",
                    Pass = "p",
                    MaxConnections = poolConnections,
                },
            ]
        });

        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.UsenetProviders, ConfigValue = providers },
            new ConfigItem { ConfigName = ConfigKeys.UsenetMaxQueueConnectionsPreset, ConfigValue = preset },
            new ConfigItem { ConfigName = ConfigKeys.UsenetMaxDownloadConnections, ConfigValue = "10" },
        ]);
        return config;
    }

    private sealed class PendingOp
    {
        public required ArticleBodyCompletionHandler? Callback { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ManualCompletionNntpClient : MinimalNntpClient
    {
        private readonly List<PendingOp> _ops = [];
        private int _currentEntries;
        private int _maxEntries;

        public bool AutoComplete { get; set; }
        public ArticleBodyResult AutoResult { get; set; } = ArticleBodyResult.Retrieved;
        public string? AutoReason { get; set; }
        public bool Success { get; set; } = true;

        public IReadOnlyList<PendingOp> Ops
        {
            get
            {
                lock (_ops) return _ops.ToArray();
            }
        }

        public int MaxEntries => Volatile.Read(ref _maxEntries);

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            var op = Enter(onConnectionReadyAgain);
            try
            {
                MaybeComplete(op);
                return Task.FromResult(CreateBody(segmentId));
            }
            finally
            {
                Exit();
            }
        }

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            var op = Enter(onConnectionReadyAgain);
            try
            {
                MaybeComplete(op);
                var responses = segmentIds
                    .Select(id => Task.FromResult(CreateBody(id)))
                    .ToArray();
                return Task.FromResult(new UsenetDecodedBodyBatch { Responses = responses });
            }
            finally
            {
                Exit();
            }
        }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            var op = Enter(onConnectionReadyAgain);
            try
            {
                MaybeComplete(op);
                return Task.FromResult(CreateArticle(segmentId));
            }
            finally
            {
                Exit();
            }
        }

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken) =>
            DecodedBodyAsync(segmentId, exclusiveConnection.OnConnectionReadyAgain, cancellationToken);

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken) =>
            DecodedBodiesAsync(segmentIds, exclusiveConnection.OnConnectionReadyAgain, cancellationToken);

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken) =>
            DecodedArticleAsync(segmentId, exclusiveConnection.OnConnectionReadyAgain, cancellationToken);

        private PendingOp Enter(ArticleBodyCompletionHandler? callback)
        {
            var current = Interlocked.Increment(ref _currentEntries);
            int snapshot;
            while (current > (snapshot = Volatile.Read(ref _maxEntries)))
            {
                if (Interlocked.CompareExchange(ref _maxEntries, current, snapshot) == snapshot)
                    break;
            }

            var op = new PendingOp { Callback = callback };
            lock (_ops) _ops.Add(op);
            op.Entered.TrySetResult();
            return op;
        }

        private void Exit() => Interlocked.Decrement(ref _currentEntries);

        private void MaybeComplete(PendingOp op)
        {
            if (AutoComplete)
                op.Callback?.Invoke(AutoResult, AutoReason);
        }

        private UsenetDecodedBodyResponse CreateBody(SegmentId segmentId)
        {
            var bytes = "body"u8.ToArray();
            return new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId.ToString(),
                ResponseCode = Success
                    ? (int)UsenetResponseType.ArticleRetrievedBodyFollows
                    : (int)UsenetResponseType.NoArticleWithThatMessageId,
                ResponseMessage = Success ? "222" : "430",
                Stream = Success ? CreateStream(bytes) : null,
            };
        }

        private static UsenetDecodedArticleResponse CreateArticle(SegmentId segmentId)
        {
            var bytes = "article"u8.ToArray();
            return new UsenetDecodedArticleResponse
            {
                SegmentId = segmentId.ToString(),
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow,
                ResponseMessage = "220",
                ArticleHeaders = new UsenetArticleHeader
                {
                    Headers = new Dictionary<string, string> { ["Subject"] = "test" },
                },
                Stream = CreateStream(bytes),
            };
        }

        private static CachedYencStream CreateStream(byte[] bytes) =>
            new(
                new UsenetYencHeader
                {
                    FileName = "fake.bin",
                    FileSize = bytes.Length,
                    LineLength = 128,
                    PartNumber = 1,
                    TotalParts = 1,
                    PartOffset = 0,
                    PartSize = bytes.Length,
                },
                new MemoryStream(bytes, writable: false));
    }

    private abstract class MinimalNntpClient : NntpClient
    {
        public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken) =>
            Task.FromResult(new UsenetStatResponse
            {
                ResponseCode = (int)UsenetResponseType.ArticleExists,
                ResponseMessage = $"223 0 0 <{segmentId}>",
                ArticleExists = true,
            });

        public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken) =>
            Task.FromResult(new UsenetHeadResponse
            {
                SegmentId = segmentId.ToString(),
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadFollows,
                ResponseMessage = "221",
                ArticleHeaders = null!,
            });

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

        public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
            string segmentId, CancellationToken cancellationToken) =>
            Task.FromResult(new UsenetExclusiveConnection(null));

        public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
            IReadOnlyList<SegmentId> segmentIds, CancellationToken cancellationToken) =>
            Task.FromResult(new UsenetExclusiveConnection(null));

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, UsenetExclusiveConnection exclusiveConnection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, UsenetExclusiveConnection exclusiveConnection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }

    private sealed class SelectiveBlockingStatNntpClient(Func<string, ManualResetEventSlim?> gateFor)
        : MinimalNntpClient
    {
        public override async Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken)
        {
            var gate = gateFor(segmentId.ToString());
            if (gate is not null)
            {
                while (!gate.IsSet)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                }
            }

            return await base.StatAsync(segmentId, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class RecordingStatNntpClient(INntpClient inner, ConcurrentQueue<string> entered) : MinimalNntpClient
    {
        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken)
        {
            entered.Enqueue(segmentId.ToString());
            return inner.StatAsync(segmentId, cancellationToken);
        }
    }

    private sealed class BlockingStatNntpClient(
        ManualResetEventSlim gate,
        Action? onEnter,
        Action? onExit) : MinimalNntpClient
    {
        public override async Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken)
        {
            onEnter?.Invoke();
            try
            {
                while (!gate.IsSet)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                }

                return await base.StatAsync(segmentId, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                onExit?.Invoke();
            }
        }
    }

    private sealed class BlockingHeadNntpClient(
        ManualResetEventSlim gate,
        Action? onEnter,
        Action? onExit) : MinimalNntpClient
    {
        public override async Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken)
        {
            onEnter?.Invoke();
            try
            {
                while (!gate.IsSet)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                }

                return await base.HeadAsync(segmentId, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                onExit?.Invoke();
            }
        }
    }

    private sealed class BlockingPipelinedBodyNntpClient(
        ManualResetEventSlim gate,
        ConcurrentQueue<string> entered) : MinimalNntpClient
    {
        public override async IAsyncEnumerable<PipelinedBodyResult> DecodedBodiesPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            int depth,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var segmentId in segmentIds)
            {
                entered.Enqueue(segmentId);
                while (!gate.IsSet)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                }

                yield return new PipelinedBodyResult
                {
                    SegmentId = segmentId,
                    Found = true,
                };
            }
        }
    }
}
