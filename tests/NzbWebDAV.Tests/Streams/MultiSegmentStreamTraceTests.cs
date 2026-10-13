using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Models;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Streams;

[Collection(nameof(GlobalStreamTraceCollection))]
public sealed class MultiSegmentStreamTraceTests
{
    private const int SegmentSize = 1000;

    [Fact]
    public async Task BlockedIncrementalBody_IsSampledAfterLiveActivationAndRecorded()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var trace = new TraceCapture(enabled: false);
        await using var stream = CreateGatedStream(gate.Task);

        var pending = stream.ReadAtLeastAsync(new byte[3 * SegmentSize], 3 * SegmentSize).AsTask();
        // Tracing turns on while the reader is already blocked inside the body.
        await Task.Delay(100);
        trace.Enable();
        var sample = await trace.WaitForAsync(e =>
            e.Kind == nameof(StreamTraceKind.PipelineSample) && e.Status == "incremental-body");
        Assert.True(sample.DurationMs >= 0);
        Assert.False(pending.IsCompleted);

        gate.SetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        var wait = Assert.Single(trace.Events, e =>
            e.Kind == nameof(StreamTraceKind.HeadWait) && e.Status == "incremental-body");
        Assert.Equal(1, wait.SegmentIndex);
        Assert.True(wait.ReaderBlocked);
        Assert.Null(wait.EndReason);
    }

    [Fact]
    public async Task CancelledIncrementalBodyWait_IsRecordedAndCancellationPropagates()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var trace = new TraceCapture(enabled: true);
        await using var stream = CreateGatedStream(gate.Task);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            stream.ReadAtLeastAsync(new byte[3 * SegmentSize], 3 * SegmentSize, cancellationToken: cts.Token)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10)));

        var wait = Assert.Single(trace.Events, e =>
            e.Kind == nameof(StreamTraceKind.HeadWait) && e.EndReason == "cancelled");
        Assert.Equal("incremental-body", wait.Status);
        Assert.True(wait.DurationMs >= 50, $"Wait was {wait.DurationMs} ms.");

        // The abandoned wait no longer reads as in progress.
        StreamTrace.SampleAll();
        var last = trace.Events.Last(e => e.Kind == nameof(StreamTraceKind.PipelineSample));
        Assert.Null(last.Status);
        gate.SetResult();
    }

    [Fact]
    public async Task OverlappingVolumePipelines_CarryDistinctPipelineAndPartIdentity()
    {
        using var trace = new TraceCapture(enabled: true);
        var segments = MultiSegmentStreamIncrementalTests.CreateSegments(3);
        var ranges = MultiSegmentStreamIncrementalTests.Ranges(segments.Count, SegmentSize);
        NzbFileStream Open(int partIndex) => new(
            MultiSegmentStreamIncrementalTests.Ids(segments.Count), 3L * SegmentSize,
            new FakeNntpClient(segments, useCachedYencStreams: true, segmentRanges: ranges),
            articleBufferSize: 2, segmentByteRanges: ranges.Values.ToArray(), fileName: $"part-{partIndex}.bin")
        {
            TracePartIndex = partIndex,
        };

        var first = Open(0);
        var second = Open(1);
        await Task.WhenAll(first.CopyToAsync(Stream.Null), second.CopyToAsync(Stream.Null))
            .WaitAsync(TimeSpan.FromSeconds(10));
        await first.DisposeAsync();
        await second.DisposeAsync();

        var summaries = trace.Events.Where(e => e.Kind == nameof(StreamTraceKind.HeadWaitSummary)).ToList();
        Assert.Equal([0, 1], summaries.Select(e => e.PartIndex ?? -1).Order());
        Assert.Equal(2, summaries.Select(e => e.PipelineId).Distinct().Count());
        Assert.All(summaries, e => Assert.NotNull(e.PipelineId));
    }

    private static Stream CreateGatedStream(Task gate)
    {
        var segments = MultiSegmentStreamIncrementalTests.CreateSegments(3);
        var client = new FakeNntpClient(
            segments,
            useCachedYencStreams: true,
            decodedStreamFactory: (id, bytes) => id == "seg-1"
                ? new GatedBodyStream(bytes, SegmentSize / 2, gate)
                : new MemoryStream(bytes, writable: false));
        return MultiSegmentStream.Create(
            segments.Keys.Order(StringComparer.Ordinal).ToArray().AsMemory(),
            client,
            articleBufferSize: 2,
            estimatedSegmentSize: SegmentSize,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            CancellationToken.None,
            fileName: $"gated-{Guid.NewGuid():N}.bin",
            exactSegmentSizes: Enumerable.Repeat((long)SegmentSize, segments.Count).ToArray());
    }

    private sealed class TraceCapture : IDisposable
    {
        private readonly StreamTraceBuffer? _previous = StreamTrace.Buffer;
        private readonly StreamTraceBuffer _buffer;
        private readonly Guid _sessionId = Guid.NewGuid();
        private readonly IDisposable _scope;

        public TraceCapture(bool enabled)
        {
            _buffer = new StreamTraceBuffer(capacity: 1_000, maxSessions: 16, enabled: enabled);
            StreamTrace.Configure(_buffer);
            _scope = MultiProviderNntpClient.BeginReadSessionScope(_sessionId);
            if (enabled) Enable();
        }

        public IReadOnlyList<StreamTraceEvent> Events => _buffer.GetSessionEvents(_sessionId);

        public void Enable()
        {
            if (!_buffer.Enabled) _buffer.EnableFor(TimeSpan.Zero, 1_000, "test");
            _buffer.RangeOpen(_sessionId, "/view/trace.bin", "GET", 0, null, null, null, null);
        }

        public async Task<StreamTraceEvent> WaitForAsync(Func<StreamTraceEvent, bool> match)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                StreamTrace.SampleAll();
                if (Events.FirstOrDefault(match) is { } found) return found;
                await Task.Delay(25);
            }

            throw new TimeoutException("Expected trace event was not recorded: "
                + string.Join("; ", Events.Select(e => $"{e.Kind} {e.Status}")));
        }

        public void Dispose()
        {
            _scope.Dispose();
            StreamTrace.Configure(_previous ?? new StreamTraceBuffer(capacity: 1, maxSessions: 10, enabled: false));
        }
    }

    /// <summary>Decodes a prefix, waits on a gate, then decodes the rest.</summary>
    private sealed class GatedBodyStream(byte[] bytes, int prefix, Task gate) : Stream
    {
        private int _position;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position == prefix)
                await gate.WaitAsync(cancellationToken);
            var end = _position < prefix ? prefix : bytes.Length;
            var count = Math.Min(end - _position, buffer.Length);
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
