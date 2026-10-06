using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;

namespace NzbWebDAV.Tests.Streams;

public class MultiSegmentStreamIncrementalTests
{
    private const int SegmentSize = 1000;

    [Fact]
    public async Task LateCrcFailure_DeliversOnlyTheCleanReplacement()
    {
        var segments = CreateSegments(3);
        var corruptServed = 0;
        var client = new FakeNntpClient(
            segments,
            useCachedYencStreams: true,
            decodedStreamFactory: (id, bytes) =>
                id == "seg-1" && Interlocked.Exchange(ref corruptServed, 1) == 0
                    ? new FailingBodyStream(SegmentSize / 2, () => Corrupt(id))
                    : new MemoryStream(bytes, writable: false));

        await using var stream = Create(client, segments, $"late-crc-{Guid.NewGuid():N}.bin");
        using var output = new MemoryStream();
        await stream.CopyToAsync(output).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(segments.OrderBy(s => s.Key, StringComparer.Ordinal).SelectMany(s => s.Value), output.ToArray());
        Assert.Equal(2, client.BodyRequestCounts["seg-1"]);
    }

    [Fact]
    public async Task ConsecutiveShortBodies_StillFailFast_BeforeTheThirdPad()
    {
        var segments = CreateSegments(6);
        var client = new FakeNntpClient(
            segments,
            useCachedYencStreams: true,
            decodedStreamFactory: (_, bytes) => new MemoryStream(bytes, 0, SegmentSize / 2, writable: false));

        await using var stream = Create(client, segments, $"short-{Guid.NewGuid():N}.bin");
        var delivered = 0;
        var buffer = new byte[256];
        await Assert.ThrowsAsync<UsenetArticleNotFoundException>(async () =>
        {
            int read;
            while ((read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10))) > 0)
                delivered += read;
        });

        Assert.Equal(2 * SegmentSize + SegmentSize / 2, delivered);
    }

    [Fact]
    public async Task RecoveryAfterSetupFailures_KeepsTheRemainingRetryBudget()
    {
        var segments = CreateSegments(3);
        var fetches = 0;
        var client = new FakeNntpClient(
            segments,
            useCachedYencStreams: true,
            decodedStreamFactory: (id, bytes) =>
            {
                if (id != "seg-1")
                    return new MemoryStream(bytes, writable: false);
                return Interlocked.Increment(ref fetches) switch
                {
                    <= 2 => throw new IOException("connect failed"),
                    3 => new FailingBodyStream(SegmentSize / 2, () => new IOException("connection reset")),
                    _ => new MemoryStream(bytes, writable: false),
                };
            });

        await using var stream = Create(client, segments, $"budget-{Guid.NewGuid():N}.bin");
        using var output = new MemoryStream();
        await Record.ExceptionAsync(() => stream.CopyToAsync(output).WaitAsync(TimeSpan.FromSeconds(10)));

        // MaxBodyRetries = 2: the mid-body failure is the third and last fetch.
        Assert.Equal(3, client.BodyRequestCounts["seg-1"]);
    }

    [Fact]
    public async Task FullRead_DoesNotEndBeforeTheLastArticleTrailerValidates()
    {
        var segments = CreateSegments(3);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var corruptServed = 0;
        var client = new FakeNntpClient(
            segments,
            useCachedYencStreams: true,
            segmentRanges: Ranges(segments.Count, SegmentSize),
            decodedStreamFactory: (id, bytes) =>
                id == "seg-2" && Interlocked.Exchange(ref corruptServed, 1) == 0
                    ? new FailingBodyStream(SegmentSize, () => Corrupt(id), gate.Task)
                    : new MemoryStream(bytes, writable: false));
        await using var stream = new NzbFileStream(
            Ids(segments.Count), 3L * SegmentSize, client, articleBufferSize: 2,
            segmentByteRanges: Ranges(segments.Count, SegmentSize).Values.ToArray(),
            usePipelinedBodyRequests: false, fileName: $"trailer-{Guid.NewGuid():N}.bin");

        using var output = new MemoryStream();
        var copy = stream.CopyToAsync(output);
        await Task.WhenAny(copy, Task.Delay(500));
        Assert.False(copy.IsCompleted, "the copy must wait for the last article's trailer");

        gate.SetResult();
        await AssertTrailerFailureAsync(copy);
        Assert.Equal(2, client.BodyRequestCounts["seg-2"]);
    }

    [Fact]
    public async Task LegacySeek_RangePastBufferedHeadWaitsForItsEndingArticleTrailer()
    {
        var segments = CreateSegments(3);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var corruptServed = 0;
        var client = new FakeNntpClient(
            segments,
            useCachedYencStreams: true,
            segmentRanges: Ranges(segments.Count, SegmentSize),
            decodedStreamFactory: (id, bytes) =>
                id == "seg-2" && Interlocked.Exchange(ref corruptServed, 1) == 0
                    ? new FailingBodyStream(SegmentSize / 2, () => Corrupt(id), gate.Task)
                    : new MemoryStream(bytes, writable: false));
        // No trusted ranges: a buffered head, a direct next article, then the incremental rest.
        await using var stream = new NzbFileStream(
            Ids(segments.Count), 3L * SegmentSize, client, articleBufferSize: 2,
            usePipelinedBodyRequests: false, fileName: $"legacy-seek-{Guid.NewGuid():N}.bin");
        stream.Seek(SegmentSize / 4, SeekOrigin.Begin);

        var copy = new LimitedLengthStream(stream, 2 * SegmentSize).CopyToAsync(Stream.Null);
        await Task.WhenAny(copy, Task.Delay(500));
        Assert.False(copy.IsCompleted, $"the range must wait for its ending article's trailer: {copy.Exception}");

        gate.SetResult();
        await AssertTrailerFailureAsync(copy);
    }

    // A timeout is a hang, not a rejection: require the trailer's own failure.
    internal static async Task AssertTrailerFailureAsync(Task copy)
    {
        var completed = await Task.WhenAny(copy, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(completed == copy, "the read hung after its trailer failed");
        var failure = await Record.ExceptionAsync(() => copy);
        for (var e = failure; e is not null; e = e.InnerException)
            if (e is InvalidDataException) return;
        Assert.Fail($"expected the trailer failure, got {failure?.ToString() ?? "success"}");
    }

    internal static string[] Ids(int count) =>
        Enumerable.Range(0, count).Select(i => $"seg-{i}").ToArray();

    internal static Dictionary<string, LongRange> Ranges(int count, int size) =>
        Enumerable.Range(0, count).ToDictionary(
            i => $"seg-{i}", i => new LongRange((long)i * size, (long)(i + 1) * size), StringComparer.Ordinal);

    internal static Exception Corrupt(string segmentId) =>
        new UsenetCorruptArticleException(segmentId, "provider", new InvalidDataException("bad crc"));

    internal static Dictionary<string, byte[]> CreateSegments(int count, int size = SegmentSize) =>
        Enumerable.Range(0, count).ToDictionary(
            i => $"seg-{i}",
            i => Enumerable.Range(0, size).Select(b => (byte)(b + i)).ToArray(),
            StringComparer.Ordinal);

    private static Stream Create(
        FakeNntpClient client, Dictionary<string, byte[]> segments, string fileName) =>
        MultiSegmentStream.Create(
            segments.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray().AsMemory(),
            client,
            articleBufferSize: 2,
            estimatedSegmentSize: SegmentSize,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            CancellationToken.None,
            fileName: fileName,
            exactSegmentSizes: Enumerable.Repeat((long)SegmentSize, segments.Count).ToArray());
}

/// <summary>Decodes a wrong prefix, optionally waits on a gate, then fails (e.g. the trailer CRC).</summary>
internal sealed class FailingBodyStream(int prefix, Func<Exception> failure, Task? gate = null) : Stream
{
    private int _remaining = prefix;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_remaining == 0)
        {
            if (gate is not null)
                await gate.WaitAsync(cancellationToken);
            throw failure();
        }

        var count = Math.Min(_remaining, buffer.Length);
        buffer.Span[..count].Fill(0xEE);
        _remaining -= count;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
