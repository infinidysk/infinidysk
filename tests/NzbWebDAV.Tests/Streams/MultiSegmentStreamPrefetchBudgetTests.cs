using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using NzbWebDAV.Exceptions;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Tests.Streams;

[Collection(nameof(GlobalLoggerCollection))]
public class MultiSegmentStreamPrefetchBudgetTests
{
    [Fact]
    public async Task ReadBudget_WithoutExactSizes_DeliversFullConsumerRange()
    {
        const int segmentCount = 20;
        const int segmentSize = 1000;
        const int articleBufferSize = 10;
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

        Assert.Equal(readBudget, totalRead);
    }

    [Fact]
    public async Task ReadBudget_WithExactSizes_CapsPrefetchBelowArticleBufferSize()
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
        var exactSizes = Enumerable.Repeat((long)segmentSize, segmentCount).ToArray();

        await using var stream = MultiSegmentStream.Create(
            segmentIds,
            client,
            articleBufferSize,
            estimatedSegmentSize: segmentSize,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            CancellationToken.None,
            fileName: "budget.bin",
            readBudget: readBudget,
            exactSegmentSizes: exactSizes.AsMemory());

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

        Assert.Equal(0, budget.LeasedBytes);
    }

    [Fact]
    public async Task SourceDisposeFailure_ReleasesFallbackLease()
    {
        const int segmentSize = 10_000;
        var budget = new InFlightArticleBudget(segmentSize * 4);
        var client = new FakeNntpClient(
            new Dictionary<string, byte[]>
            {
                ["fallback"] = Enumerable.Repeat((byte)7, segmentSize).ToArray(),
            },
            useCachedYencStreams: true,
            decodedStreamFactory: (_, bytes) => new ThrowingDisposeMemoryStream(bytes));

        await using var stream = MultiSegmentStream.Create(
            new[] { "missing" }.AsMemory(),
            client,
            articleBufferSize: 4,
            estimatedSegmentSize: segmentSize,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            CancellationToken.None,
            fileName: "dispose-failure.bin",
            segmentFallbacks: [["fallback"]],
            exactSegmentSizes: new long[] { segmentSize },
            inFlightArticleBudget: budget);

        await Assert.ThrowsAsync<IOException>(
            async () => await stream.ReadAtLeastAsync(
                new byte[segmentSize], segmentSize, throwOnEndOfStream: false));

        Assert.Equal(0, budget.LeasedBytes);
    }

    [Fact]
    public async Task DisposeAsync_ReleasesQueuedLeasesBeforeReturning()
    {
        const int segmentSize = 20_000;
        var budget = new InFlightArticleBudget(segmentSize * 16);
        var segments = Enumerable.Range(0, 20)
            .ToDictionary(
                i => $"seg-{i}",
                _ => Enumerable.Repeat((byte)3, segmentSize).ToArray());
        var client = new FakeNntpClient(segments, useCachedYencStreams: true);

        var stream = MultiSegmentStream.Create(
            segments.Keys.ToArray().AsMemory(),
            client,
            articleBufferSize: 8,
            estimatedSegmentSize: segmentSize,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            CancellationToken.None,
            fileName: "dispose.bin",
            readBudget: null,
            inFlightArticleBudget: budget);

        var buffer = new byte[1024];
        _ = await stream.ReadAsync(buffer);
        Assert.True(budget.LeasedBytes > 0);

        await stream.DisposeAsync();
        Assert.Equal(0, budget.LeasedBytes);
    }

    [Fact]
    public async Task Handoff_RemainderBudgetUsesExactTailAfterPrefix()
    {
        const int segmentSize = 100;
        const int segmentCount = 20;
        const long prefix = 90;
        const long readBudget = 25;
        var segments = Enumerable.Range(0, segmentCount)
            .ToDictionary(
                i => $"seg-{i}",
                i => Enumerable.Repeat((byte)(i + 1), segmentSize).ToArray());
        var client = new FakeNntpClient(segments, useCachedYencStreams: true);
        var exactSizes = Enumerable.Repeat((long)segmentSize, segmentCount).ToArray();
        var budget = new InFlightArticleBudget(segmentSize * 16);

        await using var stream = await MultiSegmentStream.CreatePositionedFirstSegmentHybridAsync(
            new MultiSegmentStream.FirstSegmentHybridOptions(
                SegmentIds: segments.Keys.ToArray().AsMemory(),
                UsenetClient: client,
                ArticleBufferSize: 8,
                EstimatedSegmentSize: segmentSize,
                FailFastOnFirstSegment: false,
                UsePipelinedBodyRequests: true,
                CancellationToken: CancellationToken.None,
                FileName: "handoff-budget.bin",
                ReadBudget: readBudget,
                SegmentFallbacks: null,
                ExactSegmentSizes: exactSizes,
                InFlightArticleBudget: budget,
                UseContainerAwareFill: false,
                FirstSegmentFileOffset: 0,
                BodyPipelineBatchWidth: 4,
                KnownCorruptSegmentIds: null,
                KnownMissingSegmentIndices: null),
            firstSegmentPrefixBytes: prefix);

        var buffer = new byte[readBudget];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read));
            if (n == 0) break;
            read += n;
        }

        Assert.Equal(readBudget, read);
        Assert.True(
            client.RequestedSegmentIds.Count <= 5,
            $"remainder budget 15 should not fetch the whole file; got {client.RequestedSegmentIds.Count} unique segments, BODY={client.BodyRequestCount}");
        await stream.DisposeAsync();
        Assert.Equal(0, budget.LeasedBytes);
    }

    [Fact]
    public async Task Handoff_RangeSatisfiedByHeadLeasesNoBufferedBytes()
    {
        const int segmentSize = 100;
        const int segmentCount = 4;
        var segments = Enumerable.Range(0, segmentCount)
            .ToDictionary(
                i => $"seg-{i}",
                i => Enumerable.Repeat((byte)(i + 1), segmentSize).ToArray());
        var client = new FakeNntpClient(segments, useCachedYencStreams: true);
        var exactSizes = Enumerable.Repeat((long)segmentSize, segmentCount).ToArray();
        var budget = new InFlightArticleBudget(segmentSize * 16);

        await using var stream = MultiSegmentStream.CreateFirstSegmentHybrid(
            segments.Keys.ToArray().AsMemory(),
            client,
            articleBufferSize: 8,
            estimatedSegmentSize: segmentSize,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: true,
            CancellationToken.None,
            fileName: "handoff-head-only.bin",
            readBudget: segmentSize,
            exactSegmentSizes: exactSizes,
            inFlightArticleBudget: budget);

        var buffer = new byte[segmentSize];
        Assert.Equal(segmentSize, await stream.ReadAsync(buffer));
        await Task.Delay(50);
        Assert.Equal(0, client.BatchRequestCount);
        Assert.Equal(0, budget.LeasedBytes);
    }

    [Fact]
    public async Task Handoff_CancelMidHeadReleasesPipeAndRemainderLeases()
    {
        const int segmentSize = 8;
        var budget = new InFlightArticleBudget(segmentSize * 64);
        var client = new ControlledBatchNntpClient(segmentCount: 8, segmentSize);
        client.ReleaseAllUpTo(7);
        using var cts = new CancellationTokenSource();
        var stream = MultiSegmentStream.CreateFirstSegmentHybrid(
            client.SegmentIds.AsMemory(),
            client,
            articleBufferSize: 8,
            estimatedSegmentSize: segmentSize,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: true,
            cts.Token,
            fileName: "handoff-cancel.bin",
            exactSegmentSizes: Enumerable.Repeat((long)segmentSize, 8).ToArray(),
            inFlightArticleBudget: budget,
            bodyPipelineBatchWidth: 4);

        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        await client.WaitUntilAsync(() => client.BatchIssueCount > 0, TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await stream.DisposeAsync();
        Assert.Equal(0, budget.LeasedBytes);
        Assert.Equal(0, client.ActiveBodyStreams);
    }

    [Fact]
    public async Task Handoff_DisposeWhileRemainderLeaseWaitsRemovesWaiter()
    {
        const int segmentSize = 50;
        var cap = segmentSize;
        var budget = new InFlightArticleBudget(cap);
        var held = await budget.LeaseAsync(cap, CancellationToken.None);
        var client = new ControlledBatchNntpClient(segmentCount: 8, segmentSize);
        client.ReleaseAllUpTo(7);
        var stream = MultiSegmentStream.CreateFirstSegmentHybrid(
            client.SegmentIds.AsMemory(),
            client,
            articleBufferSize: 8,
            estimatedSegmentSize: segmentSize,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: true,
            CancellationToken.None,
            fileName: "handoff-waiter.bin",
            exactSegmentSizes: Enumerable.Repeat((long)segmentSize, 8).ToArray(),
            inFlightArticleBudget: budget,
            bodyPipelineBatchWidth: 4);

        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!budget.HasWaiters && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(budget.HasWaiters);

        await stream.DisposeAsync();
        Assert.False(budget.HasWaiters);
        held.Dispose();
        Assert.Equal(0, budget.LeasedBytes);
    }

    [Fact]
    public async Task Handoff_AsyncDisposeJoinsQueuedAndOrphanedRemainderWork()
    {
        const int segmentSize = 20_000;
        var budget = new InFlightArticleBudget(segmentSize * 16);
        var segments = Enumerable.Range(0, 20)
            .ToDictionary(
                i => $"seg-{i}",
                _ => Enumerable.Repeat((byte)3, segmentSize).ToArray());
        var client = new FakeNntpClient(segments, useCachedYencStreams: true);
        var exactSizes = Enumerable.Repeat((long)segmentSize, 20).ToArray();

        var stream = MultiSegmentStream.CreateFirstSegmentHybrid(
            segments.Keys.ToArray().AsMemory(),
            client,
            articleBufferSize: 8,
            estimatedSegmentSize: segmentSize,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: true,
            CancellationToken.None,
            fileName: "handoff-join.bin",
            exactSegmentSizes: exactSizes,
            inFlightArticleBudget: budget);

        var buffer = new byte[1024];
        _ = await stream.ReadAsync(buffer);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (budget.LeasedBytes == 0 && client.BatchRequestCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(budget.LeasedBytes > 0 || client.BatchRequestCount > 0);

        await stream.DisposeAsync();
        Assert.Equal(0, budget.LeasedBytes);
    }

    [Fact]
    public async Task RemoveHeadWaiter_WakesNextWaiterWhenCapacityAvailable()
    {
        const long cap = 1000;
        var budget = new InFlightArticleBudget(cap);
        var held = await budget.LeaseAsync(cap, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var head = budget.LeaseAsync(cap, cts.Token).AsTask();
        for (var i = 0; i < 50 && budget.ThrottleEvents == 0; i++)
            await Task.Delay(10);
        Assert.True(budget.ThrottleEvents > 0);

        var next = budget.LeaseAsync(cap, CancellationToken.None).AsTask();
        for (var i = 0; i < 50 && budget.ThrottleEvents < 2; i++)
            await Task.Delay(10);

        // Free capacity and cancel the head before/while it observes the wake so
        // RemoveWaiter must signal the new FIFO head.
        held.Dispose();
        await cts.CancelAsync();
        try
        {
            await head;
        }
        catch (OperationCanceledException)
        {
            // Expected when the head loses the race to TryLease.
        }

        if (head.IsCompletedSuccessfully)
            (await head).Dispose();

        using var lease = await next.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(cap, budget.LeasedBytes);
    }

    [Fact]
    public async Task PersistentCorruption_ZeroFillsAndContinues()
    {
        const int segmentSize = 50;
        var budget = new InFlightArticleBudget(segmentSize * 8);
        var segments = new Dictionary<string, byte[]>
        {
            ["a"] = Enumerable.Repeat((byte)1, segmentSize).ToArray(),
            ["b"] = Enumerable.Repeat((byte)2, segmentSize).ToArray(),
            ["c"] = Enumerable.Repeat((byte)3, segmentSize).ToArray(),
        };
        var client = new FakeNntpClient(
            segments,
            useCachedYencStreams: true,
            decodedStreamFactory: (key, bytes) => key == "b"
                ? new ThrowingCorruptStream("b")
                : new MemoryStream(bytes, writable: false));

        await using var stream = MultiSegmentStream.Create(
            segments.Keys.ToArray().AsMemory(),
            client,
            articleBufferSize: 4,
            estimatedSegmentSize: segmentSize,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            CancellationToken.None,
            fileName: "corrupt.bin",
            exactSegmentSizes: new long[] { segmentSize, segmentSize, segmentSize },
            inFlightArticleBudget: budget);

        var buffer = new byte[segmentSize];
        Assert.Equal(segmentSize, await stream.ReadAsync(buffer));
        Assert.All(buffer, b => Assert.Equal((byte)1, b));

        Assert.Equal(segmentSize, await stream.ReadAsync(buffer));
        Assert.All(buffer, b => Assert.Equal((byte)0, b));

        Assert.Equal(segmentSize, await stream.ReadAsync(buffer));
        Assert.All(buffer, b => Assert.Equal((byte)3, b));

        await stream.DisposeAsync();
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

    [Fact]
    public async Task DisposeAsync_JoinsPipelinedBatchOrphanedDisposals()
    {
        // Pipelined prefetch takes a lease per batch segment before writing each to the
        // channel. When the channel writer is completed during teardown, the producer's
        // WriteAsync throws and it disposes the unwritten segment tasks out-of-band.
        // DisposeAsync must join those disposals so their leases release before it returns
        // (the #840 scrub wedge: leases held after NNTP goes idle).
        const int segmentSize = 20_000;
        var budget = new InFlightArticleBudget(segmentSize * 16);
        var segments = Enumerable.Range(0, 20)
            .ToDictionary(
                i => $"seg-{i}",
                _ => Enumerable.Repeat((byte)9, segmentSize).ToArray());
        var client = new FakeNntpClient(segments, useCachedYencStreams: true);

        using var cts = new CancellationTokenSource();
        var stream = MultiSegmentStream.Create(
            segments.Keys.ToArray().AsMemory(),
            client,
            articleBufferSize: 8,
            estimatedSegmentSize: segmentSize,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: true,
            cts.Token,
            fileName: "orphan-pipelined.bin",
            readBudget: null,
            inFlightArticleBudget: budget);

        // Start prefetch, then cancel while batches are in flight so the producer hits
        // the orphan-dispose path for unwritten segment tasks.
        var buffer = new byte[1024];
        _ = await stream.ReadAsync(buffer, CancellationToken.None);
        Assert.True(budget.LeasedBytes > 0);

        cts.Cancel();
        await stream.DisposeAsync();

        Assert.Equal(0, budget.LeasedBytes);
    }

    [Fact]
    public async Task PipeAccounting_TinyBudget_CompletesFullReadWithoutDeadlock()
    {
        // FakeNntpClient cannot emit real UsenetSharp pipe deltas. Wrap each body in a
        // stream that charges/releases the budget the same way DecodedBodyReadStream does,
        // so a tiny cap plus pipe occupancy cannot deadlock a waiter that holds no pipe.
        const int segmentCount = 6;
        const int segmentSize = 2_000;
        var budget = new InFlightArticleBudget(segmentSize + 500);
        var keys = Enumerable.Range(0, segmentCount).Select(i => $"seg-{i}").ToArray();
        var segments = keys.ToDictionary(
            key => key,
            key => Enumerable.Repeat((byte)(key[^1] - '0'), segmentSize).ToArray());
        var client = new FakeNntpClient(
            segments,
            useCachedYencStreams: true,
            decodedStreamFactory: (_, bytes) => new PipeDeltaReportingStream(bytes, budget));

        await using var stream = MultiSegmentStream.Create(
            keys.AsMemory(),
            client,
            articleBufferSize: 4,
            estimatedSegmentSize: segmentSize,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            CancellationToken.None,
            fileName: "pipe-budget.bin",
            readBudget: null,
            exactSegmentSizes: Enumerable.Repeat((long)segmentSize, segmentCount).ToArray(),
            inFlightArticleBudget: budget);

        using var output = new MemoryStream();
        await stream.CopyToAsync(output).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(keys.SelectMany(key => segments[key]).ToArray(), output.ToArray());
        Assert.Equal(0, budget.LeasedBytes);
    }

    [Fact]
    public async Task PipeAccounting_TinyBudget_CompletesRetryWithoutDeadlock()
    {
        // Window of 1 so the producer cannot lease a later segment while the ordered
        // consumer is blocked on this retry — the pre-existing retry-under-saturation
        // window. Pipe charges still apply; the retry waiter holds no open pipe.
        const int segmentCount = 4;
        const int segmentSize = 2_000;
        var budget = new InFlightArticleBudget(segmentSize + 500);
        var keys = Enumerable.Range(0, segmentCount).Select(i => $"seg-{i}").ToArray();
        var segments = keys.ToDictionary(
            key => key,
            key => Enumerable.Repeat((byte)(key[^1] - '0'), segmentSize).ToArray());
        var retryAttempts = new int[1];
        var client = new FakeNntpClient(
            segments,
            useCachedYencStreams: true,
            decodedStreamFactory: (key, bytes) =>
            {
                var failOnce = key == "seg-1" && Interlocked.Increment(ref retryAttempts[0]) == 1;
                return new PipeDeltaReportingStream(bytes, budget, failOnce);
            });

        await using var stream = MultiSegmentStream.Create(
            keys.AsMemory(),
            client,
            articleBufferSize: 1,
            estimatedSegmentSize: segmentSize,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            CancellationToken.None,
            fileName: "pipe-budget-retry.bin",
            readBudget: null,
            exactSegmentSizes: Enumerable.Repeat((long)segmentSize, segmentCount).ToArray(),
            inFlightArticleBudget: budget);

        using var output = new MemoryStream();
        await stream.CopyToAsync(output).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(keys.SelectMany(key => segments[key]).ToArray(), output.ToArray());
        Assert.Equal(0, budget.LeasedBytes);
        Assert.True(retryAttempts[0] >= 2);
        Assert.True(client.BodyRequestCounts.GetValueOrDefault("seg-1") >= 2);
    }

    [Fact]
    public async Task PipeAccounting_TinyBudget_PrefetchWindowRetry_CompletesWithoutDeadlock()
    {
        // Repro from issue 1043: budget ≈ 1.25 segments and a prefetch window of 4.
        // Dispose-then-re-lease queued the retry behind already-enqueued waiters while
        // drained-but-unread buffers held the cap. Retaining the original lease across
        // the retry must complete the copy.
        const int segmentCount = 4;
        const int segmentSize = 2_000;
        var budget = new InFlightArticleBudget(segmentSize + 500);
        var keys = Enumerable.Range(0, segmentCount).Select(i => $"seg-{i}").ToArray();
        var segments = keys.ToDictionary(
            key => key,
            key => Enumerable.Repeat((byte)(key[^1] - '0'), segmentSize).ToArray());
        var retryAttempts = new int[1];
        var client = new FakeNntpClient(
            segments,
            useCachedYencStreams: true,
            decodedStreamFactory: (key, bytes) =>
            {
                var failOnce = key == "seg-1" && Interlocked.Increment(ref retryAttempts[0]) == 1;
                return new PipeDeltaReportingStream(bytes, budget, failOnce);
            });

        await using var stream = MultiSegmentStream.Create(
            keys.AsMemory(),
            client,
            articleBufferSize: 4,
            estimatedSegmentSize: segmentSize,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            CancellationToken.None,
            fileName: "pipe-budget-retry-pipelined.bin",
            readBudget: null,
            exactSegmentSizes: Enumerable.Repeat((long)segmentSize, segmentCount).ToArray(),
            inFlightArticleBudget: budget);

        using var output = new MemoryStream();
        await stream.CopyToAsync(output).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(keys.SelectMany(key => segments[key]).ToArray(), output.ToArray());
        Assert.Equal(0, budget.LeasedBytes);
        Assert.True(retryAttempts[0] >= 2);
        Assert.True(client.BodyRequestCounts.GetValueOrDefault("seg-1") >= 2);
    }

    [Fact]
    public async Task PipeAccounting_TinyBudget_PipelinedPrefetchWindowRetry_CompletesWithoutDeadlock()
    {
        // Same retry-under-saturation shape as the non-pipelined test, on the
        // DownloadBatchSegment → TryRescue path. Batch width 1 so the producer does
        // not try to lease a whole window before the consumer can drain — that is a
        // separate admission issue, not the dispose-then-re-lease stall.
        const int segmentCount = 4;
        const int segmentSize = 2_000;
        var budget = new InFlightArticleBudget(segmentSize + 500);
        var keys = Enumerable.Range(0, segmentCount).Select(i => $"seg-{i}").ToArray();
        var segments = keys.ToDictionary(
            key => key,
            key => Enumerable.Repeat((byte)(key[^1] - '0'), segmentSize).ToArray());
        var retryAttempts = new int[1];
        var client = new FakeNntpClient(
            segments,
            useCachedYencStreams: true,
            decodedStreamFactory: (key, bytes) =>
            {
                // FakeNntpClient materializes the whole pipelined batch up front. Charging
                // every body pipe before any lease is released would hang this tiny cap
                // for reasons unrelated to production BODY occupancy.
                if (key == "seg-1" && Interlocked.Increment(ref retryAttempts[0]) == 1)
                    throw new IOException("simulated transient body failure");
                return new MemoryStream(bytes, writable: false);
            });

        await using var stream = MultiSegmentStream.Create(
            keys.AsMemory(),
            client,
            articleBufferSize: 4,
            estimatedSegmentSize: segmentSize,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: true,
            CancellationToken.None,
            fileName: "pipe-budget-retry-pipelined.bin",
            readBudget: null,
            exactSegmentSizes: Enumerable.Repeat((long)segmentSize, segmentCount).ToArray(),
            inFlightArticleBudget: budget,
            bodyPipelineBatchWidth: 1);

        using var output = new MemoryStream();
        await stream.CopyToAsync(output).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(keys.SelectMany(key => segments[key]).ToArray(), output.ToArray());
        Assert.Equal(0, budget.LeasedBytes);
        Assert.True(retryAttempts[0] >= 2);
        Assert.True(client.BodyRequestCounts.GetValueOrDefault("seg-1") >= 2);
    }

    private sealed class ThrowingCorruptStream(string segmentId) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new UsenetCorruptArticleException(
                segmentId, "fake-provider", new InvalidDataException("bad crc"));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(
                new UsenetCorruptArticleException(
                    segmentId, "fake-provider", new InvalidDataException("bad crc")));

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingDisposeMemoryStream(byte[] bytes)
        : MemoryStream(bytes, writable: false)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
                throw new IOException("Simulated source disposal failure.");
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

    /// <summary>
    /// Simulates UsenetSharp pipe occupancy: charge the full body on production, release
    /// as the consumer reads or on dispose so deltas zero-balance.
    /// </summary>
    private sealed class PipeDeltaReportingStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly InFlightArticleBudget _budget;
        private readonly bool _failOnce;
        private long _remaining;
        private int _failed;
        private int _completed;

        public PipeDeltaReportingStream(byte[] bytes, InFlightArticleBudget budget, bool failOnce = false)
        {
            _inner = new MemoryStream(bytes, writable: false);
            _budget = budget;
            _failOnce = failOnce;
            _remaining = bytes.Length;
            if (_remaining > 0)
                _budget.AccountBufferedPipeBytes(_remaining);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ThrowIfFailOnce();
            var read = _inner.Read(buffer, offset, count);
            Consume(read);
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfFailOnce();
            var read = _inner.Read(buffer.Span);
            Consume(read);
            return ValueTask.FromResult(read);
        }

        private void ThrowIfFailOnce()
        {
            if (_failOnce && Interlocked.Exchange(ref _failed, 1) == 0)
                throw new IOException("simulated transient body failure");
        }

        private void Consume(int count)
        {
            if (count <= 0) return;
            var release = Math.Min(_remaining, count);
            if (release <= 0) return;
            _remaining -= release;
            _budget.AccountBufferedPipeBytes(-release);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _completed, 1) == 0 && _remaining > 0)
            {
                _budget.AccountBufferedPipeBytes(-_remaining);
                _remaining = 0;
            }

            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
