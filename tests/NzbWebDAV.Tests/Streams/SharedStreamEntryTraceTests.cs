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

    [Fact]
    public async Task Pump_SamplesWhileUpstreamBlocksAndStopsOnDispose()
    {
        var previous = StreamTrace.Buffer;
        var buffer = new StreamTraceBuffer(capacity: 1_000, maxSessions: 16);
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

            int PumpSamples() => buffer.GetSessionEvents(entry.EntryId)
                .Count(e => e.Kind == nameof(StreamTraceKind.PumpSample));
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (PumpSamples() < 2 && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            Assert.True(PumpSamples() >= 2, "Pump did not sample while its upstream read was blocked.");

            await entry.DisposeAsync();
            var afterDispose = buffer.GetSessionEvents(entry.EntryId).Count;
            Assert.Contains(buffer.GetSessionEvents(entry.EntryId), e => e.Kind == nameof(StreamTraceKind.RangeEnd));
            await Task.Delay(1_100);
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
