using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Streams;

public class KnownCorruptFastPathTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownCorruptId_FetchesOnceThenZeroFills(bool usePipelinedBodyRequests)
    {
        var segmentId = $"known-corrupt-{Guid.NewGuid():N}@example";
        const int fill = 8;
        var client = NewCorruptClient(segmentId);
        await using var stream = MultiSegmentStream.Create(
            new[] { segmentId }.AsMemory(),
            client,
            articleBufferSize: 4,
            estimatedSegmentSize: fill,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests,
            CancellationToken.None,
            fileName: "movie.mkv",
            exactSegmentSizes: new long[] { fill },
            knownCorruptSegmentIds: new HashSet<string>(StringComparer.Ordinal) { segmentId });
        using var output = new MemoryStream();

        await stream.CopyToAsync(output);

        Assert.Equal(new byte[fill], output.ToArray());
        Assert.Equal(1, client.BodyRequestCount);
        Assert.Equal(1, client.BodyRequestCounts[segmentId]);
    }

    [Fact]
    public async Task KnownCorruptId_Unbuffered_FetchesOnceThenZeroFills()
    {
        var segmentId = $"known-corrupt-{Guid.NewGuid():N}@example";
        const int fill = 8;
        var client = NewCorruptClient(segmentId);
        await using var stream = MultiSegmentStream.Create(
            new[] { segmentId }.AsMemory(),
            client,
            articleBufferSize: 0,
            estimatedSegmentSize: fill,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            CancellationToken.None,
            fileName: "movie.mkv",
            exactSegmentSizes: new long[] { fill },
            knownCorruptSegmentIds: new HashSet<string>(StringComparer.Ordinal) { segmentId });
        using var output = new MemoryStream();

        await stream.CopyToAsync(output);

        Assert.Equal(new byte[fill], output.ToArray());
        Assert.Equal(1, client.BodyRequestCount);
    }

    [Fact]
    public async Task KnownCorruptId_PatchedBytesAreServedWithoutProviderFetch()
    {
        var dir = Path.Join(Path.GetTempPath(), "nzbdav-known-corrupt-patch-" + Guid.NewGuid().ToString("N"));
        var segmentId = $"patched-{Guid.NewGuid():N}@example";
        var patched = "repaired!"u8.ToArray();
        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024);
            await store.CatalogLoadTask;
            store.CommitPatch(segmentId, patched, new UsenetYencHeader
            {
                FileName = "movie.mkv",
                FileSize = patched.Length,
                LineLength = 128,
                PartNumber = 1,
                TotalParts = 1,
                PartSize = patched.Length,
                PartOffset = 0,
            });

            var inner = NewCorruptClient(segmentId);
            using var client = new RepairedSegmentNntpClient(inner, store);
            await using var stream = MultiSegmentStream.Create(
                new[] { segmentId }.AsMemory(),
                client,
                articleBufferSize: 4,
                estimatedSegmentSize: patched.Length,
                failFastOnFirstSegment: false,
                usePipelinedBodyRequests: false,
                CancellationToken.None,
                fileName: "movie.mkv",
                exactSegmentSizes: new long[] { patched.Length },
                knownCorruptSegmentIds: new HashSet<string>(StringComparer.Ordinal) { segmentId });
            using var output = new MemoryStream();

            await stream.CopyToAsync(output);

            Assert.Equal(patched, output.ToArray());
            Assert.Equal(0, inner.BodyRequestCount);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task KnownCorruptId_DonorStillServedAfterSinglePrimaryFetch()
    {
        var primary = $"primary-{Guid.NewGuid():N}@example";
        var donor = $"donor-{Guid.NewGuid():N}@example";
        var payload = "donorok"u8.ToArray();
        var present = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [primary] = new byte[payload.Length],
            [donor] = payload,
        };
        var client = new FakeNntpClient(
            present,
            useCachedYencStreams: true,
            decodedStreamFactory: (id, bytes) => id == primary
                ? new ThrowingReadStream(id)
                : new MemoryStream(bytes, writable: false));
        await using var stream = MultiSegmentStream.Create(
            new[] { primary }.AsMemory(),
            client,
            articleBufferSize: 4,
            estimatedSegmentSize: payload.Length,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            CancellationToken.None,
            fileName: "movie.mkv",
            segmentFallbacks: [[donor]],
            exactSegmentSizes: new long[] { payload.Length },
            knownCorruptSegmentIds: new HashSet<string>(StringComparer.Ordinal) { primary });
        using var output = new MemoryStream();

        await stream.CopyToAsync(output);

        Assert.Equal(payload, output.ToArray());
        Assert.Equal(1, client.BodyRequestCounts[primary]);
        Assert.Equal(1, client.BodyRequestCounts[donor]);
    }

    [Fact]
    public async Task KnownCorruptId_FailFastOnFirstSegment_ThrowsAfterOneFetch()
    {
        var segmentId = $"failfast-{Guid.NewGuid():N}@example";
        var client = NewCorruptClient(segmentId);
        await using var stream = MultiSegmentStream.Create(
            new[] { segmentId }.AsMemory(),
            client,
            articleBufferSize: 4,
            estimatedSegmentSize: 8,
            failFastOnFirstSegment: true,
            usePipelinedBodyRequests: false,
            CancellationToken.None,
            fileName: "movie.mkv",
            exactSegmentSizes: new long[] { 8 },
            knownCorruptSegmentIds: new HashSet<string>(StringComparer.Ordinal) { segmentId });

        var thrown = await Assert.ThrowsAsync<UsenetCorruptArticleException>(
            async () => await stream.ReadExactlyAsync(new byte[1]));
        Assert.Equal(segmentId, thrown.SegmentId);
        Assert.Equal(1, client.BodyRequestCount);
    }

    [Fact]
    public async Task GetFileStream_ThreadsKnownCorruptIdsIntoTheReader()
    {
        var segmentId = $"threaded-{Guid.NewGuid():N}@example";
        const int fill = 8;
        var client = NewCorruptClient(segmentId);
        await using var stream = client.GetFileStream(
            [segmentId],
            fill,
            articleBufferSize: 4,
            segmentByteRanges: [LongRange.FromStartAndSize(0, fill)],
            usePipelinedBodyRequests: false,
            fileName: "movie.mkv",
            knownCorruptSegmentIds: new HashSet<string>(StringComparer.Ordinal) { segmentId });

        var thrown = await Assert.ThrowsAsync<UsenetCorruptArticleException>(
            async () => await stream.CopyToAsync(Stream.Null));
        Assert.Equal(segmentId, thrown.SegmentId);
        Assert.Equal(1, client.BodyRequestCount);
    }

    [Fact]
    public async Task TwoConcurrentStreams_BothGapFill_AndDedupToOneSinkEvent()
    {
        var segmentId = $"concurrent-{Guid.NewGuid():N}@example";
        const int fill = 8;
        var path = $"/view/concurrent-{Guid.NewGuid():N}.mkv";
        var dir = Path.Join(Path.GetTempPath(), "nzbdav-known-corrupt-sink-" + Guid.NewGuid().ToString("N"));
        var prev = Par2RepairTriggerSink.Current;
        try
        {
            Directory.CreateDirectory(dir);
            var config = new ConfigManager();
            config.UpdateValues(
            [
                new ConfigItem { ConfigName = ConfigKeys.RepairEnable, ConfigValue = "true" },
            ]);
            var store = new RepairPatchStore(dir, 1024 * 1024);
            await store.CatalogLoadTask;
            var service = new Par2RepairService(config, null!, store);
            Par2RepairTriggerSink.Current = new Par2RepairTriggerSink(service);

            var clientA = NewCorruptClient(segmentId);
            var clientB = NewCorruptClient(segmentId);
            var known = new HashSet<string>(StringComparer.Ordinal) { segmentId };

            await using var streamA = CreateZeroFillStream(clientA, segmentId, fill, path, known);
            await using var streamB = CreateZeroFillStream(clientB, segmentId, fill, path, known);
            using var outputA = new MemoryStream();
            using var outputB = new MemoryStream();

            await Task.WhenAll(streamA.CopyToAsync(outputA), streamB.CopyToAsync(outputB));

            Assert.Equal(new byte[fill], outputA.ToArray());
            Assert.Equal(new byte[fill], outputB.ToArray());
            Assert.Equal(1, clientA.BodyRequestCount);
            Assert.Equal(1, clientB.BodyRequestCount);
            Assert.True(service.HasPendingZeroFillPath(path));
        }
        finally
        {
            Par2RepairTriggerSink.Current = prev;
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static Stream CreateZeroFillStream(
        INntpClient client,
        string segmentId,
        int fill,
        string fileName,
        HashSet<string> knownCorrupt) =>
        MultiSegmentStream.Create(
            new[] { segmentId }.AsMemory(),
            client,
            articleBufferSize: 4,
            estimatedSegmentSize: fill,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            CancellationToken.None,
            fileName: fileName,
            exactSegmentSizes: new long[] { fill },
            knownCorruptSegmentIds: knownCorrupt);

    private static FakeNntpClient NewCorruptClient(string segmentId) =>
        new(
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [segmentId] = new byte[8],
            },
            useCachedYencStreams: true,
            decodedStreamFactory: (id, _) => new ThrowingReadStream(id));

    private sealed class ThrowingReadStream(string segmentId) : MemoryStream
    {
        private UsenetCorruptArticleException CreateException() =>
            new(segmentId, "provider-a", new InvalidDataException("CRC mismatch"));

        public override int Read(byte[] buffer, int offset, int count) =>
            throw CreateException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(CreateException());
    }
}
