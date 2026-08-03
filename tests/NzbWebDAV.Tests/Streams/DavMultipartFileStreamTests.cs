using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Tests.Fakes;

namespace NzbWebDAV.Tests.Streams;

[Collection(nameof(ConfigPathCollection))]
public class DavMultipartFileStreamTests
{
    [Fact]
    public void GetEffectivePartLength_UsesPackedRangeEndForUnderestimatedVolume()
    {
        var part = MultipartFile(
            segmentRange: LongRange.FromStartAndSize(0, 8),
            fileRange: LongRange.FromStartAndSize(4, 12))
            .Metadata.FileParts[0];

        Assert.Equal(16, DavMultipartFileStream.GetEffectivePartLength(part));
    }

    [Fact]
    public async Task ReadAsync_HealsUnderestimatedVolumeLength()
    {
        var volumeBytes = Enumerable.Range(0, 16).Select(x => (byte)x).ToArray();
        using var client = new FakeNntpClient(new Dictionary<string, byte[]>
        {
            ["segment"] = volumeBytes,
        }, useCachedYencStreams: true);
        var multipart = MultipartFile(
            segmentRange: LongRange.FromStartAndSize(0, 8),
            fileRange: LongRange.FromStartAndSize(4, 12));
        await using var stream = new DavMultipartFileStream(
            multipart,
            client,
            articleBufferSize: 0,
            resolver: null,
            usePipelinedBodyRequests: false,
            fileName: "movie.mkv");

        var buffer = new byte[12];
        var bytesRead = await stream.ReadAsync(buffer);

        Assert.Equal(buffer.Length, bytesRead);
        Assert.Equal(volumeBytes[4..], buffer);
    }

    [Fact]
    public void Read_PreservesSynchronousArchiveParserCompatibility()
    {
        var volumeBytes = Enumerable.Range(0, 16).Select(x => (byte)x).ToArray();
        using var client = new FakeNntpClient(new Dictionary<string, byte[]>
        {
            ["segment"] = volumeBytes,
        }, useCachedYencStreams: true);
        var multipart = MultipartFile(
            segmentRange: LongRange.FromStartAndSize(0, 8),
            fileRange: LongRange.FromStartAndSize(4, 12));
        using var stream = new DavMultipartFileStream(
            multipart,
            client,
            articleBufferSize: 0,
            resolver: null,
            usePipelinedBodyRequests: false,
            fileName: "movie.mkv");

        var buffer = new byte[12];
        var bytesRead = stream.Read(buffer, 0, buffer.Length);

        Assert.Equal(buffer.Length, bytesRead);
        Assert.Equal(volumeBytes[4..], buffer);
    }

    [Fact]
    public async Task ReadAsync_InvalidSegmentRangeThrowsKnownSeekError()
    {
        using var client = new FakeNntpClient(new Dictionary<string, byte[]>());
        var multipart = MultipartFile(
            segmentRange: LongRange.FromStartAndSize(1, 8),
            fileRange: LongRange.FromStartAndSize(4, 4));
        await using var stream = new DavMultipartFileStream(
            multipart,
            client,
            articleBufferSize: 0,
            resolver: null,
            usePipelinedBodyRequests: false,
            fileName: "movie.mkv");

        await Assert.ThrowsAsync<SeekPositionNotFoundException>(
            () => stream.ReadAsync(new byte[1], 0, 1));
    }

    [Fact]
    public async Task ReadAsync_UnencryptedShortVolume_FailsWithPartProvenance()
    {
        using var client = new FakeNntpClient(new Dictionary<string, byte[]>
        {
            ["segment"] = Enumerable.Range(0, 8).Select(x => (byte)x).ToArray(),
        }, useCachedYencStreams: true);
        var multipart = MultipartFile(
            segmentRange: LongRange.FromStartAndSize(0, 12),
            fileRange: LongRange.FromStartAndSize(0, 12));
        await using var stream = new DavMultipartFileStream(
            multipart,
            client,
            articleBufferSize: 0,
            resolver: null,
            usePipelinedBodyRequests: false,
            fileName: "movie.mkv");

        var failure = await Assert.ThrowsAsync<IncompleteMultipartPartException>(
            () => ReadFullyAsync(stream, 12));

        Assert.Contains("delivered 8 of 12 expected bytes", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Part 1 of 1", failure.Message, StringComparison.Ordinal);
        Assert.Contains("encrypted: False", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_EncryptedVolumeShortByLessThanAnAesBlock_KeepsFollowingOffsets()
    {
        using var client = new FakeNntpClient(new Dictionary<string, byte[]>
        {
            ["segment"] = Enumerable.Range(1, 12).Select(x => (byte)x).ToArray(),
        }, useCachedYencStreams: true);
        var multipart = MultipartFile(
            segmentRange: LongRange.FromStartAndSize(0, 16),
            fileRange: LongRange.FromStartAndSize(0, 16));
        multipart.Metadata.AesParams = new AesParams();
        await using var stream = new DavMultipartFileStream(
            multipart,
            client,
            articleBufferSize: 0,
            resolver: null,
            usePipelinedBodyRequests: false,
            fileName: "movie.mkv");

        var bytes = await ReadFullyAsync(stream, 16);

        Assert.Equal(Enumerable.Range(1, 12).Select(x => (byte)x), bytes[..12]);
        Assert.Equal(new byte[4], bytes[12..]);
    }

    [Fact]
    public async Task ReadAsync_UnresolvableTrailingVolume_DoesNotLookLikeEndOfFile()
    {
        using var client = new FakeNntpClient(new Dictionary<string, byte[]>
        {
            ["segment"] = Enumerable.Range(0, 8).Select(x => (byte)x).ToArray(),
        }, useCachedYencStreams: true);
        var multipart = MultipartFile(
            segmentRange: LongRange.FromStartAndSize(0, 8),
            fileRange: LongRange.FromStartAndSize(0, 8));
        multipart.Metadata.IsLazy = true;
        multipart.Metadata.PathInArchive = "movie.mkv";
        multipart.Metadata.PendingParts =
        [
            new DavMultipartFile.PendingPart
            {
                SegmentIds = ["vol2-seg0"],
                SegmentIdByteRange = LongRange.FromStartAndSize(0, 8),
                EstimatedDataSize = 8,
            }
        ];
        await using var stream = new DavMultipartFileStream(
            multipart,
            client,
            articleBufferSize: 0,
            resolver: new StalledRarResolver(client),
            usePipelinedBodyRequests: false,
            fileName: "movie.mkv");

        var failure = await Assert.ThrowsAsync<IncompleteMultipartPartException>(
            () => ReadFullyAsync(stream, 16));

        Assert.Contains("Volume 2 of \"movie.mkv\" could not be resolved",
            failure.Message, StringComparison.Ordinal);
    }

    private static async Task<byte[]> ReadFullyAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset));
            if (read == 0) break;
            offset += read;
        }

        Assert.Equal(count, offset);
        return buffer;
    }

    // Stands in for a resolver that returns without materializing the volume it was
    // asked for — the case where the archive layout could not be read.
    private sealed class StalledRarResolver(INntpClient client)
        : LazyRarResolver(client, new ConfigManager())
    {
        public override Task<DavMultipartFile.Meta> ResolveNextAsync(
            DavMultipartFile mpf, CancellationToken ct) =>
            Task.FromResult(mpf.Metadata);
    }

    private static DavMultipartFile MultipartFile(LongRange segmentRange, LongRange fileRange) =>
        new()
        {
            Id = Guid.NewGuid(),
            Metadata = new DavMultipartFile.Meta
            {
                FileParts =
                [
                    new DavMultipartFile.FilePart
                    {
                        SegmentIds = ["segment"],
                        SegmentIdByteRange = segmentRange,
                        FilePartByteRange = fileRange,
                    }
                ],
            },
        };
}
