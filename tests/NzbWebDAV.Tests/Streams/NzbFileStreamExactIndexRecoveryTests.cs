using System.Text;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using static NzbWebDAV.Tests.Streams.NzbFileStreamExactIndexTestSupport;

namespace NzbWebDAV.Tests.Streams;

public class NzbFileStreamExactIndexRecoveryTests
{
    [Fact]
    public async Task ExactIndexedSeek_PrefixDiscardEofThrowsAndDisposesBody()
    {
        var opened = new List<ImmediateEofStream>();
        var client = new FakeNntpClient(
            SegmentIds.Zip(SegmentBytes).ToDictionary(pair => pair.First, pair => pair.Second),
            useCachedYencStreams: true,
            SegmentIds.Zip(SegmentRanges).ToDictionary(pair => pair.First, pair => pair.Second),
            (id, bytes) =>
            {
                if (id != "two")
                    return new MemoryStream(bytes, writable: false);
                var body = new ImmediateEofStream();
                opened.Add(body);
                return body;
            });
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client);
        stream.Seek(6, SeekOrigin.Begin);

        await Assert.ThrowsAsync<SeekPositionNotFoundException>(
            async () => await stream.ReadAsync(new byte[1]));
        Assert.All(opened, body => Assert.True(body.Disposed));
        Assert.Equal(0, client.BatchRequestCount);
    }

    [Fact]
    public async Task ExactIndexedSeek_PrefixFailureRemainsPrimaryWhenDisposeAlsoFails()
    {
        var client = CreateClient(decodedStreamFactory: (id, bytes) =>
            id == "two"
                ? new StagedBodyStream(
                    "f"u8.ToArray(),
                    [],
                    [],
                    readFailure: _ => new EndOfStreamException("prefix truncated"),
                    disposeFailure: () => new IOException("dispose failed"))
                : new MemoryStream(bytes, writable: false));
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client);
        stream.Seek(6, SeekOrigin.Begin);

        var failure = await Assert.ThrowsAsync<SeekPositionNotFoundException>(
            async () => await stream.ReadAsync(new byte[1]));

        var prefixFailure = Assert.IsType<EndOfStreamException>(failure.InnerException);
        Assert.Equal("prefix truncated", prefixFailure.Message);
    }

    [Fact]
    public async Task ExactIndexedSeek_MismatchedResponseIdDisposesAndThrows()
    {
        var client = CreateClient();
        client.ForcedResponseSegmentId = "other";
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client);
        stream.Seek(6, SeekOrigin.Begin);

        await Assert.ThrowsAsync<UsenetUnexpectedResponseException>(
            async () => await stream.ReadAsync(new byte[1]));
        Assert.Equal(0, client.BatchRequestCount);
    }

    [Fact]
    public async Task ExactIndexedSeek_KnownMissingUsesLocalBodyBeforeGapFill()
    {
        var client = new FakeNntpClient(
            new Dictionary<string, byte[]>
            {
                ["one"] = SegmentBytes[0],
                ["three"] = SegmentBytes[2],
            },
            useCachedYencStreams: true,
            SegmentIds.Zip(SegmentRanges).ToDictionary(pair => pair.First, pair => pair.Second),
            localSegments: new Dictionary<string, byte[]> { ["two"] = SegmentBytes[1] });
        using var _ = SetBudget(LargeBudget);
        await using var stream = new NzbFileStream(
            SegmentIds,
            15,
            client,
            4,
            SegmentRanges,
            knownMissingSegmentIndices: new HashSet<int> { 1 },
            segmentByteRangesTrusted: true);
        stream.Seek(5, SeekOrigin.Begin);

        var buffer = new byte[5];
        Assert.Equal(5, await stream.ReadAsync(buffer));
        Assert.Equal("fghij", Encoding.ASCII.GetString(buffer));
        Assert.Equal(0, client.BodyRequestCounts.GetValueOrDefault("two"));
    }

    [Fact]
    public async Task ExactIndexedSeek_MissingPrimaryUsesValidatedFallback()
    {
        var client = new FakeNntpClient(
            new Dictionary<string, byte[]>
            {
                ["one"] = SegmentBytes[0],
                ["two-fallback"] = SegmentBytes[1],
                ["three"] = SegmentBytes[2],
            },
            useCachedYencStreams: true,
            new Dictionary<string, LongRange>
            {
                ["one"] = SegmentRanges[0],
                ["two-fallback"] = SegmentRanges[1],
                ["three"] = SegmentRanges[2],
            });
        using var _ = SetBudget(LargeBudget);
        await using var stream = new NzbFileStream(
            SegmentIds,
            15,
            client,
            4,
            SegmentRanges,
            segmentFallbacks: [[], ["two-fallback"], []],
            segmentByteRangesTrusted: true);
        stream.Seek(5, SeekOrigin.Begin);

        var buffer = new byte[5];
        Assert.Equal(5, await stream.ReadAsync(buffer));
        Assert.Equal("fghij", Encoding.ASCII.GetString(buffer));
        Assert.Contains("two-fallback", client.RequestedSegmentIds);
    }

    [Fact]
    public async Task ExactIndexedSeek_LateFallbackCorruptionContinuesToNextFallback()
    {
        var client = new FakeNntpClient(
            new Dictionary<string, byte[]>
            {
                ["one"] = SegmentBytes[0],
                ["two"] = SegmentBytes[1],
                ["two-fallback-1"] = SegmentBytes[1],
                ["two-fallback-2"] = SegmentBytes[1],
                ["three"] = SegmentBytes[2],
            },
            useCachedYencStreams: true,
            new Dictionary<string, LongRange>
            {
                ["one"] = SegmentRanges[0],
                ["two"] = SegmentRanges[1],
                ["two-fallback-1"] = SegmentRanges[1],
                ["two-fallback-2"] = SegmentRanges[1],
                ["three"] = SegmentRanges[2],
            },
            decodedStreamFactory: (id, bytes) => id switch
            {
                "two" => new ThrowingPhaseStream(new UsenetCorruptArticleException(
                    id, "provider-a", new InvalidDataException("CRC mismatch"))),
                "two-fallback-1" => new StagedBodyStream(
                    "f"u8.ToArray(),
                    [],
                    "ghij"u8.ToArray(),
                    readFailure: phase => phase == "tail"
                        ? new UsenetCorruptArticleException(
                            id, "provider-b", new InvalidDataException("CRC mismatch"))
                        : null),
                _ => new MemoryStream(bytes, writable: false),
            });
        using var _ = SetBudget(LargeBudget);
        await using var stream = new NzbFileStream(
            SegmentIds,
            15,
            client,
            4,
            SegmentRanges,
            segmentFallbacks: [[], ["two-fallback-1", "two-fallback-2"], []],
            knownCorruptSegmentIds: new HashSet<string>(StringComparer.Ordinal) { "two" },
            segmentByteRangesTrusted: true);
        stream.Seek(5, SeekOrigin.Begin);

        var buffer = new byte[5];
        Assert.Equal(5, await stream.ReadAsync(buffer));
        Assert.Equal("fghij", Encoding.ASCII.GetString(buffer));
        Assert.Equal(1, client.BodyRequestCounts["two"]);
        Assert.Equal(1, client.BodyRequestCounts["two-fallback-1"]);
        Assert.Equal(1, client.BodyRequestCounts["two-fallback-2"]);
    }

    [Fact]
    public async Task ExactIndexedSeek_CorruptionBeforeEmissionPreservesRetryPolicy()
    {
        var client = CreateClient(decodedStreamFactory: (id, bytes) =>
            id == "two"
                ? new ThrowingPhaseStream(new UsenetCorruptArticleException(
                    id, "provider-a", new InvalidDataException("CRC mismatch")))
                : new MemoryStream(bytes, writable: false));
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client, articleBufferSize: 4);
        stream.Seek(6, SeekOrigin.Begin);

        var buffer = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(buffer));
        Assert.Equal(0, buffer[0]);
        Assert.True(client.BodyRequestCounts["two"] > 1);
    }

    [Fact]
    public async Task ExactIndexedSeek_CorruptionDuringPrefixDiscardPreservesRetryPolicy()
    {
        var client = CreateClient(decodedStreamFactory: (id, bytes) =>
            id == "two"
                ? new StagedBodyStream(
                    "f"u8.ToArray(),
                    "g"u8.ToArray(),
                    "hij"u8.ToArray(),
                    readFailure: phase => phase == "tail"
                        ? new UsenetCorruptArticleException(
                            "two", "provider-a", new InvalidDataException("CRC mismatch"))
                        : null)
                : new MemoryStream(bytes, writable: false));
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client, articleBufferSize: 4);
        stream.Seek(8, SeekOrigin.Begin);

        var buffer = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(buffer));
        Assert.Equal(0, buffer[0]);
        Assert.True(client.BodyRequestCounts["two"] > 1);
    }

    [Fact]
    public async Task ExactIndexedSeek_CorruptionAfterPositioningReplaysPrefixBeforeEmission()
    {
        var opens = 0;
        var client = CreateClient(decodedStreamFactory: (id, bytes) =>
        {
            if (id != "two" || Interlocked.Increment(ref opens) > 1)
                return new MemoryStream(bytes, writable: false);

            return new StagedBodyStream(
                "f"u8.ToArray(),
                "g"u8.ToArray(),
                "hij"u8.ToArray(),
                readFailure: phase => phase == "requested"
                    ? new UsenetCorruptArticleException(
                        "two", "provider-a", new InvalidDataException("CRC mismatch"))
                    : null);
        });
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client, articleBufferSize: 4);
        stream.Seek(6, SeekOrigin.Begin);

        var buffer = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(buffer));
        Assert.Equal((byte)'g', buffer[0]);
        Assert.Equal(2, client.BodyRequestCounts["two"]);
    }

    [Fact]
    public async Task ExactIndexedSeek_TransientFailureBeforeFirstByteRetriesAndThenStartsRemainder()
    {
        var opens = 0;
        var client = CreateClient(decodedStreamFactory: (id, bytes) =>
            id == "two" && Interlocked.Increment(ref opens) == 1
                ? new ThrowingPhaseStream(new IOException("transient body failure"))
                : new MemoryStream(bytes, writable: false));
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client, articleBufferSize: 4);
        stream.Seek(6, SeekOrigin.Begin);

        var buffer = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(buffer));
        Assert.Equal((byte)'g', buffer[0]);
        Assert.Equal(2, client.BodyRequestCounts["two"]);
        await client.FirstBatchRequested.Task.WaitAsync(WaitTimeout);
    }
}
