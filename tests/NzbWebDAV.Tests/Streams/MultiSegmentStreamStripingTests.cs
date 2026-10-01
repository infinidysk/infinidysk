using NzbWebDAV.Clients.Usenet.Contexts;
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
        InFlightArticleBudget? budget = null) =>
        (MultiSegmentStream)MultiSegmentStream.CreateWithInitialBatchPlan(
            client.SegmentIds.AsMemory(),
            client,
            articleBufferSize: 40,
            estimatedSegmentSize: 0,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: true,
            cancellationToken,
            fileName: "striping.bin",
            exactSegmentSizes: Enumerable.Repeat((long)SegmentSize, client.SegmentIds.Length).ToArray(),
            inFlightArticleBudget: budget,
            bodyPipelineBatchWidth: 4);

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var output = new MemoryStream();
        await stream.CopyToAsync(output).WaitAsync(Timeout);
        return output.ToArray();
    }
}
