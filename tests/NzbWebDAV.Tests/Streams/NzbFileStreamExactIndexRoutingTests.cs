using System.Text;
using MemoryPack;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using static NzbWebDAV.Tests.Streams.NzbFileStreamExactIndexTestSupport;

namespace NzbWebDAV.Tests.Streams;

public class NzbFileStreamExactIndexRoutingTests
{
    [Fact]
    public async Task ExactIndexedSeek_ReturnsFirstRequestedByteBeforeTargetBodyEof()
    {
        var staged = new StagedBodyStream("f"u8.ToArray(), "g"u8.ToArray(), "hij"u8.ToArray());
        var client = CreateClient(decodedStreamFactory: (id, bytes) =>
            id == "two" ? staged : new MemoryStream(bytes, writable: false));
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client);
        stream.Seek(6, SeekOrigin.Begin);
        Assert.Equal(0, client.BatchRequestCount);

        var buffer = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(buffer));
        Assert.Equal((byte)'g', buffer[0]);
        Assert.True(staged.TailGateClosed);
        Assert.False(staged.TailReadStarted.IsCompleted);
        Assert.Equal(0, client.HeaderProbeCount);
        Assert.Equal(1, client.BodyRequestCounts["two"]);
    }

    [Fact]
    public async Task ExactIndexedSeek_DoesNotProbeHeaders()
    {
        var client = CreateClient();
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client);
        stream.Seek(7, SeekOrigin.Begin);

        var buffer = new byte[3];
        Assert.Equal(3, await stream.ReadAsync(buffer));
        Assert.Equal("hij", Encoding.ASCII.GetString(buffer));
        Assert.Equal(0, client.HeaderProbeCount);
    }

    [Fact]
    public async Task UntrustedStructurallyValidIndex_UsesActualHeaderGeometry()
    {
        string[] ids = ["one", "two"];
        var client = new FakeNntpClient(
            new Dictionary<string, byte[]>
            {
                ["one"] = "abcd"u8.ToArray(),
                ["two"] = "efghij"u8.ToArray(),
            },
            useCachedYencStreams: true,
            segmentRanges: new Dictionary<string, LongRange>
            {
                ["one"] = new(0, 4),
                ["two"] = new(4, 10),
            });
        using var _ = SetBudget(1);
        await using var stream = new NzbFileStream(
            ids,
            fileSize: 10,
            client,
            articleBufferSize: 4,
            segmentByteRanges: [new LongRange(0, 5), new LongRange(5, 10)],
            segmentByteRangesTrusted: false);
        stream.Seek(5, SeekOrigin.Begin);

        var buffer = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(buffer));
        Assert.Equal((byte)'f', buffer[0]);
        Assert.True(client.HeaderProbeCount > 0);
    }

    [Fact]
    public async Task LegacyBlobWithoutTrustMetadata_UsesHeaderProbedSeeking()
    {
        var original = new DavNzbFile
        {
            Id = Guid.NewGuid(),
            SegmentIds = ["one", "two"],
            SegmentByteRanges = [new LongRange(0, 5), new LongRange(5, 10)],
        };
        var deserialized = MemoryPackSerializer.Deserialize<DavNzbFile>(
            MemoryPackSerializer.Serialize(original))!;
        Assert.Null(deserialized.SegmentByteRangesTrusted);

        var client = new FakeNntpClient(
            new Dictionary<string, byte[]>
            {
                ["one"] = "abcd"u8.ToArray(),
                ["two"] = "efghij"u8.ToArray(),
            },
            useCachedYencStreams: true,
            segmentRanges: new Dictionary<string, LongRange>
            {
                ["one"] = new(0, 4),
                ["two"] = new(4, 10),
            });
        using var _ = SetBudget(1);
        await using var stream = new NzbFileStream(
            deserialized.SegmentIds,
            fileSize: 10,
            client,
            articleBufferSize: 4,
            segmentByteRanges: deserialized.SegmentByteRanges,
            segmentByteRangesTrusted: deserialized.SegmentByteRangesTrusted == true);
        stream.Seek(5, SeekOrigin.Begin);

        var buffer = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(buffer));
        Assert.Equal((byte)'f', buffer[0]);
        Assert.True(client.HeaderProbeCount > 0);
    }

    [Fact]
    public async Task ExactIndexedSeek_DoesNotDrainUnrequestedTargetSuffixBeforeFirstRead()
    {
        var staged = new StagedBodyStream("fg"u8.ToArray(), "h"u8.ToArray(), "ij"u8.ToArray());
        var client = CreateClient(decodedStreamFactory: (id, bytes) =>
            id == "two" ? staged : new MemoryStream(bytes, writable: false));
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client);
        stream.Seek(7, SeekOrigin.Begin);

        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        Assert.True(staged.TailGateClosed);
        Assert.Equal(3, staged.TotalBytesRead);
    }

    [Fact]
    public async Task ExactIndexedSeek_LargeRangeBypassesLegacyBufferedSeekHead()
    {
        var client = CreateClient();
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client, articleBufferSize: 4);
        stream.Seek(6, SeekOrigin.Begin);

        var buffer = new byte[2];
        Assert.Equal(2, await stream.ReadAsync(buffer));
        Assert.Equal("gh", Encoding.ASCII.GetString(buffer));
        Assert.Equal(0, client.HeaderProbeCount);
        Assert.Equal(1, client.BodyRequestCounts.GetValueOrDefault("two"));
    }

    [Fact]
    public async Task LegacySeek_LargeRangeRetainsBufferedFastPath()
    {
        var client = CreateClient();
        using var _ = SetBudget(LargeBudget);
        await using var stream = new NzbFileStream(
            SegmentIds, 15, client, articleBufferSize: 4, segmentByteRanges: null);
        stream.Seek(6, SeekOrigin.Begin);

        var buffer = new byte[2];
        Assert.Equal(2, await stream.ReadAsync(buffer));
        Assert.Equal("gh", Encoding.ASCII.GetString(buffer));
        Assert.True(client.BodyRequestCounts["two"] >= 1);
    }

    [Fact]
    public async Task LegacySeek_SmallRangeRetainsHeaderProbedUnbufferedPath()
    {
        var client = CreateClient();
        using var _ = SetBudget(2);
        await using var stream = new NzbFileStream(
            SegmentIds, 15, client, articleBufferSize: 4, segmentByteRanges: null);
        stream.Seek(6, SeekOrigin.Begin);

        var buffer = new byte[2];
        Assert.Equal(2, await stream.ReadAsync(buffer));
        Assert.Equal("gh", Encoding.ASCII.GetString(buffer));
        Assert.True(client.HeaderProbeCount >= 1);
        Assert.Equal(0, client.BatchRequestCount);
    }

    [Fact]
    public async Task ExactIndexedSeek_LastSegmentReturnsFirstByteBeforeTargetBodyEof()
    {
        var staged = new StagedBodyStream("kl"u8.ToArray(), "m"u8.ToArray(), "no"u8.ToArray());
        var client = CreateClient(decodedStreamFactory: (id, bytes) =>
            id == "three" ? staged : new MemoryStream(bytes, writable: false));
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client, articleBufferSize: 4);
        stream.Seek(12, SeekOrigin.Begin);

        var buffer = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(buffer));
        Assert.Equal((byte)'m', buffer[0]);
        Assert.True(staged.TailGateClosed);
        Assert.False(client.BodyRequestCounts.ContainsKey("one"));
        Assert.False(client.BodyRequestCounts.ContainsKey("two"));
        Assert.Equal(1, client.BodyRequestCounts["three"]);
        Assert.Equal(0, client.BatchRequestCount);
    }

    [Fact]
    public async Task ExactIndexedSeek_NearArticleEndUsesOnlyVisibleTailForBudget()
    {
        var client = CreateClient();
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client, articleBufferSize: 4);
        stream.Seek(9, SeekOrigin.Begin);

        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        await client.FirstBatchRequested.Task.WaitAsync(WaitTimeout);
        Assert.True(client.BatchRequestCount > 0);
    }

    [Fact]
    public async Task ExactIndexedSeek_RangeWithinTargetNeverStartsRemainder()
    {
        var client = CreateClient();
        using var _ = SetBudget(2);
        await using var stream = CreateStream(client, articleBufferSize: 4);
        stream.Seek(6, SeekOrigin.Begin);

        var buffer = new byte[2];
        Assert.Equal(2, await stream.ReadAsync(buffer));
        Assert.Equal("gh", Encoding.ASCII.GetString(buffer));
        Assert.Equal(0, client.BatchRequestCount);
        Assert.Equal(1, client.BodyRequestCounts["two"]);
    }

    [Fact]
    public async Task ExactIndexedSeek_CrossingBoundaryReturnsExactBytes()
    {
        var client = CreateClient();
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client, articleBufferSize: 4);
        stream.Seek(8, SeekOrigin.Begin);

        var buffer = new byte[4];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read));
            if (n == 0) break;
            read += n;
        }

        Assert.Equal(4, read);
        Assert.Equal("ijkl", Encoding.ASCII.GetString(buffer));
    }

    [Fact]
    public async Task ExactIndexedSeek_StartingAtBoundaryDoesNotOpenPreviousArticle()
    {
        var client = CreateClient();
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client, articleBufferSize: 4);
        stream.Seek(5, SeekOrigin.Begin);

        var buffer = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(buffer));
        Assert.Equal((byte)'f', buffer[0]);
        Assert.False(client.BodyRequestCounts.ContainsKey("one"));
        Assert.Equal(1, client.BodyRequestCounts["two"]);
    }

    [Fact]
    public async Task ExactIndexedSeek_OneArticleFileNeverCreatesRemainder()
    {
        var bytes = Encoding.ASCII.GetBytes("abcdefghij");
        var client = new FakeNntpClient(
            new Dictionary<string, byte[]> { ["only"] = bytes },
            useCachedYencStreams: true,
            segmentRanges: new Dictionary<string, LongRange> { ["only"] = new(0, 10) });
        using var _ = SetBudget(LargeBudget);
        await using var stream = new NzbFileStream(
            ["only"],
            10,
            client,
            articleBufferSize: 4,
            segmentByteRanges: [new LongRange(0, 10)],
            segmentByteRangesTrusted: true);
        stream.Seek(3, SeekOrigin.Begin);

        var buffer = new byte[4];
        Assert.Equal(4, await stream.ReadAsync(buffer));
        Assert.Equal("defg", Encoding.ASCII.GetString(buffer));
        Assert.Equal(0, client.BatchRequestCount);
        Assert.Equal(1, client.BodyRequestCounts["only"]);
    }

    [Theory]
    [InlineData(0, 4, "abcd")]
    [InlineData(3, 4, "defg")]
    [InlineData(4, 6, "efghij")]
    [InlineData(9, 6, "jklmno")]
    [InlineData(14, 1, "o")]
    public async Task ExactIndexedSeek_RangeVariantsAreByteIdentical(
        long start, int count, string expected)
    {
        var client = CreateClient();
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client, articleBufferSize: 4);
        stream.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read));
            if (n == 0) break;
            read += n;
        }

        Assert.Equal(expected.Length, read);
        Assert.Equal(expected, Encoding.ASCII.GetString(buffer, 0, read));
        Assert.Equal(start + read, stream.Position);
    }
}
