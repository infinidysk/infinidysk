using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Streams;

public sealed class MultiSegmentStreamStripingTests
{
    private const int SegmentSize = 64;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task StripeHint_InterleavesBatchMembershipAcrossConnections()
    {
        var client = new ControlledBatchNntpClient(16, SegmentSize, uniqueBytes: true);
        client.ReleaseAllUpTo(15);
        using var cts = new CancellationTokenSource();
        using var hint = cts.Token.SetContext(new StreamingStripeContext { StripeCount = 4 });
        await using var stream = CreateStream(client, cts.Token);

        var bytes = await ReadAllAsync(stream);

        Assert.Equal(client.ExpectedConcatenation, bytes);
        Assert.Equal(
            new[] { new[] { 0, 4, 8, 12 }, new[] { 1, 5, 9, 13 }, new[] { 2, 6, 10, 14 }, new[] { 3, 7, 11, 15 } },
            client.ObservedBatchIndexes);
    }

    [Fact]
    public async Task NoStripeHint_KeepsContiguousBatches()
    {
        var client = new ControlledBatchNntpClient(8, SegmentSize, uniqueBytes: true);
        client.ReleaseAllUpTo(7);
        await using var stream = CreateStream(client, CancellationToken.None);

        var bytes = await ReadAllAsync(stream);

        Assert.Equal(client.ExpectedConcatenation, bytes);
        Assert.Equal(new[] { new[] { 0, 1, 2, 3 }, new[] { 4, 5, 6, 7 } }, client.ObservedBatchIndexes);
    }

    [Fact]
    public async Task PartialGroup_SpreadsAcrossEveryStripe()
    {
        var client = new ControlledBatchNntpClient(10, SegmentSize, uniqueBytes: true);
        client.ReleaseAllUpTo(9);
        using var cts = new CancellationTokenSource();
        using var hint = cts.Token.SetContext(new StreamingStripeContext { StripeCount = 4 });
        await using var stream = CreateStream(client, cts.Token);

        var bytes = await ReadAllAsync(stream);

        Assert.Equal(client.ExpectedConcatenation, bytes);
        Assert.Equal(
            new[] { new[] { 0, 4, 8 }, new[] { 1, 5, 9 }, new[] { 2, 6 }, new[] { 3, 7 } },
            client.ObservedBatchIndexes);
    }

    [Fact]
    public async Task FirstResponseOfEveryBatch_IsEnoughToReadTheNextStripeSegments()
    {
        // Each connection answers its batch in order, so only a batch's first response is
        // available early. Contiguous batches would expose just segment 0 to the reader.
        var client = new ControlledBatchNntpClient(16, SegmentSize, uniqueBytes: true);
        using var cts = new CancellationTokenSource();
        using var hint = cts.Token.SetContext(new StreamingStripeContext { StripeCount = 4 });
        await using var stream = CreateStream(client, cts.Token);
        await client.WaitUntilAsync(() => client.BatchIssueCount == 4, Timeout);
        foreach (var batch in client.ObservedBatchIndexes)
            client.ReleaseSegment(batch[0]);

        var head = new byte[4 * SegmentSize];
        await stream.ReadExactlyAsync(head).AsTask().WaitAsync(Timeout);

        Assert.Equal(client.ExpectedConcatenation.AsSpan(0, head.Length).ToArray(), head);
        client.ReleaseAllUpTo(15);
        var rest = await ReadAllAsync(stream);
        Assert.Equal(client.ExpectedConcatenation.AsSpan(head.Length).ToArray(), rest);
    }

    [Fact]
    public async Task GroupThatDoesNotFitTheBudget_FallsBackToContiguousBatches()
    {
        var client = new ControlledBatchNntpClient(16, SegmentSize, uniqueBytes: true);
        client.ReleaseAllUpTo(15);
        var budget = new InFlightArticleBudget(6 * SegmentSize);
        using var cts = new CancellationTokenSource();
        using var hint = cts.Token.SetContext(new StreamingStripeContext { StripeCount = 4 });
        var stream = CreateStream(client, cts.Token, budget);

        var bytes = await ReadAllAsync(stream);
        await stream.DisposeAsync();

        Assert.Equal(client.ExpectedConcatenation, bytes);
        Assert.Equal(new[] { 0, 1, 2, 3 }, client.ObservedBatchIndexes[0]);
        Assert.Equal(0, budget.LeasedBytes);
    }

    [Fact]
    public async Task DisposeWhileStripedResponsesAreOutstanding_ReleasesEveryLease()
    {
        var client = new ControlledBatchNntpClient(16, SegmentSize, uniqueBytes: true);
        var budget = new InFlightArticleBudget(1024 * SegmentSize);
        using var cts = new CancellationTokenSource();
        using var hint = cts.Token.SetContext(new StreamingStripeContext { StripeCount = 4 });
        var stream = CreateStream(client, cts.Token, budget);
        await client.WaitUntilAsync(() => client.BatchIssueCount == 4, Timeout);
        client.ReleaseSegment(0);
        client.ReleaseSegment(1);

        await stream.DisposeAsync().AsTask().WaitAsync(Timeout);

        await client.WaitUntilAsync(() => budget.LeasedBytes == 0, Timeout);
        await client.WaitUntilAsync(() => client.CallbackCount == client.BatchIssueCount, Timeout);
    }

    [Fact]
    public async Task SinglePermit_StillDeliversEveryStripeInOrder()
    {
        // The stripe hint is not a reservation; one live connection must still make progress.
        var client = new ControlledBatchNntpClient(16, SegmentSize, uniqueBytes: true)
        {
            SharedPermit = new SemaphoreSlim(1, 1),
        };
        client.ReleaseAllUpTo(15);
        using var cts = new CancellationTokenSource();
        using var hint = cts.Token.SetContext(new StreamingStripeContext { StripeCount = 4 });
        await using var stream = CreateStream(client, cts.Token);

        var bytes = await ReadAllAsync(stream).WaitAsync(Timeout);

        Assert.Equal(client.ExpectedConcatenation, bytes);
        Assert.Equal(1, client.MaxActiveBatches);
    }

    [Fact]
    public async Task CapacityBelowStripes_NextSegmentNeedsOnlyItsOwnRelease()
    {
        // Four stripes planned, one connection held. Once the stream sees a stripe admitted
        // only after its own earlier stripe finished, later batches must follow file order:
        // a striped [16,20,24,28] would hold segment 17 behind segments 20, 24 and 28.
        var client = new ControlledBatchNntpClient(32, SegmentSize, uniqueBytes: true)
        {
            SharedPermit = new SemaphoreSlim(1, 1),
        };
        using var cts = new CancellationTokenSource();
        using var hint = cts.Token.SetContext(new StreamingStripeContext { StripeCount = 4 });
        await using var stream = CreateStream(client, cts.Token);
        client.ReleaseAllUpTo(15);
        var head = new byte[16 * SegmentSize];
        await stream.ReadExactlyAsync(head).AsTask().WaitAsync(Timeout);

        var next = new byte[SegmentSize];
        for (var index = 16; index < 24; index++)
        {
            client.ReleaseSegment(index);
            await stream.ReadExactlyAsync(next).AsTask().WaitAsync(Timeout);
            Assert.Equal(client.ExpectedConcatenation.AsSpan(index * SegmentSize, SegmentSize).ToArray(), next);
        }

        Assert.Equal(
            new[] { new[] { 0, 4, 8, 12 }, new[] { 1, 5, 9, 13 }, new[] { 2, 3, 6, 7 }, new[] { 10, 11, 14, 15 } },
            client.ObservedBatchIndexes.Take(4));
        client.ReleaseAllUpTo(31);
        await ReadAllAsync(stream);
    }

    [Fact]
    public async Task CachedPrefix_CapacityBelowStripes_NextSegmentNeedsOnlyItsOwnRelease()
    {
        // Same capacity loss, but each group's first stripe starts with a cached article, so
        // every batch returns before its misses are admitted. The stream must still observe
        // remote admission and narrow, or [20,24,28] holds segment 21 behind 24 and 28.
        var client = new ControlledBatchNntpClient(32, SegmentSize, uniqueBytes: true)
        {
            SharedPermit = new SemaphoreSlim(1, 1),
            LocalSegments = new HashSet<int> { 0, 1, 2, 3, 16, 17, 18, 19 },
        };
        using var cts = new CancellationTokenSource();
        using var hint = cts.Token.SetContext(new StreamingStripeContext { StripeCount = 4 });
        await using var stream = CreateStream(client, cts.Token);

        // The whole cached prefix is readable before any remote batch drains, as on main.
        var cached = new byte[4 * SegmentSize];
        await stream.ReadExactlyAsync(cached).AsTask().WaitAsync(Timeout);
        Assert.Equal(client.ExpectedConcatenation.AsSpan(0, cached.Length).ToArray(), cached);

        client.ReleaseAllUpTo(15);
        var head = new byte[12 * SegmentSize];
        await stream.ReadExactlyAsync(head).AsTask().WaitAsync(Timeout);

        var next = new byte[SegmentSize];
        for (var index = 16; index < 24; index++)
        {
            client.ReleaseSegment(index);
            await stream.ReadExactlyAsync(next).AsTask().WaitAsync(Timeout);
            Assert.Equal(client.ExpectedConcatenation.AsSpan(index * SegmentSize, SegmentSize).ToArray(), next);
        }

        client.ReleaseAllUpTo(31);
        await ReadAllAsync(stream);
    }

    [Fact]
    public async Task CapacityThatReturns_InterleavesAgain()
    {
        var permit = new SemaphoreSlim(1, 4);
        var client = new ControlledBatchNntpClient(32, SegmentSize, uniqueBytes: true) { SharedPermit = permit };
        using var cts = new CancellationTokenSource();
        using var hint = cts.Token.SetContext(new StreamingStripeContext { StripeCount = 4 });
        await using var stream = CreateStream(client, cts.Token);
        client.ReleaseAllUpTo(15);
        await client.WaitUntilAsync(() => client.BatchIssueCount == 5, Timeout);

        // [16..19] still holds the only connection; three more become free.
        permit.Release(3);
        await client.WaitUntilAsync(() => client.BatchIssueCount == 8, Timeout);
        client.ReleaseAllUpTo(31);
        var bytes = await ReadAllAsync(stream);

        Assert.Equal(client.ExpectedConcatenation, bytes);
        Assert.Equal(
            new[] { new[] { 16, 17, 18, 19 }, new[] { 20, 21, 22, 23 }, new[] { 24, 26, 28, 30 }, new[] { 25, 27, 29, 31 } },
            client.ObservedBatchIndexes.Skip(4));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DemandIdledWhileStripeAwaitsAdmission_IssuesNoFrontierProbeAndParksTheGroup(bool resume)
    {
        // The first stripe returns with its misses awaiting a connection. Detaching then must
        // stop the cached frontier probes and park the group's remaining stripes.
        var permit = new SemaphoreSlim(1, 1);
        await permit.WaitAsync();
        var gate = new SharedStreamDemandGate();
        var requests = new List<int[]>();
        var requestsAtIdle = -1;
        var client = new ControlledBatchNntpClient(16, SegmentSize, uniqueBytes: true)
        {
            SharedPermit = permit,
            LocalSegments = new HashSet<int> { 0, 1, 2, 3 },
        };
        client.OnBatchRequested = indexes =>
        {
            lock (requests)
            {
                requests.Add(indexes);
                if (requestsAtIdle < 0 && indexes.Contains(4))
                {
                    requestsAtIdle = requests.Count;
                    gate.SetIdle();
                }
            }
        };
        var budget = new InFlightArticleBudget(1024 * SegmentSize);
        using var cts = new CancellationTokenSource();
        using var hint = cts.Token.SetContext(new StreamingStripeContext { StripeCount = 4 });
        using var demand = cts.Token.SetContext(gate);
        var stream = CreateStream(client, cts.Token, budget);

        await client.WaitUntilAsync(() => gate.IsIdle, Timeout);
        await Task.Delay(100);
        permit.Release();
        await client.WaitUntilAsync(() => client.BatchIssueCount >= 1, Timeout);
        await Task.Delay(100);
        lock (requests)
        {
            Assert.Equal(new[] { 0, 4, 8, 12 }, requests[requestsAtIdle - 1]);
            Assert.Equal(requestsAtIdle, requests.Count);
        }

        if (resume)
        {
            gate.SetDemand();
            client.ReleaseAllUpTo(15);
            Assert.Equal(client.ExpectedConcatenation, await ReadAllAsync(stream));
        }

        await stream.DisposeAsync().AsTask().WaitAsync(Timeout);
        await client.WaitUntilAsync(() => budget.LeasedBytes == 0, Timeout);
    }

    [Theory]
    [InlineData(10, 4, 4, new[] { 0, 4, 8 })]
    [InlineData(12, 1, 4, new[] { 0, 1, 2, 3 })]
    [InlineData(3, 4, 4, new[] { 0 })]
    [InlineData(16, 2, 4, new[] { 0, 2, 4, 6 })]
    public void TakeStride_TakesEveryStrideSlotUpToWidth(int count, int stride, int width, int[] expected)
    {
        var remaining = Enumerable.Range(0, count).ToList();

        var slots = MultiSegmentStream.TakeStride(remaining, stride, width);

        Assert.Equal(expected, slots);
        Assert.Equal(Enumerable.Range(0, count).Except(expected), remaining);
    }

    [Fact]
    public async Task MissingArticleInAStripe_ZeroFillsOnlyThatSegment()
    {
        var client = new ControlledBatchNntpClient(16, SegmentSize, uniqueBytes: true);
        client.FailSegment(4, new UsenetArticleNotFoundException("seg-4"));
        client.ReleaseAllUpTo(15);
        using var cts = new CancellationTokenSource();
        using var hint = cts.Token.SetContext(new StreamingStripeContext { StripeCount = 4 });
        await using var stream = CreateStream(client, cts.Token);

        var bytes = await ReadAllAsync(stream);

        var expected = client.ExpectedConcatenation.ToArray();
        Array.Clear(expected, 4 * SegmentSize, SegmentSize);
        Assert.Equal(expected, bytes);
        Assert.Equal(new[] { 0, 4, 8, 12 }, client.ObservedBatchIndexes[0]);
    }

    [Fact]
    public async Task StalledStripe_HoldsOnlyItsOwnSegmentsAndIsRescuedWhenItsConnectionDrops()
    {
        // Batch [0,4,8,12] stalls after segment 0. The other stripes keep delivering, so the
        // reader is held only at segment 4; when the connection drops, 4, 8 and 12 are
        // re-requested individually rather than waiting for the reader to reach them.
        var client = new ControlledBatchNntpClient(16, SegmentSize, uniqueBytes: true);
        using var cts = new CancellationTokenSource();
        using var hint = cts.Token.SetContext(new StreamingStripeContext { StripeCount = 4 });
        await using var stream = CreateStream(client, cts.Token);
        foreach (var index in Enumerable.Range(0, 16).Except([4, 8, 12]))
            client.ReleaseSegment(index);

        var head = new byte[4 * SegmentSize];
        await stream.ReadExactlyAsync(head).AsTask().WaitAsync(Timeout);
        Assert.Equal(client.ExpectedConcatenation.AsSpan(0, head.Length).ToArray(), head);
        var firstOfFifth = new byte[1];
        var blocked = stream.ReadAsync(firstOfFifth).AsTask();
        await Task.Delay(100);
        Assert.False(blocked.IsCompleted);

        foreach (var index in new[] { 4, 8, 12 })
            client.FailSegment(index, new IOException("connection reset"));
        Assert.Equal(1, await blocked.WaitAsync(Timeout));
        var rest = await ReadAllAsync(stream);

        Assert.Equal(client.ExpectedConcatenation[4 * SegmentSize], firstOfFifth[0]);
        Assert.Equal(client.ExpectedConcatenation.AsSpan(4 * SegmentSize + 1).ToArray(), rest);
        Assert.Equal(3, client.IndividualRequestCount);
    }

    [Theory]
    [InlineData(SegmentSize)]
    [InlineData(SegmentSize - 1)]
    public async Task StripedRamp_StartsAtHalfTheWindowAndOpensTheFullWindowOnFirstRead(int estimatedSegmentSize)
    {
        // Eight stripes at width 4 fill a 32-segment window; the start admits half of it.
        var client = new ControlledBatchNntpClient(64, SegmentSize, uniqueBytes: true);
        using var cts = new CancellationTokenSource();
        using var hint = cts.Token.SetContext(new StreamingStripeContext { StripeCount = 8 });
        await using var stream = CreateStream(
            client, cts.Token, articleBufferSize: 8, estimatedSegmentSize: estimatedSegmentSize);

        Assert.Equal(16 * SegmentSize, stream.CurrentPrefetchByteCeiling);
        await client.WaitUntilAsync(() => client.BatchIssueCount == 8, Timeout);
        await Task.Delay(100);
        Assert.Equal(
            Enumerable.Range(0, 8).Select(index => new[] { index, index + 8 }),
            client.ObservedBatchIndexes);

        client.ReleaseAllUpTo(63);
        var head = new byte[4 * SegmentSize];
        await stream.ReadExactlyAsync(head).AsTask().WaitAsync(Timeout);
        Assert.Equal(32 * estimatedSegmentSize, stream.CurrentPrefetchByteCeiling);
        var middle = new byte[12 * SegmentSize];
        await stream.ReadExactlyAsync(middle).AsTask().WaitAsync(Timeout);
        Assert.Equal(32 * estimatedSegmentSize, stream.CurrentPrefetchByteCeiling);
        var rest = await ReadAllAsync(stream);

        Assert.Equal(32 * estimatedSegmentSize, stream.CurrentPrefetchByteCeiling);
        Assert.Equal(client.ExpectedConcatenation, head.Concat(middle).Concat(rest).ToArray());
    }

    [Theory]
    [InlineData(null, null, 40, 1)]
    [InlineData(null, 20, 40, 20)]
    [InlineData(null, 80, 40, 40)]
    [InlineData(8, 20, 40, 8)]
    [InlineData(20, 8, 40, 8)]
    [InlineData(6, null, 40, 6)]
    public void ResolveStripeCount_BoundsPlanByHintAndWindow(
        int? planTarget, int? hint, int articleBufferSize, int expected)
    {
        using var cts = new CancellationTokenSource();
        using var scope = hint is { } stripes
            ? cts.Token.SetContext(new StreamingStripeContext { StripeCount = stripes })
            : null;
        InitialBodyBatchPlan? plan = planTarget is { } target
            ? InitialBodyBatchPlan.Create(100, 100 * SegmentSize, target, 4, articleBufferSize)
            : null;

        Assert.Equal(expected, MultiSegmentStream.ResolveStripeCount(plan, articleBufferSize, cts.Token));
    }

    private static MultiSegmentStream CreateStream(
        ControlledBatchNntpClient client,
        CancellationToken cancellationToken,
        InFlightArticleBudget? budget = null,
        int articleBufferSize = 40,
        long estimatedSegmentSize = 0) =>
        (MultiSegmentStream)MultiSegmentStream.CreateWithInitialBatchPlan(
            client.SegmentIds.AsMemory(),
            client,
            articleBufferSize: articleBufferSize,
            estimatedSegmentSize: estimatedSegmentSize,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: true,
            cancellationToken,
            fileName: "striping.bin",
            exactSegmentSizes: Enumerable.Repeat((long)SegmentSize, client.SegmentIds.Length).ToArray(),
            // A private budget: the process-wide one is shared with parallel test classes,
            // and a failed group lease silently switches the stream to contiguous batches.
            inFlightArticleBudget: budget ?? new InFlightArticleBudget(1024 * SegmentSize),
            bodyPipelineBatchWidth: 4);

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var output = new MemoryStream();
        await stream.CopyToAsync(output).WaitAsync(Timeout);
        return output.ToArray();
    }
}
