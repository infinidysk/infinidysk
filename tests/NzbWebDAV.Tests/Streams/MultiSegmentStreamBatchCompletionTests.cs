using System.Collections.Concurrent;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Streams;

public sealed class MultiSegmentStreamBatchCompletionTests
{
    [Fact]
    public async Task PipelinedBatch_CompletionIsObservedWithoutSerializingNextBatch()
    {
        const int segmentSize = 32;
        var segments = Enumerable.Range(0, 8)
            .ToDictionary(i => $"seg-{i}", _ => Enumerable.Repeat((byte)7, segmentSize).ToArray());
        var ranges = segments.Keys
            .Select((id, index) => KeyValuePair.Create(id, new LongRange(index * segmentSize, (index + 1L) * segmentSize)))
            .ToDictionary();
        var inner = new FakeNntpClient(segments, useCachedYencStreams: true, segmentRanges: ranges);
        using var delayed = new DelayedBatchCompletionClient(inner);
        Stream? stream = null;
        try
        {
            stream = MultiSegmentStream.Create(
                segments.Keys.ToArray().AsMemory(),
                delayed,
                articleBufferSize: 40,
                estimatedSegmentSize: segmentSize,
                failFastOnFirstSegment: false,
                usePipelinedBodyRequests: true,
                CancellationToken.None,
                fileName: "batch-completion.bin",
                bodyPipelineBatchWidth: 4);

            var buffer = new byte[segmentSize * 3];
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (delayed.Completions.Count < 2 && DateTime.UtcNow < deadline)
            {
                var read = await stream.ReadAsync(buffer);
                Assert.True(read > 0);
            }

            Assert.True(delayed.Completions.Count >= 2, "A second batch should issue while the first completion is still pending.");
            Assert.True(delayed.Completions.TryPeek(out var firstCompletion));
            Assert.False(firstCompletion.Task.IsCompleted);
        }
        finally
        {
            foreach (var gate in delayed.Completions)
                gate.TrySetResult();
            if (stream is not null)
                await stream.DisposeAsync();
        }
    }

    [Fact]
    public async Task PipelinedBatch_ResponseCountMismatch_DrainsResponsesAndAwaitsCompletion()
    {
        const int segmentSize = 8;
        var segments = new Dictionary<string, byte[]>
        {
            ["a"] = Enumerable.Repeat((byte)1, segmentSize).ToArray(),
            ["b"] = Enumerable.Repeat((byte)2, segmentSize).ToArray(),
            ["c"] = Enumerable.Repeat((byte)3, segmentSize).ToArray(),
        };
        var ranges = new Dictionary<string, LongRange>
        {
            ["a"] = new(0, segmentSize),
            ["b"] = new(segmentSize, segmentSize * 2L),
            ["c"] = new(segmentSize * 2L, segmentSize * 3L),
        };
        var inner = new FakeNntpClient(segments, useCachedYencStreams: true, segmentRanges: ranges);
        using var mismatch = new MismatchBatchClient(inner);
        await using var stream = MultiSegmentStream.Create(
            segments.Keys.ToArray().AsMemory(),
            mismatch,
            articleBufferSize: 4,
            estimatedSegmentSize: segmentSize,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: true,
            CancellationToken.None,
            fileName: "batch-mismatch.bin",
            bodyPipelineBatchWidth: 4);

        var buffer = new byte[segmentSize];
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await stream.ReadAtLeastAsync(buffer, segmentSize, throwOnEndOfStream: true));
        Assert.True(mismatch.CompletionObserved);
    }

    [Fact]
    public async Task PipelinedBatch_EarlyDispose_JoinsCompletionObservers()
    {
        const int segmentSize = 32;
        var segments = Enumerable.Range(0, 4)
            .ToDictionary(i => $"seg-{i}", _ => Enumerable.Repeat((byte)9, segmentSize).ToArray());
        var ranges = segments.Keys
            .Select((id, index) => KeyValuePair.Create(id, new LongRange(index * segmentSize, (index + 1L) * segmentSize)))
            .ToDictionary();
        var inner = new FakeNntpClient(segments, useCachedYencStreams: true, segmentRanges: ranges);
        using var delayed = new DelayedBatchCompletionClient(inner);
        var stream = MultiSegmentStream.Create(
            segments.Keys.ToArray().AsMemory(),
            delayed,
            articleBufferSize: 40,
            estimatedSegmentSize: segmentSize,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: true,
            CancellationToken.None,
            fileName: "batch-dispose.bin",
            bodyPipelineBatchWidth: 4);

        var buffer = new byte[8];
        _ = await stream.ReadAsync(buffer);
        var dispose = stream.DisposeAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!dispose.IsCompleted && DateTime.UtcNow < deadline)
        {
            foreach (var gate in delayed.Completions)
                gate.TrySetResult();
            await Task.Delay(10);
        }

        await dispose.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class DelayedBatchCompletionClient(INntpClient inner) : WrappingNntpClient(inner)
    {
        public ConcurrentQueue<TaskCompletionSource> Completions { get; } = new();

        public override async Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            var batch = await base.DecodedBodiesAsync(segmentIds, onConnectionReadyAgain, cancellationToken)
                .ConfigureAwait(false);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Completions.Enqueue(gate);
            return batch with { Completion = gate.Task };
        }
    }

    private sealed class MismatchBatchClient(INntpClient inner) : WrappingNntpClient(inner)
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CompletionObserved => _completion.Task.IsCompleted;

        public override async Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            if (segmentIds.Count <= 1)
            {
                return await base.DecodedBodiesAsync(segmentIds, onConnectionReadyAgain, cancellationToken)
                    .ConfigureAwait(false);
            }

            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            _ = Task.Run(async () =>
            {
                await Task.Delay(25);
                _completion.TrySetResult();
            });
            return new UsenetDecodedBodyBatch
            {
                Responses = [],
                Completion = _completion.Task,
            };
        }
    }
}
