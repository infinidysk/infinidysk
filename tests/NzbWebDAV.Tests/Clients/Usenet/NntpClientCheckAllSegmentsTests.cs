using System.Collections.Concurrent;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class NntpClientCheckAllSegmentsTests
{
    [Fact]
    public async Task CheckAllSegmentsAsync_With451_ThrowsArticleNotFound()
    {
        var client = new StatCodeClient(451);

        var exception = await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() =>
            client.CheckAllSegmentsAsync(["seg@example"], 1, null, CancellationToken.None));

        Assert.Equal("seg@example", exception.SegmentId);
    }

    [Fact]
    public async Task CheckAllSegmentsAsync_With430_ThrowsArticleNotFound()
    {
        var client = new StatCodeClient(430);

        await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() =>
            client.CheckAllSegmentsAsync(["seg@example"], 1, null, CancellationToken.None));
    }

    [Fact]
    public async Task CheckAllSegmentsAsync_With400_ThrowsUnexpectedResponse()
    {
        var client = new StatCodeClient(400);

        var exception = await Assert.ThrowsAsync<UsenetUnexpectedResponseException>(() =>
            client.CheckAllSegmentsAsync(["seg@example"], 1, null, CancellationToken.None));

        Assert.IsAssignableFrom<RetryableDownloadException>(exception);
    }

    [Fact]
    public async Task CheckAllSegmentsAsync_With223_Succeeds()
    {
        var client = new StatCodeClient(223);

        await client.CheckAllSegmentsAsync(["seg@example"], 1, null, CancellationToken.None);
    }

    [Fact]
    public async Task CheckAllSegmentsAsync_MidpointMissCancelsInFlightAndStartsNoFurtherStats()
    {
        var slowIds = new[] { "slow-0", "slow-1", "slow-2" };
        const string failId = "fail-mid";
        var extraIds = new[] { "extra-0", "extra-1", "extra-2" };
        var client = new CoordinatedStatClient(failId, expectedSlowStarts: slowIds.Length);
        var ids = slowIds.Concat([failId]).Concat(extraIds).ToArray();
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var exception = await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() =>
            client.CheckAllSegmentsAsync(ids, concurrency: 4, progress: null, timeoutCts.Token));

        Assert.Equal(failId, exception.SegmentId);
        Assert.Equal(0, client.StatStartsAfterFailure);
        foreach (var extraId in extraIds)
            Assert.False(client.StatStartCounts.ContainsKey(extraId), extraId);
        foreach (var slowId in slowIds)
            Assert.True(client.StatStartCounts.ContainsKey(slowId), slowId);
        Assert.True(client.AllStartedStatsCompleted);
    }

    [Fact]
    public async Task ArticleExistenceChecker_UsesConcurrentPoolPath()
    {
        var client = new TrackingPipelinedStatClient(
            pipelinedExists: [true, true],
            recheckCodes: [223, 223]);

        await ArticleExistenceChecker.CheckAsync(
            client,
            ["a@example", "b@example"],
            concurrency: 7,
            progress: null,
            CancellationToken.None);

        Assert.Equal(1, client.CheckAllSegmentsCallCount);
        Assert.Equal(0, client.PipelinedStatsCallCount);
        Assert.Equal(7, client.LastConcurrency);
        Assert.Equal(["a@example", "b@example"], client.RecheckedSegmentIds);
    }

    [Fact]
    public async Task CheckAllSegmentsPipelinedAsync_WithAllExists_SucceedsWithoutFailoverRecheck()
    {
        var client = new TrackingPipelinedStatClient(
            pipelinedExists: [true, true],
            recheckCodes: []);

        await client.CheckAllSegmentsPipelinedAsync(
            ["a@example", "b@example"], depth: 8, fallbackConcurrency: 2, progress: null,
            CancellationToken.None);

        Assert.Equal(0, client.CheckAllSegmentsCallCount);
        Assert.Empty(client.RecheckedSegmentIds);
    }

    [Fact]
    public async Task CheckAllSegmentsPipelinedAsync_RechecksOnlyMisses()
    {
        var client = new TrackingPipelinedStatClient(
            pipelinedExists: [true, false, true, false],
            recheckCodes: [223, 223]);

        await client.CheckAllSegmentsPipelinedAsync(
            ["a@example", "b@example", "c@example", "d@example"],
            depth: 8,
            fallbackConcurrency: 2,
            progress: null,
            CancellationToken.None);

        Assert.Equal(1, client.CheckAllSegmentsCallCount);
        Assert.Equal(["b@example", "d@example"], client.RecheckedSegmentIds);
    }

    [Fact]
    public async Task CheckAllSegmentsPipelinedAsync_MissConfirmedOnFailover_ThrowsArticleNotFound()
    {
        var client = new TrackingPipelinedStatClient(
            pipelinedExists: [true, false],
            recheckCodes: [430]);

        var exception = await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() =>
            client.CheckAllSegmentsPipelinedAsync(
                ["a@example", "b@example"], 8, 1, null, CancellationToken.None));

        Assert.Equal("b@example", exception.SegmentId);
        Assert.Equal(["b@example"], client.RecheckedSegmentIds);
    }

    [Fact]
    public async Task CheckAllSegmentsPipelinedAsync_SweepThrowsUnexpected_FallsBackToFullConcurrentPath()
    {
        var client = new TrackingPipelinedStatClient(
            pipelinedExists: null,
            recheckCodes: [223, 223],
            sweepException: new UsenetUnexpectedResponseException("a@example", "400 idle timeout"));

        await client.CheckAllSegmentsPipelinedAsync(
            ["a@example", "b@example"], 8, 2, null, CancellationToken.None);

        Assert.Equal(1, client.CheckAllSegmentsCallCount);
        Assert.Equal(["a@example", "b@example"], client.RecheckedSegmentIds);
    }

    [Fact]
    public async Task CheckAllSegmentsPipelinedAsync_SweepThrowsProtocol_FallsBackToFullConcurrentPath()
    {
        var client = new TrackingPipelinedStatClient(
            pipelinedExists: null,
            recheckCodes: [223, 223],
            sweepException: new UsenetProtocolException(
                "The NNTP connection closed before all pipelined STAT responses were received."));

        await client.CheckAllSegmentsPipelinedAsync(
            ["a@example", "b@example"], 8, 2, null, CancellationToken.None);

        Assert.Equal(1, client.CheckAllSegmentsCallCount);
        Assert.Equal(["a@example", "b@example"], client.RecheckedSegmentIds);
    }

    [Fact]
    public async Task CheckAllSegmentsPipelinedAsync_SweepThrowsAfterProgress_FallbackProgressIsMonotonic()
    {
        var reports = new List<int>();
        // Collect synchronously — System.Progress<T> posts via the sync context / thread
        // pool and races List enumeration in Assert.Equal.
        var progress = new CollectingProgress(reports);
        var client = new TrackingPipelinedStatClient(
            pipelinedExists: [true, true, true],
            recheckCodes: [223, 223, 223],
            sweepException: new UsenetProtocolException("connection closed mid-sweep"),
            throwAfterYieldCount: 2);

        await client.CheckAllSegmentsPipelinedAsync(
            ["a@example", "b@example", "c@example"], 8, 2, progress, CancellationToken.None);

        Assert.Equal(1, client.CheckAllSegmentsCallCount);
        Assert.Equal(["a@example", "b@example", "c@example"], client.RecheckedSegmentIds);
        // Pipelined reports 1,2 then throw; fallback clamps so n=1,2 stay at 2 before advancing to 3.
        Assert.Equal([1, 2, 2, 2, 3], reports);
    }

    [Fact]
    public async Task CollectMissingSegmentsPipelinedAsync_SweepThrowsAfterProgress_FallbackProgressIsMonotonic()
    {
        var reports = new List<int>();
        var progress = new CollectingProgress(reports);
        var client = new TrackingPipelinedStatClient(
            pipelinedExists: [true, true, true],
            recheckCodes: [223, 223, 223],
            sweepException: new UsenetProtocolException("connection closed mid-sweep"),
            throwAfterYieldCount: 2);

        var missing = await client.CollectMissingSegmentsPipelinedAsync(
            ["a@example", "b@example", "c@example"], 8, 2, progress, CancellationToken.None);

        Assert.Empty(missing);
        Assert.Equal(["a@example", "b@example", "c@example"], client.RecheckedSegmentIds);
        // Pipelined reports 1,2 then throw; fallback clamps so n=1,2 stay at 2 before advancing to 3.
        Assert.Equal([1, 2, 2, 2, 3], reports);
    }

    [Fact]
    public async Task CollectMissingSegmentsPipelinedAsync_CollectsConfirmedMissesInInputOrder()
    {
        var client = new TrackingPipelinedStatClient(
            pipelinedExists: [false, true, false],
            recheckCodes: [430, 223]);

        var missing = await client.CollectMissingSegmentsPipelinedAsync(
            ["a@example", "b@example", "c@example"], 8, 2, null, CancellationToken.None);

        Assert.Equal(["a@example"], missing);
        Assert.Equal(["a@example", "c@example"], client.RecheckedSegmentIds);
    }

    [Fact]
    public async Task CollectMissingSegmentsPipelinedAsync_NonDefinitiveRecheckThrows()
    {
        var client = new TrackingPipelinedStatClient(
            pipelinedExists: [false],
            recheckCodes: [400]);

        await Assert.ThrowsAsync<UsenetUnexpectedResponseException>(() =>
            client.CollectMissingSegmentsPipelinedAsync(
                ["a@example"], 8, 1, null, CancellationToken.None));
    }

    [Fact]
    public async Task CollectMissingSegmentsPipelinedAsync_WithAllExists_ReturnsEmptyWithoutRecheck()
    {
        var client = new TrackingPipelinedStatClient(
            pipelinedExists: [true, true],
            recheckCodes: []);

        var missing = await client.CollectMissingSegmentsPipelinedAsync(
            ["a@example", "b@example"], 8, 2, null, CancellationToken.None);

        Assert.Empty(missing);
        Assert.Empty(client.RecheckedSegmentIds);
        Assert.Equal(0, client.CheckAllSegmentsCallCount);
    }

    [Fact]
    public async Task CollectMissingSegmentsPipelinedAsync_FansWindowsAcrossConcurrencyBudget()
    {
        var client = new GatedPipelinedStatClient();
        var segmentIds = Enumerable.Range(0, NntpClient.StatPipelinedDispatchBatchSize + 1)
            .Select(index => $"{index}@example")
            .ToArray();

        var sweep = client.CollectMissingSegmentsPipelinedAsync(
            segmentIds, depth: 8, fallbackConcurrency: 2, progress: null,
            CancellationToken.None);
        try
        {
            await client.BothBatchesStarted.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            client.ReleaseBatches();
        }

        Assert.Empty(await sweep);
        Assert.Equal(2, client.PipelinedStatsCallCount);
        Assert.Equal(2, client.MaxConcurrentCalls);
    }

    [Fact]
    public async Task CollectMissingSegmentsPipelinedAsync_WithEmptyInput_ReturnsEmpty()
    {
        var client = new TrackingPipelinedStatClient(
            pipelinedExists: [],
            recheckCodes: []);

        var missing = await client.CollectMissingSegmentsPipelinedAsync(
            [], 8, 2, null, CancellationToken.None);

        Assert.Empty(missing);
        Assert.Equal(0, client.PipelinedStatsCallCount);
    }

    [Fact]
    public async Task CollectMissingSegmentsPipelinedAsync_SweepThrows_CollectingFallbackReturnsFullSet()
    {
        var client = new TrackingPipelinedStatClient(
            pipelinedExists: null,
            recheckCodes: [430, 223, 430],
            sweepException: new UsenetProtocolException("connection closed mid-sweep"));

        var missing = await client.CollectMissingSegmentsPipelinedAsync(
            ["a@example", "b@example", "c@example"], 8, 2, null, CancellationToken.None);

        // The collecting fallback STATs every segment concurrently (not just a partial
        // sweep's misses) and returns the full confirmed set in input order.
        Assert.Equal(["a@example", "c@example"], missing);
        Assert.Equal(["a@example", "b@example", "c@example"], client.RecheckedSegmentIds);
        Assert.Equal(0, client.CheckAllSegmentsCallCount);
    }

    private sealed class CollectingProgress(List<int> reports) : IProgress<int>
    {
        public void Report(int value) => reports.Add(value);
    }

    private sealed class GatedPipelinedStatClient : NntpClient
    {
        private readonly TaskCompletionSource _bothBatchesStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeCalls;
        private int _maxConcurrentCalls;
        private int _pipelinedStatsCallCount;

        public Task BothBatchesStarted => _bothBatchesStarted.Task;
        public int MaxConcurrentCalls => Volatile.Read(ref _maxConcurrentCalls);
        public int PipelinedStatsCallCount => Volatile.Read(ref _pipelinedStatsCallCount);
        public void ReleaseBatches() => _release.TrySetResult();

        public override async IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            int depth,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _pipelinedStatsCallCount);
            var active = Interlocked.Increment(ref _activeCalls);
            RecordMaxConcurrentCalls(active);
            if (PipelinedStatsCallCount == 2)
                _bothBatchesStarted.TrySetResult();

            try
            {
                await _release.Task.WaitAsync(cancellationToken);
                foreach (var segmentId in segmentIds)
                {
                    yield return new PipelinedStatResult
                    {
                        SegmentId = segmentId,
                        Exists = true,
                    };
                }
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }

        private void RecordMaxConcurrentCalls(int active)
        {
            var current = Volatile.Read(ref _maxConcurrentCalls);
            while (active > current)
            {
                var observed = Interlocked.CompareExchange(
                    ref _maxConcurrentCalls, active, current);
                if (observed == current) return;
                current = observed;
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

        public override Task<UsenetDateResponse> DateAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }

    [Fact]
    public async Task MapPipelinedBodyResult_With451_ReportsNotFound()
    {
        var client = new BodyCodeClient(451);

        PipelinedBodyResult? result = null;
        await foreach (var item in client.DecodedBodiesPipelinedAsync(
                           ["seg@example"], 1, CancellationToken.None))
            result = item;

        Assert.NotNull(result);
        var body = result ?? throw new InvalidOperationException("expected result");
        Assert.False(body.Found);
        Assert.Null(body.Stream);
    }

    private sealed class TrackingPipelinedStatClient(
        bool[]? pipelinedExists,
        int[] recheckCodes,
        Exception? sweepException = null,
        int throwAfterYieldCount = 0) : NntpClient
    {
        private int _recheckIndex;

        public int CheckAllSegmentsCallCount { get; private set; }
        public int PipelinedStatsCallCount { get; private set; }
        public int? LastConcurrency { get; private set; }
        public List<string> RecheckedSegmentIds { get; } = [];

        public override async IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            int depth,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            PipelinedStatsCallCount++;
            if (sweepException != null && throwAfterYieldCount <= 0)
                throw sweepException;

            for (var i = 0; i < segmentIds.Count; i++)
            {
                if (sweepException != null && i == throwAfterYieldCount)
                    throw sweepException;

                yield return new PipelinedStatResult
                {
                    SegmentId = segmentIds[i],
                    Exists = pipelinedExists![i],
                };
            }
        }

        public override async Task CheckAllSegmentsAsync(
            IEnumerable<string> segmentIds,
            int concurrency,
            IProgress<int>? progress,
            CancellationToken cancellationToken)
        {
            CheckAllSegmentsCallCount++;
            LastConcurrency = concurrency;
            var list = segmentIds.ToList();
            RecheckedSegmentIds.AddRange(list);

            var processed = 0;
            foreach (var segmentId in list)
            {
                progress?.Report(++processed);
                var code = recheckCodes[_recheckIndex++];
                if (code == (int)UsenetResponseType.ArticleExists) continue;
                if (code is 430 or 451)
                    throw new UsenetArticleNotFoundException(segmentId, $"{code} missing");
                throw new UsenetUnexpectedResponseException(segmentId, $"{code} unexpected");
            }

            await Task.CompletedTask;
        }

        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken)
        {
            RecheckedSegmentIds.Add(segmentId);
            var code = recheckCodes[_recheckIndex++];
            return Task.FromResult(new UsenetStatResponse
            {
                ResponseCode = code,
                ResponseMessage = $"{code} <{segmentId}>",
                ArticleExists = code == (int)UsenetResponseType.ArticleExists,
            });
        }

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

    private sealed class StatCodeClient(int responseCode) : NntpClient
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
                ResponseCode = responseCode,
                ResponseMessage = $"{responseCode} <{segmentId}>",
                ArticleExists = responseCode == (int)UsenetResponseType.ArticleExists,
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

    private sealed class CoordinatedStatClient(string failingId, int expectedSlowStarts) : NntpClient
    {
        private readonly TaskCompletionSource _slowReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _slowStarted;
        private int _failureObserved;
        private int _statStartsAfterFailure;
        private int _started;
        private int _completed;

        public ConcurrentDictionary<string, int> StatStartCounts { get; } = new(StringComparer.Ordinal);
        public int StatStartsAfterFailure => Volatile.Read(ref _statStartsAfterFailure);
        public bool AllStartedStatsCompleted => Volatile.Read(ref _started) == Volatile.Read(ref _completed);

        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override async Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken)
        {
            var id = segmentId.ToString();
            Interlocked.Increment(ref _started);
            try
            {
                if (Volatile.Read(ref _failureObserved) == 1)
                    Interlocked.Increment(ref _statStartsAfterFailure);

                StatStartCounts.AddOrUpdate(id, 1, (_, n) => n + 1);

                if (id == failingId)
                {
                    await _slowReady.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    Interlocked.Exchange(ref _failureObserved, 1);
                    return new UsenetStatResponse
                    {
                        ResponseCode = 430,
                        ResponseMessage = $"430 <{id}>",
                        ArticleExists = false,
                    };
                }

                if (id.StartsWith("slow-", StringComparison.Ordinal))
                {
                    if (Interlocked.Increment(ref _slowStarted) >= expectedSlowStarts)
                        _slowReady.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // cancelled after the miss; still complete so WithConcurrencyAsync can drain
                    }

                    return new UsenetStatResponse
                    {
                        ResponseCode = 223,
                        ResponseMessage = $"223 <{id}>",
                        ArticleExists = true,
                    };
                }

                return new UsenetStatResponse
                {
                    ResponseCode = 223,
                    ResponseMessage = $"223 <{id}>",
                    ArticleExists = true,
                };
            }
            finally
            {
                Interlocked.Increment(ref _completed);
            }
        }

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

    private sealed class BodyCodeClient(int responseCode) : NntpClient
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
            var success = responseCode == (int)UsenetResponseType.ArticleRetrievedBodyFollows;
            return Task.FromResult(new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId.ToString(),
                ResponseCode = responseCode,
                ResponseMessage = $"{responseCode} scripted body",
                Stream = success ? new YencStream(new MemoryStream([], writable: false)) : null,
            });
        }

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            var responses = segmentIds
                .Select(id => DecodedBodyAsync(id, cancellationToken))
                .ToArray();
            return Task.FromResult(new UsenetDecodedBodyBatch { Responses = responses });
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
}
