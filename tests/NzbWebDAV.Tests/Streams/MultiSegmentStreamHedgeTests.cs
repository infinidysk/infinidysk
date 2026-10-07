using System.Diagnostics;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Streams;

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

    private sealed class StalledArticleClient(INntpClient inner, string stalledId) : WrappingNntpClient(inner)
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _stalled;
        private int _failedCallbacks;

        public int FailedCallbacks => Volatile.Read(ref _failedCallbacks);

        public void Release() => _gate.TrySetResult();

        public override async Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            var batch = await base.DecodedBodiesAsync(segmentIds, Track(onConnectionReadyAgain), cancellationToken)
                .ConfigureAwait(false);
            var responses = batch.Responses.ToArray();
            for (var index = 0; index < responses.Length; index++)
            {
                if (segmentIds[index].ToString() != stalledId || Interlocked.Exchange(ref _stalled, 1) != 0)
                    continue;
                var original = responses[index];
                responses[index] = _gate.Task.ContinueWith(_ => original, TaskScheduler.Default).Unwrap();
            }

            return batch with { Responses = responses };
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
