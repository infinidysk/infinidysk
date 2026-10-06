using NzbWebDAV.Exceptions;
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
                    ? new CorruptAfterPrefixStream(id, SegmentSize / 2)
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

    private static Dictionary<string, byte[]> CreateSegments(int count) =>
        Enumerable.Range(0, count).ToDictionary(
            i => $"seg-{i}",
            i => Enumerable.Range(0, SegmentSize).Select(b => (byte)(b + i)).ToArray(),
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

    /// <summary>Decodes a wrong prefix, then fails the trailer CRC.</summary>
    private sealed class CorruptAfterPrefixStream(string segmentId, int prefix) : Stream
    {
        private bool _prefixRead;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefixRead)
                throw new UsenetCorruptArticleException(segmentId, "provider", new InvalidDataException("bad crc"));
            _prefixRead = true;
            var count = Math.Min(prefix, buffer.Length);
            buffer.Span[..count].Fill(0xEE);
            return ValueTask.FromResult(count);
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
}
