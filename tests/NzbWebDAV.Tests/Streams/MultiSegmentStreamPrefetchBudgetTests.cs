using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;

namespace NzbWebDAV.Tests.Streams;

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
}
