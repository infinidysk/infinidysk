using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using static NzbWebDAV.Tests.Streams.NzbFileStreamExactIndexTestSupport;

namespace NzbWebDAV.Tests.Streams;

public sealed class CacheReadEvidenceTests
{
    [Theory]
    [InlineData("complete", true)]
    [InlineData("short", false)]
    [InlineData("missing", false)]
    public async Task BufferedSegment_OnlyCompleteAcceptedBytesAreCacheable(string kind, bool expected)
    {
        var data = new Dictionary<string, byte[]> { ["two"] = "fghij"u8.ToArray() };
        if (kind != "missing") data["one"] = kind == "short" ? "ab"u8.ToArray() : "abcde"u8.ToArray();
        using var client = new FakeNntpClient(data, useCachedYencStreams: true);
        await using var stream = MultiSegmentStream.Create(new[] { "one", "two" }.AsMemory(), client,
            articleBufferSize: 4, estimatedSegmentSize: 5, failFastOnFirstSegment: false,
            usePipelinedBodyRequests: true, cancellationToken: CancellationToken.None,
            fileName: "evidence-" + Guid.NewGuid().ToString("N"), exactSegmentSizes: new long[] { 5, 5 });
        var buffer = new byte[5];
        Assert.Equal(5, await stream.ReadAsync(buffer));
        var evidence = Assert.IsAssignableFrom<ICacheReadEvidence>(stream);
        Assert.Equal(expected, evidence.LastReadCacheable);
        Assert.Equal(5, await stream.ReadAsync(buffer));
        Assert.True(evidence.LastReadCacheable);
        Assert.Equal(0, await stream.ReadAsync(Memory<byte>.Empty));
        Assert.False(evidence.LastReadCacheable);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 0)]
    [InlineData(0, 2)]
    [InlineData(4, 7)]
    public async Task NativeTrustedNzb_FillsBeyondShortHttpRangeWithVerifiedBufferedBytes(int bufferSize, int offset)
    {
        using var httpBudget = SetBudget(1);
        using var native = new NativeCacheReadContext();
        using var client = CreateClient();
        await using var stream = CreateStream(client, bufferSize);
        stream.Seek(offset, SeekOrigin.Begin);
        var evidence = Assert.IsAssignableFrom<ICacheReadEvidence>(stream);
        var result = new List<byte>();
        var buffer = new byte[8];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            Assert.True(evidence.LastReadCacheable);
            result.AddRange(buffer.AsSpan(0, read).ToArray());
        }
        Assert.Equal("abcdefghijklmno"u8.ToArray()[offset..], result);
        Assert.False(evidence.LastReadCacheable);
    }

    [Fact]
    public async Task OrdinaryDirectNzbRead_DoesNotClaimEarlyBytesAreVerified()
    {
        using var budget = SetBudget(1);
        using var client = CreateClient();
        await using var stream = CreateStream(client);
        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        Assert.False(Assert.IsAssignableFrom<ICacheReadEvidence>(stream).LastReadCacheable);
    }

    [Fact]
    public async Task NativeSequentialBlocks_RestartAnExhaustedFiniteSourceAtTheSamePosition()
    {
        using var client = CreateClient();
        await using var stream = new NzbFileStream(SegmentIds, 15, client, 1,
            SegmentRanges, usePipelinedBodyRequests: false, segmentByteRangesTrusted: true);
        var buffer = new byte[5];
        for (var block = 0; block < 3; block++)
        {
            using var native = new NativeCacheReadContext(readBudget: 5);
            stream.Position = block * 5;
            await stream.ReadExactlyAsync(buffer);
            Assert.Equal(SegmentBytes[block], buffer);
            Assert.True(Assert.IsAssignableFrom<ICacheReadEvidence>(stream).LastReadCacheable);
        }
    }

    [Fact]
    public async Task CombinedAndPadding_PropagateEvidenceWithoutTrustingSyntheticBytes()
    {
        var source = new EvidenceMemoryStream([1, 2], safe: true);
        var padded = new PaddedLengthStream(source, 4, "part", context: new MultipartPartContext
        {
            PartNumber = 1, PartCount = 1, SeekOffsetWithinPart = 0,
            DeclaredVolumeLength = 4, IsEncrypted = true,
        });
        await using var combined = new CombinedStream([Task.FromResult<Stream>(padded)]);
        var evidence = Assert.IsAssignableFrom<ICacheReadEvidence>(combined);
        Assert.Equal(2, await combined.ReadAsync(new byte[4]));
        Assert.True(evidence.LastReadCacheable);
        Assert.Equal(2, await combined.ReadAsync(new byte[4]));
        Assert.False(evidence.LastReadCacheable);
    }

    [Fact]
    public async Task Handoff_UnknownHeadDoesNotTaintVerifiedRemainder()
    {
        await using var stream = new FirstSegmentHandoffStream(new MemoryStream([1]),
            _ => new EvidenceMemoryStream([2], safe: true), RemainderStartPolicy.AtHeadEof, CancellationToken.None);
        var evidence = Assert.IsAssignableFrom<ICacheReadEvidence>(stream);
        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        Assert.False(evidence.LastReadCacheable);
        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        Assert.True(evidence.LastReadCacheable);
        Assert.Equal(0, await stream.ReadAsync(new byte[1]));
        Assert.False(evidence.LastReadCacheable);
    }

    private sealed class EvidenceMemoryStream(byte[] bytes, bool safe) : MemoryStream(bytes), ICacheReadEvidence
    {
        public bool LastReadCacheable { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            LastReadCacheable = false;
            var read = await base.ReadAsync(buffer, ct);
            LastReadCacheable = read > 0 && safe;
            return read;
        }
    }
}
