using System.Text;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Streams;

[Collection(nameof(PlaybackHoleTrackerCollection))]
public sealed class UnbufferedShortDecodeHoleTests : IDisposable
{
    public UnbufferedShortDecodeHoleTests() => PlaybackHoleTracker.ResetForTests();

    public void Dispose() => PlaybackHoleTracker.ResetForTests();

    [Fact]
    public async Task ShortDecodePadding_RecordsHoleAndDoesNotFailFastAlone()
    {
        var path = $"/view/short-decode-{Guid.NewGuid():N}.mkv";
        const string shortId = "short@test";
        const string okId = "ok@test";
        var client = new FakeNntpClient(
            new Dictionary<string, byte[]>
            {
                [shortId] = "ab"u8.ToArray(),
                [okId] = "cdef"u8.ToArray(),
            });
        await using var stream = new UnbufferedMultiSegmentStream(
            new[] { shortId, okId }.AsMemory(),
            client,
            estimatedSegmentSize: 5,
            fileName: path,
            exactSegmentSizes: new long[] { 5, 4 });
        using var output = new MemoryStream();

        await stream.CopyToAsync(output);

        Assert.Equal("ab\0\0\0cdef", Encoding.ASCII.GetString(output.ToArray()));
        Assert.True(PlaybackHoleTracker.IsKnownMissingSegment(path, shortId));
        Assert.False(PlaybackHoleTracker.ShouldFailFast(path, out _));
        Assert.Equal(1, client.BodyRequestCounts[shortId]);
        Assert.Equal(1, client.BodyRequestCounts[okId]);
    }

    [Fact]
    public async Task ThreeConsecutiveShortDecodes_FailFast()
    {
        var path = $"/view/short-cap-{Guid.NewGuid():N}.mkv";
        var ids = new[] { "s0@test", "s1@test", "s2@test" };
        var client = new FakeNntpClient(
            ids.ToDictionary(id => id, _ => "x"u8.ToArray(), StringComparer.Ordinal));
        await using var stream = new UnbufferedMultiSegmentStream(
            ids.AsMemory(),
            client,
            estimatedSegmentSize: 5,
            fileName: path,
            exactSegmentSizes: new long[] { 5, 5, 5 });

        await Assert.ThrowsAsync<UsenetArticleNotFoundException>(
            async () => await stream.CopyToAsync(Stream.Null));

        Assert.True(PlaybackHoleTracker.ShouldFailFast(path, out var stored));
        Assert.IsType<UsenetArticleNotFoundException>(stored);
    }

    [Fact]
    public async Task ShortDecode_CountsTowardCrossRangeFailFast()
    {
        var path = $"/view/short-range-{Guid.NewGuid():N}.mkv";
        var ids = new[] { "s0@test", "s1@test", "s2@test" };
        var first = new FakeNntpClient(
            ids.ToDictionary(id => id, _ => "x"u8.ToArray(), StringComparer.Ordinal));
        await using (var stream = new UnbufferedMultiSegmentStream(
                         ids.AsMemory(),
                         first,
                         estimatedSegmentSize: 5,
                         fileName: path,
                         exactSegmentSizes: new long[] { 5, 5, 5 }))
        {
            await Assert.ThrowsAsync<UsenetArticleNotFoundException>(
                async () => await stream.CopyToAsync(Stream.Null));
        }

        var second = new FakeNntpClient(
            ids.ToDictionary(id => id, _ => "x"u8.ToArray(), StringComparer.Ordinal));
        await using var stream2 = new UnbufferedMultiSegmentStream(
            ids.AsMemory(),
            second,
            estimatedSegmentSize: 5,
            fileName: path,
            exactSegmentSizes: new long[] { 5, 5, 5 });

        await Assert.ThrowsAsync<UsenetArticleNotFoundException>(
            async () => await stream2.CopyToAsync(Stream.Null));
        Assert.Equal(0, second.BodyRequestCount);
    }

    [Fact]
    public async Task CorruptionAfterRetryProbeUsesPreEmissionRecovery()
    {
        var path = $"/view/probe-return-{Guid.NewGuid():N}.mkv";
        const string id = "probe@test";
        var bodies = 0;
        var crc = new UsenetCorruptArticleException(
            id, "provider-a", new InvalidDataException("CRC mismatch"));
        var client = new FakeNntpClient(
            new Dictionary<string, byte[]> { [id] = "abcde"u8.ToArray() },
            useCachedYencStreams: true,
            decodedStreamFactory: (_, bytes) =>
            {
                var n = Interlocked.Increment(ref bodies);
                if (n == 1)
                {
                    return new StagedBodyStream(
                        "a"u8.ToArray(),
                        [],
                        [],
                        readFailure: _ => crc);
                }

                return new StagedBodyStream(
                    [],
                    "b"u8.ToArray(),
                    "cde"u8.ToArray(),
                    readFailure: phase => phase == "tail" ? crc : null);
            });
        await using var stream = new UnbufferedMultiSegmentStream(
            new[] { id }.AsMemory(),
            client,
            estimatedSegmentSize: 5,
            fileName: path,
            exactSegmentSizes: new long[] { 5 });

        var buffer = new byte[16];
        var read = await stream.ReadAsync(buffer);
        Assert.True(read > 0);
        Assert.True(client.BodyRequestCounts[id] > 1);
    }
}
