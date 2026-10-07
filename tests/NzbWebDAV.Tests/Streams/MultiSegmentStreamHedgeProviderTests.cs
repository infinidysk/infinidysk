using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Clients.Usenet;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Streams;

/// <summary>
/// Head-of-line hedging through the production client stack: permit admission
/// (<see cref="DownloadingNntpClient"/>), provider selection
/// (<see cref="MultiProviderNntpClient"/>) and pooled connections for two providers.
/// </summary>
[Collection(nameof(GlobalStreamTraceCollection))]
public sealed class MultiSegmentStreamHedgeProviderTests
{
    private const int SegmentCount = 8;
    private const int SegmentSize = 64;
    private const int Permits = 8;
    private const string StalledId = "seg-1";

    [Fact]
    public async Task DuplicateOnOtherProvider_WinsAndLateOriginalReleasesItsPermit()
    {
        await using var harness = new Harness();
        using var trace = new MultiSegmentStreamHedgeTests.HedgeTraceCapture();

        var buffer = await harness.ReadAllAsync();

        Assert.Equal(harness.Expected, buffer);
        harness.AssertDuplicateOnOtherProvider();
        var hedges = trace.AssertHedge(StalledId, 1, HedgeOutcome.Duplicate);
        // Later articles queue behind the stalled original on its connection, so each is
        // raced as soon as the reader reaches it rather than after another full delay.
        Assert.True(hedges.Count > 1, "Articles behind the stalled original were not raced.");
        Assert.All(hedges.Skip(1), hedge =>
        {
            Assert.Equal(0, hedge.DelayMs);
            Assert.Equal(HedgeOutcome.Duplicate, hedge.Outcome);
        });
        Assert.Equal(Enumerable.Range(1, hedges.Count), hedges.Select(hedge => hedge.Index ?? -1));

        harness.Gate.Original.TrySetResult();
        await harness.AssertSettledAsync();
    }

    [Fact]
    public async Task OriginalAnswersFirst_DuplicateIsCancelledAndReleasesItsPermit()
    {
        await using var harness = new Harness();
        using var trace = new MultiSegmentStreamHedgeTests.HedgeTraceCapture();
        harness.Gate.BeforeDuplicate = async cancellationToken =>
        {
            harness.Gate.Original.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        };

        var buffer = await harness.ReadAllAsync();

        Assert.Equal(harness.Expected, buffer);
        harness.AssertDuplicateOnOtherProvider();
        trace.AssertHedge(StalledId, 1, HedgeOutcome.Original);
        await harness.AssertSettledAsync();
        Assert.Equal(1, harness.Gate.CancelledDuplicates);
    }

    [Fact]
    public async Task OriginalFailsAfterDuplicateWins_IsNotRetriedAndReleasesItsPermit()
    {
        await using var harness = new Harness();
        using var trace = new MultiSegmentStreamHedgeTests.HedgeTraceCapture();

        var buffer = await harness.ReadAllAsync();
        harness.Gate.Original.TrySetException(new IOException("connection reset"));
        await harness.AssertSettledAsync();
        // Long enough for a retry or rescue (250 ms + 500 ms) to have fired.
        await Task.Delay(TimeSpan.FromMilliseconds(1200));

        Assert.Equal(harness.Expected, buffer);
        harness.AssertDuplicateOnOtherProvider();
        var hedges = trace.AssertHedge(StalledId, 1, HedgeOutcome.Duplicate);
        // Failed originals that a duplicate already replaced do not walk providers again.
        Assert.Equal(SegmentCount + hedges.Count, harness.TotalBodyRequests);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly Dictionary<string, byte[]> _segments;
        private readonly GatedConnection _connectionA;
        private readonly GatedConnection _connectionB;
        private readonly MultiConnectionNntpClient _providerA;
        private readonly MultiConnectionNntpClient _providerB;
        private readonly DownloadingNntpClient _client;
        private readonly PrioritizedSemaphore _permits = new(Permits, Permits);
        private readonly CancellationTokenSource _cts = new();
        private readonly IDisposable _priority;
        private readonly Stream _stream;

        public Harness()
        {
            _segments = Enumerable.Range(0, SegmentCount)
                .ToDictionary(i => $"seg-{i}", i => Enumerable.Repeat((byte)i, SegmentSize).ToArray());
            var ranges = _segments.Keys
                .Select((id, index) => KeyValuePair.Create(id, new LongRange(index * SegmentSize, (index + 1L) * SegmentSize)))
                .ToDictionary();
            _connectionA = new GatedConnection("a.example", new FakeNntpClient(_segments, useCachedYencStreams: true, segmentRanges: ranges), Gate);
            _connectionB = new GatedConnection("b.example", new FakeNntpClient(_segments, useCachedYencStreams: true, segmentRanges: ranges), Gate);
            _providerA = MultiProviderNntpClientTests.CreateProvider(_connectionA, host: "a.example", maxConnections: 4);
            _providerB = MultiProviderNntpClientTests.CreateProvider(_connectionB, host: "b.example", maxConnections: 4);
            _client = new DownloadingNntpClient(
                new MultiProviderNntpClient([_providerA, _providerB], cascadeEnabled: () => true),
                new ConfigManager());
            _priority = _cts.Token.SetContext(new DownloadPriorityContext
            {
                Priority = SemaphorePriority.High,
                StreamSemaphore = _permits,
            });
            _stream = MultiSegmentStream.Create(
                _segments.Keys.ToArray().AsMemory(),
                _client,
                articleBufferSize: 40,
                estimatedSegmentSize: SegmentSize,
                failFastOnFirstSegment: false,
                usePipelinedBodyRequests: true,
                _cts.Token,
                fileName: "hedge.bin",
                exactSegmentSizes: Enumerable.Repeat((long)SegmentSize, SegmentCount).ToArray(),
                bodyPipelineBatchWidth: 4);
        }

        public ArticleGate Gate { get; } = new(StalledId);

        public byte[] Expected => _segments.Values.SelectMany(bytes => bytes).ToArray();

        public int TotalBodyRequests => _connectionA.TotalBodyRequests + _connectionB.TotalBodyRequests;

        public async Task<byte[]> ReadAllAsync()
        {
            var buffer = new byte[SegmentCount * SegmentSize];
            await _stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: true)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            return buffer;
        }

        public void AssertDuplicateOnOtherProvider()
        {
            Assert.NotNull(Gate.OriginalHost);
            var duplicateHost = Assert.Single(Gate.DuplicateHosts);
            Assert.NotEqual(Gate.OriginalHost, duplicateHost);
        }

        /// <summary>
        /// Every request has returned its connection to its provider pool and its permit to
        /// the stream semaphore: all permits are immediately available and no more.
        /// </summary>
        public async Task AssertSettledAsync()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (_providerA.ActiveConnections + _providerB.ActiveConnections > 0 && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            Assert.Equal(0, _providerA.ActiveConnections);
            Assert.Equal(0, _providerB.ActiveConnections);

            // Permits return on the completion callback, which may run just after the pool return.
            for (var i = 0; i < Permits; i++)
            {
                var permit = _permits.WaitAsync(SemaphorePriority.High);
                await Task.WhenAny(permit, Task.Delay(TimeSpan.FromSeconds(10)));
                Assert.True(permit.IsCompletedSuccessfully, $"Permit {i + 1} of {Permits} leaked.");
            }

            using var probe = new CancellationTokenSource();
            var extra = _permits.WaitAsync(SemaphorePriority.High, probe.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            Assert.False(extra.IsCompleted, "More permits are available than the semaphore owns.");
            await probe.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extra);
            for (var i = 0; i < Permits; i++) _permits.Release();
        }

        public async ValueTask DisposeAsync()
        {
            Gate.Original.TrySetResult();
            await _stream.DisposeAsync();
            _priority.Dispose();
            _cts.Dispose();
            _client.Dispose();
            _permits.Dispose();
        }
    }

    /// <summary>
    /// Shared across both providers: the first batch carrying the stalled article is held,
    /// and single-article requests for it (the duplicate) are recorded per host.
    /// </summary>
    private sealed class ArticleGate(string id)
    {
        private int _claimed;
        private int _cancelledDuplicates;
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _duplicateHosts = new();

        public string Id => id;
        public TaskCompletionSource Original { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? OriginalHost { get; private set; }
        public IReadOnlyCollection<string> DuplicateHosts => _duplicateHosts;
        public int CancelledDuplicates => Volatile.Read(ref _cancelledDuplicates);
        public Func<CancellationToken, Task>? BeforeDuplicate { get; set; }

        public bool TryClaimOriginal(string host)
        {
            if (Interlocked.Exchange(ref _claimed, 1) != 0) return false;
            OriginalHost = host;
            return true;
        }

        public void RecordDuplicate(string host) => _duplicateHosts.Enqueue(host);
        public void RecordCancelledDuplicate() => Interlocked.Increment(ref _cancelledDuplicates);
    }

    /// <summary>
    /// A provider connection that answers from a <see cref="FakeNntpClient"/> but, like a
    /// pipelined transport, keeps the batch's connection busy until the held response settles
    /// and fires each completion callback exactly once (including on cancellation).
    /// </summary>
    private sealed class GatedConnection(string host, FakeNntpClient inner, ArticleGate gate) : WrappingNntpClient(inner)
    {
        private readonly SemaphoreSlim _innerLock = new(1, 1);

        public int TotalBodyRequests => inner.BodyRequestCounts.Values.Sum();

        public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, ArticleBodyCompletionHandler? onConnectionReadyAgain, CancellationToken cancellationToken)
        {
            if (segmentId.ToString() == gate.Id)
            {
                gate.RecordDuplicate(host);
                if (gate.BeforeDuplicate is { } hook)
                {
                    try
                    {
                        await hook(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        gate.RecordCancelledDuplicate();
                        onConnectionReadyAgain?.Invoke(ArticleBodyResult.Cancelled, null);
                        throw;
                    }
                }
            }

            await _innerLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                return await base.DecodedBodyAsync(segmentId, onConnectionReadyAgain, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _innerLock.Release();
            }
        }

        public override async Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            UsenetDecodedBodyBatch batch;
            await _innerLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                batch = await base.DecodedBodiesAsync(segmentIds, null, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _innerLock.Release();
            }

            var index = segmentIds.Select(id => id.ToString()).ToList().IndexOf(gate.Id);
            if (index < 0 || !gate.TryClaimOriginal(host))
            {
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved, null);
                return batch;
            }

            var responses = batch.Responses.ToArray();
            var held = HoldAsync(responses[index], cancellationToken);
            responses[index] = held;
            var completion = held.ContinueWith(
                task => onConnectionReadyAgain?.Invoke(
                    task.IsCompletedSuccessfully ? ArticleBodyResult.Retrieved
                    : task.IsCanceled ? ArticleBodyResult.Cancelled
                    : ArticleBodyResult.NotRetrieved,
                    task.Exception?.InnerException?.Message),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return batch with { Responses = responses, Completion = completion };
        }

        private async Task<UsenetDecodedBodyResponse> HoldAsync(
            Task<UsenetDecodedBodyResponse> response, CancellationToken cancellationToken)
        {
            await gate.Original.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await response.ConfigureAwait(false);
        }
    }
}
