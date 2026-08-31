using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Extensions;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class NntpClientPipelinedTeardownTests
{
    [Fact]
    public async Task DecodedBodiesPipelinedAsync_AbandonedMidBatch_DisposesUnreadBodies()
    {
        var client = new ScriptedBatchClient();

        await foreach (var result in client.DecodedBodiesPipelinedAsync(
                           ["a@example", "b@example", "c@example", "d@example"],
                           depth: 4,
                           CancellationToken.None))
        {
            Assert.True(result.Found);
            break;
        }

        // Every body in the open batch shares one connection, and a later response cannot
        // complete until the earlier stream is released, so teardown has to release the one
        // it just handed out as well.
        Assert.True(client.Streams[0].Disposed);
        Assert.True(client.Streams[1].Disposed);
        Assert.True(client.Streams[2].Disposed);
        Assert.True(client.Streams[3].Disposed);
    }

    [Fact]
    public async Task DecodedBodiesPipelinedAsync_FullyEnumerated_LeavesBodiesToTheConsumer()
    {
        var client = new ScriptedBatchClient();

        var results = new List<PipelinedBodyResult>();
        await foreach (var result in client.DecodedBodiesPipelinedAsync(
                           ["a@example", "b@example", "c@example"],
                           depth: 3,
                           CancellationToken.None))
        {
            results.Add(result);
        }

        Assert.Equal(3, results.Count);
        Assert.All(client.Streams, stream => Assert.False(stream.Disposed));
    }

    [Fact]
    public async Task DecodedBodiesPipelinedAsync_AbandonedAcrossBatches_OnlyDisposesTheOpenBatch()
    {
        var client = new ScriptedBatchClient();

        await foreach (var result in client.DecodedBodiesPipelinedAsync(
                           ["a@example", "b@example", "c@example", "d@example"],
                           depth: 2,
                           CancellationToken.None))
        {
            Assert.True(result.Found);
            if (client.Streams.Count == 4) break;
        }

        // The first batch was fully consumed before the second was requested, so its bodies
        // stay with the consumer. Only the batch still open at teardown is released.
        Assert.False(client.Streams[0].Disposed);
        Assert.False(client.Streams[1].Disposed);
        Assert.True(client.Streams[2].Disposed);
        Assert.True(client.Streams[3].Disposed);
    }

    [Fact]
    public async Task DecodedBodiesPipelinedAsync_ConsumerAlreadyReleasedCurrentBody_ReleasesItAgain()
    {
        var client = new ScriptedBatchClient();

        await foreach (var result in client.DecodedBodiesPipelinedAsync(
                           ["a@example", "b@example"],
                           depth: 2,
                           CancellationToken.None))
        {
            // What every real consumer does: release the body inside the loop, then stop.
            if (result.Stream != null)
                await result.Stream.DisposeAsync();
            break;
        }

        // Teardown cannot tell whether the consumer already released it, so bodies have to
        // tolerate a repeat dispose.
        Assert.Equal(2, client.Streams[0].DisposeCount);
    }

    [Fact]
    public async Task DecodedBodiesPipelinedAsync_Abandoned_CancelsTheBatchInsteadOfAwaitingIt()
    {
        // Remaining responses never complete on their own. Teardown must cancel the batch
        // rather than wait on work the consumer walked away from.
        var client = new CancelToCompleteBatchClient(count: 3);

        var enumeration = Task.Run(async () =>
        {
            await foreach (var result in client.DecodedBodiesPipelinedAsync(
                               ["a@example", "b@example", "c@example"],
                               depth: 3,
                               CancellationToken.None))
            {
                Assert.True(result.Found);
                break;
            }
        });

        var finished = await Task.WhenAny(enumeration, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(
            ReferenceEquals(finished, enumeration),
            "Teardown waited on responses instead of cancelling the abandoned batch.");
        await enumeration;
        Assert.True(client.BatchTokenCancelled);
    }

    [Fact]
    public async Task DecodedBodiesPipelinedAsync_AbandonedWithoutDrainingCurrent_StillCompletesTeardown()
    {
        // Models the library contract: a later response cannot complete until the earlier
        // stream is drained. Teardown must not wait on a response gated behind a stream the
        // consumer still owns, or disposing the enumerator deadlocks.
        var client = new BackpressuredBatchClient(count: 3);

        var enumeration = Task.Run(async () =>
        {
            await foreach (var result in client.DecodedBodiesPipelinedAsync(
                               ["a@example", "b@example", "c@example"],
                               depth: 3,
                               CancellationToken.None))
            {
                Assert.True(result.Found);
                break;
            }
        });

        var finished = await Task.WhenAny(enumeration, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(
            ReferenceEquals(finished, enumeration),
            "Disposing the enumerator blocked waiting on a response gated behind an undrained stream.");
        await enumeration;
    }

    [Fact]
    public async Task DecodedBodiesPipelinedAsync_CarriesTokenContextIntoTheBatch()
    {
        // Download priority and the streaming deadline are resolved by token identity, so a
        // batch token that does not carry them silently downgrades every pipelined fetch.
        using var callerCts = ContextualCancellationTokenSource.CreateLinkedTokenSource(
            CancellationToken.None);
        using var scope = callerCts.Token.SetContext(new DownloadPriorityContext
        {
            Priority = SemaphorePriority.High,
        });

        var client = new ScriptedBatchClient();
        await foreach (var result in client.DecodedBodiesPipelinedAsync(
                           ["a@example"], depth: 1, callerCts.Token))
        {
            Assert.True(result.Found);
            break;
        }

        // Resolved at the moment the batch is issued, which is when the connection pool
        // reads it in production.
        Assert.Equal(SemaphorePriority.High, client.BatchPriority);
    }

    [Fact]
    public async Task DecodedBodiesPipelinedAsync_FullyEnumerated_DoesNotJoinBatchCompletion()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedBatchClient(gate.Task);
        var enumeration = Task.Run(async () =>
        {
            await foreach (var result in client.DecodedBodiesPipelinedAsync(
                               ["a@example"], depth: 1, CancellationToken.None))
            {
                Assert.True(result.Found);
                if (result.Stream is not null)
                    await result.Stream.DisposeAsync();
            }
        });

        await client.BatchIssued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await enumeration.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(gate.Task.IsCompleted);
        gate.SetResult();
    }

    [Fact]
    public async Task DecodedBodiesPipelinedAsync_EarlyBreak_AwaitsBatchCompletionAfterCleanup()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedBatchClient(gate.Task);
        var enumeration = Task.Run(async () =>
        {
            await foreach (var result in client.DecodedBodiesPipelinedAsync(
                               ["a@example", "b@example"], depth: 2, CancellationToken.None))
            {
                Assert.True(result.Found);
                break;
            }
        });

        await client.BatchIssued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && !client.Streams.TrueForAll(stream => stream.Disposed))
            await Task.Delay(10);

        Assert.True(client.Streams.TrueForAll(stream => stream.Disposed));
        Assert.False(enumeration.IsCompleted);
        gate.SetResult();
        await enumeration.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DecodedBodiesPipelinedAsync_CompletionFault_IsObserved()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedBatchClient(gate.Task);
        var enumeration = Task.Run(async () =>
        {
            await foreach (var result in client.DecodedBodiesPipelinedAsync(
                               ["a@example"], depth: 1, CancellationToken.None))
            {
                Assert.True(result.Found);
                if (result.Stream is not null)
                    await result.Stream.DisposeAsync();
            }
        });

        gate.SetException(new IOException("batch-completion"));
        await enumeration.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class TrackingYencStream : YencStream
    {
        public TrackingYencStream() : base(new MemoryStream([], writable: false))
        {
        }

        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public bool Disposed => DisposeCount > 0;

        protected override void Dispose(bool disposing)
        {
            Interlocked.Increment(ref _disposeCount);
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Every response after the first completes only when the batch is cancelled, standing
    /// in for a fetch that is still outstanding.
    /// </summary>
    private sealed class CancelToCompleteBatchClient(int count) : NntpClient
    {
        private CancellationToken _batchToken;

        public bool BatchTokenCancelled => _batchToken.IsCancellationRequested;

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            _batchToken = cancellationToken;

            var responses = new List<Task<UsenetDecodedBodyResponse>>();
            for (var index = 0; index < count; index++)
            {
                if (index == 0)
                {
                    responses.Add(Task.FromResult(new UsenetDecodedBodyResponse
                    {
                        SegmentId = segmentIds[index].ToString(),
                        ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                        ResponseMessage = "222 first",
                        Stream = new YencStream(new MemoryStream([], writable: false)),
                    }));
                    continue;
                }

                var pending = new TaskCompletionSource<UsenetDecodedBodyResponse>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
                responses.Add(pending.Task);
            }

            return Task.FromResult(new UsenetDecodedBodyBatch { Responses = responses });
        }

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

    /// <summary>
    /// Response N+1 completes only once stream N has been disposed, which is the ordering
    /// UsenetDecodedBodyBatch documents and enforces through pipe backpressure.
    /// </summary>
    private sealed class BackpressuredBatchClient(int count) : NntpClient
    {
        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            var gates = new List<TaskCompletionSource>();
            for (var index = 0; index < count; index++)
                gates.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

            var responses = new List<Task<UsenetDecodedBodyResponse>>();
            for (var index = 0; index < count; index++)
            {
                var position = index;
                var stream = new GatedYencStream(gates[position]);
                var response = new UsenetDecodedBodyResponse
                {
                    SegmentId = segmentIds[position].ToString(),
                    ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                    ResponseMessage = "222 gated",
                    Stream = stream,
                };

                responses.Add(position == 0
                    ? Task.FromResult(response)
                    : gates[position - 1].Task.ContinueWith(
                        _ => response, TaskScheduler.Default));
            }

            return Task.FromResult(new UsenetDecodedBodyBatch { Responses = responses });
        }

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

    private sealed class GatedYencStream(TaskCompletionSource gate)
        : YencStream(new MemoryStream([], writable: false))
    {
        protected override void Dispose(bool disposing)
        {
            gate.TrySetResult();
            base.Dispose(disposing);
        }
    }

    private sealed class ScriptedBatchClient(Task? completion = null) : NntpClient
    {
        public List<TrackingYencStream> Streams { get; } = [];

        public SemaphorePriority? BatchPriority { get; private set; }

        public TaskCompletionSource BatchIssued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            BatchPriority = cancellationToken.GetContext<DownloadPriorityContext>()?.Priority;
            var responses = new List<Task<UsenetDecodedBodyResponse>>();
            foreach (var segmentId in segmentIds)
            {
                var stream = new TrackingYencStream();
                Streams.Add(stream);
                responses.Add(Task.FromResult(new UsenetDecodedBodyResponse
                {
                    SegmentId = segmentId.ToString(),
                    ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                    ResponseMessage = $"222 <{segmentId}>",
                    Stream = stream,
                }));
            }

            BatchIssued.TrySetResult();
            return Task.FromResult(new UsenetDecodedBodyBatch
            {
                Responses = responses,
                Completion = completion ?? Task.CompletedTask,
            });
        }

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
