using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Tests.Streams;

[Collection(nameof(GlobalLoggerCollection))]
public class MultiSegmentStreamPrefetchBudgetTests
{
    [Fact]
    public async Task ReadBudget_CapsPrefetchBelowArticleBufferSize()
    {
        const int segmentCount = 20;
        const int segmentSize = 1000;
        const int articleBufferSize = 10;
        // 2.5 segments of budget → stop once enqueued*size >= budget + size → 4 segments max
        const long readBudget = 2500;

        var segments = Enumerable.Range(0, segmentCount)
            .ToDictionary(
                i => $"seg-{i}",
                i => Enumerable.Repeat((byte)(i % 256), segmentSize).ToArray());
        var client = new FakeNntpClient(segments, useCachedYencStreams: true);
        var segmentIds = segments.Keys.ToArray().AsMemory();

        await using var stream = MultiSegmentStream.Create(
            segmentIds,
            client,
            articleBufferSize,
            estimatedSegmentSize: segmentSize,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            CancellationToken.None,
            fileName: "budget.bin",
            readBudget: readBudget);

        var buffer = new byte[readBudget];
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(totalRead));
            if (n == 0) break;
            totalRead += n;
        }

        Assert.True(client.RequestedSegmentIds.Count <= 4,
            $"Expected ≤4 unique segments with budget, got {client.RequestedSegmentIds.Count} (BODY={client.BodyRequestCount})");
        Assert.True(client.RequestedSegmentIds.Count < articleBufferSize);
    }

    [Fact]
    public async Task NullBudget_PrefetchesUpToArticleBufferSize()
    {
        const int segmentCount = 20;
        const int segmentSize = 100;
        const int articleBufferSize = 10;

        var segments = Enumerable.Range(0, segmentCount)
            .ToDictionary(
                i => $"seg-{i}",
                _ => Enumerable.Repeat((byte)1, segmentSize).ToArray());
        var client = new FakeNntpClient(segments, useCachedYencStreams: true);

        await using var stream = MultiSegmentStream.Create(
            segments.Keys.ToArray().AsMemory(),
            client,
            articleBufferSize,
            estimatedSegmentSize: segmentSize,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            CancellationToken.None,
            fileName: "full.bin",
            readBudget: null);

        // Drain one segment so the producer can fill the channel.
        var buffer = new byte[segmentSize];
        _ = await stream.ReadAsync(buffer);
        await Task.Delay(200);

        Assert.True(client.RequestedSegmentIds.Count >= articleBufferSize - 1,
            $"Expected prefetch near buffer size without budget, got {client.RequestedSegmentIds.Count}");
    }

    [Fact]
    public async Task GlobalCap_LimitsLeasedBytesAndThrottleUnderContention()
    {
        // Deterministic contention: fill the cap, then start waiters that must throttle
        // before concurrent workers prove leased bytes never exceed CapBytes.
        const int unit = 10_000;
        var budget = new InFlightArticleBudget(unit * 2);
        var maxObserved = 0L;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var held1 = await budget.LeaseAsync(unit, cts.Token);
        using var held2 = await budget.LeaseAsync(unit, cts.Token);
        Assert.Equal(unit * 2, budget.LeasedBytes);

        var blocked = budget.LeaseAsync(unit, cts.Token).AsTask();
        for (var i = 0; i < 50 && budget.ThrottleEvents == 0; i++)
            await Task.Delay(10);
        Assert.True(budget.ThrottleEvents > 0,
            "Expected throttle events while the cap is fully held");
        Assert.False(blocked.IsCompleted);

        held1.Dispose();
        held2.Dispose();
        using (await blocked)
            Assert.True(budget.LeasedBytes <= budget.CapBytes);

        var workers = Enumerable.Range(0, 8).Select(async _ =>
        {
            for (var i = 0; i < 20; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                using var lease = await budget.LeaseAsync(unit, cts.Token);
                var leased = budget.LeasedBytes;
                long snapshot;
                do
                {
                    snapshot = Volatile.Read(ref maxObserved);
                    if (leased <= snapshot) break;
                } while (Interlocked.CompareExchange(ref maxObserved, leased, snapshot) != snapshot);

                Assert.True(leased <= budget.CapBytes,
                    $"Leased {leased} exceeded cap {budget.CapBytes}");
                await Task.Yield();
            }
        }).ToArray();

        await Task.WhenAll(workers);
        Assert.Equal(0, budget.LeasedBytes);
        Assert.True(Volatile.Read(ref maxObserved) > 0);
        Assert.True(maxObserved <= budget.CapBytes);
    }

    [Fact]
    public async Task Lease_ReleasedOnCancelMidDrain()
    {
        const int segmentSize = 50_000;
        var budget = new InFlightArticleBudget(segmentSize * 4);
        var segments = Enumerable.Range(0, 20)
            .ToDictionary(
                i => $"seg-{i}",
                _ => Enumerable.Repeat((byte)7, segmentSize).ToArray());
        var client = new FakeNntpClient(segments, useCachedYencStreams: true);

        using var cts = new CancellationTokenSource();
        var stream = MultiSegmentStream.Create(
            segments.Keys.ToArray().AsMemory(),
            client,
            articleBufferSize: 8,
            estimatedSegmentSize: segmentSize,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            cts.Token,
            fileName: "cancel.bin",
            readBudget: null,
            inFlightArticleBudget: budget);

        var buffer = new byte[1024];
        _ = await stream.ReadAsync(buffer, CancellationToken.None);
        Assert.True(budget.LeasedBytes > 0);

        cts.Cancel();
        await stream.DisposeAsync();

        for (var i = 0; i < 50 && budget.LeasedBytes != 0; i++)
            await Task.Delay(20);

        Assert.Equal(0, budget.LeasedBytes);
    }

    [Fact]
    public async Task LeaseWait_CancelRemovesWaiterWithoutLeaking()
    {
        const long cap = 1000;
        var budget = new InFlightArticleBudget(cap);
        using var held = await budget.LeaseAsync(cap, CancellationToken.None);
        Assert.Equal(cap, budget.LeasedBytes);

        using var cts = new CancellationTokenSource();
        var waiting = budget.LeaseAsync(cap, cts.Token).AsTask();

        for (var i = 0; i < 50 && budget.ThrottleEvents == 0; i++)
            await Task.Delay(10);
        Assert.True(budget.ThrottleEvents > 0);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);

        Assert.Equal(cap, budget.LeasedBytes);
        held.Dispose();
        Assert.Equal(0, budget.LeasedBytes);

        using var next = await budget.LeaseAsync(cap, CancellationToken.None);
        Assert.Equal(cap, budget.LeasedBytes);
    }

    [Fact]
    public async Task NullReadBudget_StopsPrefetchAtPerStreamByteCeiling()
    {
        const int segmentCount = 30;
        const int segmentSize = 1000;
        const int articleBufferSize = 3;

        var segments = Enumerable.Range(0, segmentCount)
            .ToDictionary(
                i => $"seg-{i}",
                _ => Enumerable.Repeat((byte)9, segmentSize).ToArray());
        var client = new FakeNntpClient(segments, useCachedYencStreams: true);

        await using var stream = MultiSegmentStream.Create(
            segments.Keys.ToArray().AsMemory(),
            client,
            articleBufferSize,
            estimatedSegmentSize: segmentSize,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            CancellationToken.None,
            fileName: "ceiling.bin",
            readBudget: null);

        var buffer = new byte[segmentSize / 2];
        _ = await stream.ReadAsync(buffer);
        await Task.Delay(300);

        Assert.True(client.RequestedSegmentIds.Count <= articleBufferSize + 2,
            $"Expected prefetch near per-stream ceiling ({articleBufferSize} segments), got {client.RequestedSegmentIds.Count}");
        Assert.True(client.RequestedSegmentIds.Count < segmentCount / 2,
            "Full-file GET must not prefetch unbounded segments when readBudget is null");
    }

    [Fact]
    public async Task LeaseAsync_OversizedSingleSegment_StillProgressesWhenIdle()
    {
        var budget = new InFlightArticleBudget(1024);
        using (var lease = await budget.LeaseAsync(4096, CancellationToken.None))
        {
            Assert.Equal(4096, budget.LeasedBytes);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await budget.LeaseAsync(4096, cts.Token));
        }

        Assert.Equal(0, budget.LeasedBytes);

        using var second = await budget.LeaseAsync(4096, CancellationToken.None);
        Assert.Equal(4096, budget.LeasedBytes);
    }

    [Fact]
    public async Task Throttle_EmitsWarningNotError()
    {
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink)
            .CreateLogger();

        try
        {
            const int segmentSize = 800;
            var budget = new InFlightArticleBudget(capBytes: segmentSize);

            using var first = await budget.LeaseAsync(segmentSize, CancellationToken.None);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await budget.LeaseAsync(segmentSize, cts.Token));

            Assert.Contains(sink.Events, e =>
                e.Level == LogEventLevel.Warning &&
                e.MessageTemplate.Text.Contains("In-flight article memory budget saturated"));
            Assert.DoesNotContain(sink.Events, e =>
                e.Level == LogEventLevel.Error &&
                e.MessageTemplate.Text.Contains("In-flight article memory budget"));
            Assert.True(budget.ThrottleEvents > 0);
        }
        finally
        {
            Log.Logger = previous;
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
}
