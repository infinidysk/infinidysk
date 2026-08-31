using NzbWebDAV.Exceptions;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.TestUtils;
using static NzbWebDAV.Tests.Streams.NzbFileStreamExactIndexTestSupport;

namespace NzbWebDAV.Tests.Streams;

public class NzbFileStreamExactIndexLifecycleTests
{
    [Fact]
    public async Task ExactIndexedSeek_CorruptionAfterEmissionAbortsAndDisposesRemainder()
    {
        var budget = new InFlightArticleBudget(1024 * 1024);
        var staged = new StagedBodyStream(
            "f"u8.ToArray(),
            "g"u8.ToArray(),
            "hij"u8.ToArray(),
            readFailure: phase => phase == "tail"
                ? new UsenetCorruptArticleException(
                    "two", "provider-a", new InvalidDataException("CRC mismatch"))
                : null);
        var opens = 0;
        var client = CreateClient(decodedStreamFactory: (id, bytes) =>
        {
            if (id != "two")
                return new MemoryStream(bytes, writable: false);
            return Interlocked.Increment(ref opens) == 1
                ? staged
                : new MemoryStream(bytes, writable: false);
        });
        using var _ = SetBudget(LargeBudget);
        var stream = new NzbFileStream(
            SegmentIds,
            15,
            client,
            4,
            SegmentRanges,
            inFlightArticleBudget: budget,
            segmentByteRangesTrusted: true);
        stream.Seek(6, SeekOrigin.Begin);

        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        await client.FirstBatchRequested.Task.WaitAsync(WaitTimeout);

        staged.ReleaseTail();
        await Assert.ThrowsAsync<TransientSegmentExhaustionException>(async () =>
        {
            var buffer = new byte[8];
            while (await stream.ReadAsync(buffer) > 0)
            {
            }
        });

        await stream.DisposeAsync();
        Assert.Equal(0, budget.LeasedBytes);
        Assert.True(staged.AsyncDisposeCount + staged.SyncDisposeCount >= 1);
    }

    [Fact]
    public async Task ExactIndexedSeek_CancellationDuringPrefixDiscardStartsNoRemainder()
    {
        using var cts = new CancellationTokenSource();
        var client = CreateClient();
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client, articleBufferSize: 4);
        stream.Seek(6, SeekOrigin.Begin);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await stream.ReadAsync(new byte[1], cts.Token));
        Assert.Equal(0, client.BatchRequestCount);
    }

    [Fact]
    public async Task SeekThenDisposeAsync_ReleasesHeadRemainderAndArticleBudget()
    {
        var budget = new InFlightArticleBudget(1024 * 1024);
        var client = CreateClient();
        using var _ = SetBudget(LargeBudget);
        var stream = new NzbFileStream(
            SegmentIds,
            15,
            client,
            4,
            SegmentRanges,
            inFlightArticleBudget: budget,
            segmentByteRangesTrusted: true);
        stream.Seek(6, SeekOrigin.Begin);
        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        await client.FirstBatchRequested.Task.WaitAsync(WaitTimeout);

        await stream.DisposeAsync();
        Assert.Equal(0, budget.LeasedBytes);
    }

    [Fact]
    public async Task ExactIndexedSeek_CancellationAfterHandoffReleasesAllBodies()
    {
        using var cts = new CancellationTokenSource();
        var budget = new InFlightArticleBudget(1024 * 1024);
        var client = CreateClient();
        using var _ = SetBudget(LargeBudget);
        var stream = new NzbFileStream(
            SegmentIds,
            15,
            client,
            4,
            SegmentRanges,
            inFlightArticleBudget: budget,
            segmentByteRangesTrusted: true);
        stream.Seek(6, SeekOrigin.Begin);
        Assert.Equal(1, await stream.ReadAsync(new byte[1], cts.Token));
        await client.FirstBatchRequested.Task.WaitAsync(WaitTimeout);

        await cts.CancelAsync();
        await stream.DisposeAsync();
        Assert.Equal(0, budget.LeasedBytes);
    }

    [Fact]
    public async Task Seek_ReplacesStartedHandoffAndAwaitsCleanupBeforeNextBody()
    {
        var budget = new InFlightArticleBudget(1024 * 1024);
        var client = CreateClient();
        using var _ = SetBudget(LargeBudget);
        await using var stream = new NzbFileStream(
            SegmentIds,
            15,
            client,
            4,
            SegmentRanges,
            inFlightArticleBudget: budget,
            segmentByteRangesTrusted: true);
        stream.Seek(6, SeekOrigin.Begin);
        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        await client.FirstBatchRequested.Task.WaitAsync(WaitTimeout);

        stream.Seek(11, SeekOrigin.Begin);
        var buffer = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(buffer));
        Assert.Equal((byte)'l', buffer[0]);
        Assert.True(client.BodyRequestCounts.ContainsKey("three"));
        await stream.DisposeAsync();
        Assert.Equal(0, budget.LeasedBytes);
    }
}
