using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Streams;

[Collection(nameof(GlobalLoggerCollection))]
public class MultiSegmentStreamDisposeTests
{
    // Dispose(bool) must call the protected base overload. The parameterless
    // Stream.Dispose() routes back through Close() into Dispose(bool) and recurses
    // until the stack overflows, which aborted the container mid-playback (#665).
    [Fact]
    public async Task SyncDispose_StartsCleanupWithoutRecursingThroughClose()
    {
        const int segmentSize = 20_000;
        var budget = new InFlightArticleBudget(segmentSize * 16);
        var segments = Enumerable.Range(0, 20)
            .ToDictionary(
                i => $"seg-{i}",
                _ => Enumerable.Repeat((byte)5, segmentSize).ToArray());
        var client = new FakeNntpClient(segments, useCachedYencStreams: true);

        var stream = MultiSegmentStream.Create(
            segments.Keys.ToArray().AsMemory(),
            client,
            articleBufferSize: 8,
            estimatedSegmentSize: segmentSize,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            CancellationToken.None,
            fileName: "sync-dispose.bin",
            readBudget: null,
            inFlightArticleBudget: budget);

        var buffer = new byte[1024];
        _ = await stream.ReadAsync(buffer);
        Assert.True(budget.LeasedBytes > 0);

        stream.Dispose();
        stream.Dispose();

        // Sync Dispose is non-blocking, so await the cleanup it started to prove it
        // ran rather than merely returning.
        await stream.DisposeAsync();
        Assert.Equal(0, budget.LeasedBytes);
    }
}
