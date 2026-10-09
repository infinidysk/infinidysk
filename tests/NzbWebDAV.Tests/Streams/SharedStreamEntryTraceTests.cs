using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Models;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using NzbWebDAV.WebDav.Base;

namespace NzbWebDAV.Tests.Streams;

[Collection(nameof(GlobalStreamTraceCollection))]
public sealed class SharedStreamEntryTraceTests
{
    [Fact]
    public async Task Pump_TracesUpstreamPipelineUnderItsOwnRange()
    {
        var previous = StreamTrace.Buffer;
        var buffer = new StreamTraceBuffer(capacity: 1_000, maxSessions: 16);
        StreamTrace.Configure(buffer);
        try
        {
            const int segmentCount = 4, segmentSize = 16;
            var payload = new byte[segmentCount * segmentSize];
            var ids = Enumerable.Range(0, segmentCount).Select(i => $"seg-{i}").ToArray();
            var segments = new Dictionary<string, byte[]>();
            var ranges = new Dictionary<string, LongRange>();
            for (var i = 0; i < segmentCount; i++)
            {
                segments[ids[i]] = payload.AsSpan(i * segmentSize, segmentSize).ToArray();
                ranges[ids[i]] = new LongRange(i * segmentSize, (i + 1) * segmentSize);
            }
            var upstream = new NzbFileStream(
                ids, payload.Length, new FakeNntpClient(segments, useCachedYencStreams: true, segmentRanges: ranges),
                articleBufferSize: 4, segmentByteRanges: ranges.Values.ToArray());

            var entry = new SharedStreamEntry(
                "/content/trace.bin", 0, payload.Length, 64, TimeSpan.Zero, CancellationToken.None,
                chunkSize: 8, leadBytes: 64);
            entry.BindAndStart(new DetachedStreamLease
            {
                Stream = upstream,
                Ownership = NullAsyncDisposable.Instance,
                ContentIdentity = new SharedContentIdentity("trace-test", null, payload.Length),
            });
            await using (var reader = entry.TryAttach(0, (_, _) => throw new InvalidOperationException(), out _)!)
                await reader.CopyToAsync(Stream.Null);
            await entry.DisposeAsync();

            var events = buffer.GetSessionEvents(entry.EntryId);
            var open = Assert.Single(events, e => e.Kind == nameof(StreamTraceKind.RangeOpen));
            Assert.Equal("PUMP", open.Method);
            var end = Assert.Single(events, e => e.Kind == nameof(StreamTraceKind.RangeEnd));
            Assert.Equal(StreamTraceEvent.EndReasonName(ReadSession.EndReasonCode.Completed), end.EndReason);
            Assert.Contains(events, e => e.Kind == nameof(StreamTraceKind.PipelineSample)
                && e.RangeGeneration == open.RangeGeneration);
            var summary = Assert.Single(events, e => e.Kind == nameof(StreamTraceKind.HeadWaitSummary));
            Assert.Equal(open.RangeGeneration, summary.RangeGeneration);
        }
        finally
        {
            StreamTrace.Configure(previous ?? new StreamTraceBuffer(capacity: 1, maxSessions: 10, enabled: false));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pump_SamplesWhileUpstreamBlocksAndStopsOnDispose(bool enabledAtStart)
    {
        var previous = StreamTrace.Buffer;
        var buffer = new StreamTraceBuffer(capacity: 1_000, maxSessions: 16, enabled: enabledAtStart);
        StreamTrace.Configure(buffer);
        try
        {
            var entry = new SharedStreamEntry(
                "/content/blocked.bin", 0, 64, 64, TimeSpan.Zero, CancellationToken.None,
                chunkSize: 8, leadBytes: 64);
            entry.BindAndStart(new DetachedStreamLease
            {
                Stream = new BlockingStream(),
                Ownership = NullAsyncDisposable.Instance,
                ContentIdentity = new SharedContentIdentity("blocked-test", null, 64),
            });

            async Task<StreamTraceEvent> WaitForPumpSample(string state)
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline)
                {
                    StreamTrace.SampleAll();
                    if (buffer.GetSessionEvents(entry.EntryId).LastOrDefault(e =>
                            e.Kind == nameof(StreamTraceKind.PumpSample) && e.Status == state) is { } sample)
                        return sample;
                    await Task.Delay(25);
                }

                throw new TimeoutException($"No pump sample with state {state}.");
            }

            // A pump that is already running, or blocked, is reached when tracing turns on.
            await Task.Delay(100);
            if (!enabledAtStart) buffer.EnableFor(TimeSpan.Zero, 1_000, "test");
            var idle = await WaitForPumpSample("paused-no-reader");
            Assert.Equal(0, idle.Readers);
            Assert.Equal("PUMP", Assert.Single(buffer.GetSessionEvents(entry.EntryId),
                e => e.Kind == nameof(StreamTraceKind.RangeOpen)).Method);

            await using (entry.TryAttach(0, (_, _) => throw new InvalidOperationException(), out _)!)
            {
                var blocked = await WaitForPumpSample("reading-upstream");
                Assert.Equal(1, blocked.Readers);
                Assert.Equal(0, blocked.ReaderLeadBytes);
            }

            await entry.DisposeAsync();
            var afterDispose = buffer.GetSessionEvents(entry.EntryId).Count;
            Assert.Contains(buffer.GetSessionEvents(entry.EntryId), e => e.Kind == nameof(StreamTraceKind.RangeEnd));
            StreamTrace.SampleAll();
            Assert.Equal(afterDispose, buffer.GetSessionEvents(entry.EntryId).Count);
        }
        finally
        {
            StreamTrace.Configure(previous ?? new StreamTraceBuffer(capacity: 1, maxSessions: 10, enabled: false));
        }
    }

    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
