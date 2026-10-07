using System.Collections.Concurrent;
using System.Diagnostics;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Streams;

[Collection(nameof(StreamTraceCollection))]
public sealed class MultiSegmentStreamHedgeTests
{
    [Fact]
    public async Task StalledHeadSegment_DuplicateFetchKeepsPlaybackMoving()
    {
        const int segmentSize = 64;
        var segments = Enumerable.Range(0, 8)
            .ToDictionary(i => $"seg-{i}", i => Enumerable.Repeat((byte)i, segmentSize).ToArray());
        var ranges = segments.Keys
            .Select((id, index) => KeyValuePair.Create(id, new LongRange(index * segmentSize, (index + 1L) * segmentSize)))
            .ToDictionary();
        var inner = new FakeNntpClient(segments, useCachedYencStreams: true, segmentRanges: ranges);
        using var stalled = new StalledArticleClient(inner, "seg-1");
        using var trace = new HedgeTraceCapture();
        using var cts = new CancellationTokenSource();
        using var priority = cts.Token.SetContext(new DownloadPriorityContext { Priority = SemaphorePriority.High });
        var stream = MultiSegmentStream.Create(
            segments.Keys.ToArray().AsMemory(),
            stalled,
            articleBufferSize: 40,
            estimatedSegmentSize: segmentSize,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: true,
            cts.Token,
            fileName: "hedge.bin",
            bodyPipelineBatchWidth: 4);
        try
        {
            var buffer = new byte[segments.Count * segmentSize];
            var elapsed = Stopwatch.StartNew();
            await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: true)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"Delivery waited {elapsed.Elapsed} on the stalled original.");
            Assert.Equal(segments.Values.SelectMany(bytes => bytes), buffer);
            Assert.Equal(2, inner.BodyRequestCounts["seg-1"]);
            // Later articles answered on their own, so only the stalled one is raced.
            Assert.Single(trace.AssertHedge("seg-1", 1, HedgeOutcome.Duplicate));
        }
        finally
        {
            stalled.Release();
            await stream.DisposeAsync();
        }

        // The late original is discarded rather than rescued or reported as a failure.
        Assert.Equal(2, inner.BodyRequestCounts["seg-1"]);
        Assert.Equal(0, stalled.FailedCallbacks);
    }

    [Fact]
    public async Task PacedReader_ShortWaitOnOldReadAheadSegmentIsNotDuplicated()
    {
        const int segmentSize = 64;
        var segments = Enumerable.Range(0, 8)
            .ToDictionary(i => $"seg-{i}", i => Enumerable.Repeat((byte)i, segmentSize).ToArray());
        var ranges = segments.Keys
            .Select((id, index) => KeyValuePair.Create(id, new LongRange(index * segmentSize, (index + 1L) * segmentSize)))
            .ToDictionary();
        var inner = new FakeNntpClient(segments, useCachedYencStreams: true, segmentRanges: ranges);
        using var stalled = new StalledArticleClient(inner, "seg-1");
        using var cts = new CancellationTokenSource();
        using var priority = cts.Token.SetContext(new DownloadPriorityContext { Priority = SemaphorePriority.High });
        await using var stream = MultiSegmentStream.Create(
            segments.Keys.ToArray().AsMemory(),
            stalled,
            articleBufferSize: 40,
            estimatedSegmentSize: segmentSize,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: true,
            cts.Token,
            fileName: "hedge.bin",
            bodyPipelineBatchWidth: 4);

        var buffer = new byte[segments.Count * segmentSize];
        await stream.ReadAtLeastAsync(buffer.AsMemory(0, segmentSize), segmentSize, throwOnEndOfStream: true);
        _ = Task.Delay(TimeSpan.FromMilliseconds(1500)).ContinueWith(_ => stalled.Release(), TaskScheduler.Default);
        // The player idles well past the hedge delay, then reaches seg-1 shortly before it answers.
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        await stream.ReadAtLeastAsync(buffer.AsMemory(segmentSize), buffer.Length - segmentSize, throwOnEndOfStream: true)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(segments.Values.SelectMany(bytes => bytes), buffer);
        Assert.Equal(1, inner.BodyRequestCounts["seg-1"]);
    }

    [Fact]
    public async Task PaddedDuplicate_DoesNotReplaceHealthyOriginal()
    {
        var (segments, ranges) = CreateSegments(8);
        var factoryCalls = 0;
        // The duplicate (second body for seg-1) decodes short; the original is intact.
        var inner = new FakeNntpClient(
            segments,
            useCachedYencStreams: true,
            segmentRanges: ranges,
            decodedStreamFactory: (key, bytes) =>
                key == "seg-1" && Interlocked.Increment(ref factoryCalls) == 2
                    ? new MemoryStream(bytes[..(bytes.Length / 2)], writable: false)
                    : new MemoryStream(bytes, writable: false));
        var duplicateRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stalled = new StalledArticleClient(inner, "seg-1")
        {
            BeforeSingleRequest = (id, _) =>
            {
                if (id == "seg-1") duplicateRequested.TrySetResult();
                return Task.CompletedTask;
            },
        };
        using var trace = new HedgeTraceCapture();
        using var cts = new CancellationTokenSource();
        using var priority = cts.Token.SetContext(new DownloadPriorityContext { Priority = SemaphorePriority.High });
        await using var stream = CreateStream(segments, stalled, cts.Token);

        var buffer = new byte[segments.Count * SegmentSize];
        var read = stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: true).AsTask();
        await duplicateRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        stalled.Release();
        await read.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(segments.Values.SelectMany(bytes => bytes), buffer);
        Assert.Equal(2, inner.BodyRequestCounts["seg-1"]);
        trace.AssertHedge("seg-1", 1, HedgeOutcome.OriginalAfterDuplicateShort);
    }

    [Fact]
    public async Task SupersededOriginalFailure_IsNotRescued()
    {
        var (segments, ranges) = CreateSegments(8);
        var inner = new FakeNntpClient(segments, useCachedYencStreams: true, segmentRanges: ranges);
        using var stalled = new StalledArticleClient(inner, "seg-1");
        using var cts = new CancellationTokenSource();
        using var priority = cts.Token.SetContext(new DownloadPriorityContext { Priority = SemaphorePriority.High });
        await using var stream = CreateStream(segments, stalled, cts.Token);

        var buffer = new byte[segments.Count * SegmentSize];
        await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: true)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, inner.BodyRequestCounts["seg-1"]);

        // The original fails after the duplicate delivered; the stream stays open long enough
        // for retries and rescue (250 ms + 500 ms) to have fired.
        stalled.Fail("seg-1", new IOException("connection reset"));
        await Task.Delay(TimeSpan.FromMilliseconds(1200));

        Assert.Equal(segments.Values.SelectMany(bytes => bytes), buffer);
        Assert.Equal(2, inner.BodyRequestCounts["seg-1"]);
    }

    [Fact]
    public async Task ExhaustedOriginal_PendingDuplicateStillDelivers()
    {
        var (segments, ranges) = CreateSegments(8);
        var inner = new FakeNntpClient(segments, useCachedYencStreams: true, segmentRanges: ranges);
        StalledArticleClient? stalled = null;
        stalled = new StalledArticleClient(inner, "seg-1")
        {
            BeforeSingleRequest = async (id, attempt) =>
            {
                if (id != "seg-1") return;
                if (attempt > 1) throw new IOException("rescue unavailable");
                // The original dies as soon as the duplicate is issued; the duplicate answers slowly.
                stalled!.Fail("seg-1", new IOException("connection reset"));
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            },
        };
        using var disposeStalled = stalled;
        using var trace = new HedgeTraceCapture();
        using var cts = new CancellationTokenSource();
        using var priority = cts.Token.SetContext(new DownloadPriorityContext { Priority = SemaphorePriority.High });
        await using var stream = CreateStream(segments, stalled, cts.Token);

        var buffer = new byte[segments.Count * SegmentSize];
        await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: true)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(segments.Values.SelectMany(bytes => bytes), buffer);
        trace.AssertHedge("seg-1", 1, HedgeOutcome.DuplicateAfterOriginalExhausted);
    }

    [Fact]
    public async Task LateOriginalAfterRingWraparound_DoesNotDisableNewerDuplicate()
    {
        int ringLength;
        var (probeSegments, _) = CreateSegments(1);
        await using (var probe = CreateStream(
                         probeSegments, new FakeNntpClient(probeSegments), CancellationToken.None))
            ringLength = ((MultiSegmentStream)probe).TaskWindowSize * 2 + BatchWidth + 1;

        // seg-(1 + ring) shares seg-1's tracking slot.
        var newerId = $"seg-{1 + ringLength}";
        var (segments, ranges) = CreateSegments(ringLength + 8);
        var inner = new FakeNntpClient(segments, useCachedYencStreams: true, segmentRanges: ranges);
        StalledArticleClient? stalled = null;
        stalled = new StalledArticleClient(inner, "seg-1", newerId)
        {
            OnBatchRequested = ids =>
            {
                if (ids.Any(id => id.ToString() == newerId))
                    _ = Task.Delay(TimeSpan.FromMilliseconds(100))
                        .ContinueWith(_ => stalled!.Release("seg-1"), TaskScheduler.Default);
            },
        };
        using var disposeStalled = stalled;
        using var cts = new CancellationTokenSource();
        using var priority = cts.Token.SetContext(new DownloadPriorityContext { Priority = SemaphorePriority.High });
        var stream = CreateStream(segments, stalled, cts.Token);
        try
        {
            var buffer = new byte[segments.Count * SegmentSize];
            await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: true)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(segments.Values.SelectMany(bytes => bytes), buffer);
            Assert.Equal(2, inner.BodyRequestCounts[newerId]);
        }
        finally
        {
            stalled.Release();
            await stream.DisposeAsync();
        }
    }

    private const int SegmentSize = 64;
    private const int BatchWidth = 4;

    private static (Dictionary<string, byte[]> Segments, Dictionary<string, LongRange> Ranges) CreateSegments(int count)
    {
        var segments = Enumerable.Range(0, count)
            .ToDictionary(i => $"seg-{i}", i => Enumerable.Repeat((byte)i, SegmentSize).ToArray());
        var ranges = segments.Keys
            .Select((id, index) => KeyValuePair.Create(id, new LongRange(index * SegmentSize, (index + 1L) * SegmentSize)))
            .ToDictionary();
        return (segments, ranges);
    }

    private static Stream CreateStream(
        Dictionary<string, byte[]> segments, INntpClient client, CancellationToken cancellationToken) =>
        MultiSegmentStream.Create(
            segments.Keys.ToArray().AsMemory(),
            client,
            articleBufferSize: 40,
            estimatedSegmentSize: SegmentSize,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: true,
            cancellationToken,
            fileName: "hedge.bin",
            exactSegmentSizes: Enumerable.Repeat((long)SegmentSize, segments.Count).ToArray(),
            bodyPipelineBatchWidth: BatchWidth);

    /// <summary>
    /// Routes stream-trace events for the current async flow into a private buffer and restores
    /// the process-wide buffer on dispose. Users must join <see cref="StreamTraceCollection"/>.
    /// </summary>
    internal sealed class HedgeTraceCapture : IDisposable
    {
        private readonly StreamTraceBuffer? _previous = StreamTrace.Buffer;
        private readonly StreamTraceBuffer _buffer = new(capacity: 1_000, maxSessions: 16);
        private readonly Guid _sessionId = Guid.NewGuid();
        private readonly IDisposable _scope;

        public HedgeTraceCapture()
        {
            StreamTrace.Configure(_buffer);
            _scope = MultiProviderNntpClient.BeginReadSessionScope(_sessionId);
            _buffer.RangeOpen(_sessionId, "/view/hedge.bin", "GET", 0, null, null, null, null);
        }

        /// <summary>
        /// Asserts the first hedge waited at least the delay floor and resolved with
        /// <paramref name="outcome"/>; returns every hedge's (index, delay, outcome) in order.
        /// </summary>
        public IReadOnlyList<(int? Index, int? DelayMs, string? Outcome)> AssertHedge(
            string segmentId, int segmentIndex, string outcome)
        {
            var events = _buffer.GetSessionEvents(_sessionId);
            var issued = events.Where(e => e.Kind == nameof(StreamTraceKind.HedgeIssued)).ToList();
            var resolved = events.Where(e => e.Kind == nameof(StreamTraceKind.HedgeResolved)).ToList();
            var summary = string.Join("; ", events.Where(e => e.Kind.StartsWith("Hedge", StringComparison.Ordinal))
                .Select(e => $"{e.Kind} {e.SegmentId}#{e.SegmentIndex} status={e.Status} ms={e.DurationMs} delay={e.HedgeDelayMs}"));
            Assert.True(issued.Count > 0 && issued.Count == resolved.Count, $"Unpaired hedge events: {summary}");
            Assert.Equal(issued.Select(e => e.SegmentIndex), resolved.Select(e => e.SegmentIndex));
            Assert.All(issued, e => Assert.True(e.DurationMs >= e.HedgeDelayMs, $"Hedge fired early: {summary}"));

            Assert.Equal(segmentId, issued[0].SegmentId);
            Assert.Equal(segmentIndex, issued[0].SegmentIndex);
            Assert.True(issued[0].HedgeDelayMs >= 500, $"First hedge skipped the delay floor: {summary}");
            Assert.Equal(segmentId, resolved[0].SegmentId);
            Assert.Equal(outcome, resolved[0].Status);
            return issued.Zip(resolved, (i, r) => (i.SegmentIndex, i.HedgeDelayMs, r.Status)).ToList();
        }

        public void Dispose()
        {
            _scope.Dispose();
            StreamTrace.Configure(_previous ?? new StreamTraceBuffer(capacity: 1, maxSessions: 10, enabled: false));
        }
    }

    private sealed class StalledArticleClient(INntpClient inner, params string[] stalledIds) : WrappingNntpClient(inner)
    {
        private readonly Dictionary<string, TaskCompletionSource> _gates = stalledIds.ToDictionary(
            id => id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        private readonly ConcurrentDictionary<string, byte> _stalled = new();
        private readonly ConcurrentDictionary<string, int> _singleRequests = new();
        private int _failedCallbacks;

        public int FailedCallbacks => Volatile.Read(ref _failedCallbacks);

        /// <summary>Runs before each single-article body request with the 1-based request count for that id.</summary>
        public Func<string, int, Task>? BeforeSingleRequest { get; init; }

        public Action<IReadOnlyList<SegmentId>>? OnBatchRequested { get; init; }

        public void Release()
        {
            foreach (var gate in _gates.Values) gate.TrySetResult();
        }

        public void Release(string id) => _gates[id].TrySetResult();

        public void Fail(string id, Exception exception) => _gates[id].TrySetException(exception);

        public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken)
        {
            await RunSingleRequestHookAsync(segmentId).ConfigureAwait(false);
            return await base.DecodedBodyAsync(segmentId, cancellationToken).ConfigureAwait(false);
        }

        public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, ArticleBodyCompletionHandler? onConnectionReadyAgain, CancellationToken cancellationToken)
        {
            await RunSingleRequestHookAsync(segmentId).ConfigureAwait(false);
            return await base.DecodedBodyAsync(segmentId, onConnectionReadyAgain, cancellationToken).ConfigureAwait(false);
        }

        private Task RunSingleRequestHookAsync(SegmentId segmentId)
        {
            var id = segmentId.ToString();
            var attempt = _singleRequests.AddOrUpdate(id, 1, (_, count) => count + 1);
            return BeforeSingleRequest?.Invoke(id, attempt) ?? Task.CompletedTask;
        }

        public override async Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            OnBatchRequested?.Invoke(segmentIds);
            var batch = await base.DecodedBodiesAsync(segmentIds, Track(onConnectionReadyAgain), cancellationToken)
                .ConfigureAwait(false);
            var responses = batch.Responses.ToArray();
            for (var index = 0; index < responses.Length; index++)
            {
                var id = segmentIds[index].ToString();
                if (!_gates.TryGetValue(id, out var gate) || !_stalled.TryAdd(id, 0))
                    continue;
                responses[index] = AfterGateAsync(gate.Task, responses[index]);
            }

            return batch with { Responses = responses };
        }

        private static async Task<UsenetDecodedBodyResponse> AfterGateAsync(
            Task gate, Task<UsenetDecodedBodyResponse> original)
        {
            await gate.ConfigureAwait(false);
            return await original.ConfigureAwait(false);
        }

        private ArticleBodyCompletionHandler Track(ArticleBodyCompletionHandler? callback) =>
            (result, reason) =>
            {
                if (result == ArticleBodyResult.NotRetrieved)
                    Interlocked.Increment(ref _failedCallbacks);
                callback?.Invoke(result, reason);
            };
    }
}
