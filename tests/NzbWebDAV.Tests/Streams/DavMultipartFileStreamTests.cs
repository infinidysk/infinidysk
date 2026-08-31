using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;

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
    public async Task ReadAsync_TailOfPersistedLazyPartWithTrailingArchiveBytes_Succeeds()
    {
        var volumeBytes = Enumerable.Range(0, 16).Select(x => (byte)x).ToArray();
        using var client = new FakeNntpClient(
            new Dictionary<string, byte[]> { ["segment"] = volumeBytes },
            useCachedYencStreams: true,
            segmentRanges: new Dictionary<string, LongRange> { ["segment"] = new(0, 16) });
        // Mimic an already-persisted lazy part where the recorded packed-data
        // end excludes the trailing RAR structure in its final yEnc segment.
        var multipart = MultipartFile(
            segmentRange: LongRange.FromStartAndSize(0, 12),
            fileRange: LongRange.FromStartAndSize(4, 8));
        var previousBudget = NzbWebDAV.WebDav.Requests.RangeContext.GetReadBudget();
        NzbWebDAV.WebDav.Requests.RangeContext.SetReadBudget(1);
        try
        {
            await using var stream = new DavMultipartFileStream(
                multipart,
                client,
                articleBufferSize: 0,
                resolver: null,
                usePipelinedBodyRequests: false,
                fileName: "movie.mkv");
            stream.Seek(7, SeekOrigin.Begin);

            var buffer = new byte[1];
            Assert.Equal(1, await stream.ReadAsync(buffer));
            Assert.Equal(11, buffer[0]);
        }
        finally
        {
            NzbWebDAV.WebDav.Requests.RangeContext.SetReadBudget(previousBudget);
        }
    }

    [Fact]
    public async Task ReadAsync_ExactIndexedOffsetDelegatesFirstByteBeforeContainingBodyEof()
    {
        var volumeOne = Enumerable.Range(0, 8).Select(x => (byte)x).ToArray();
        var volumeTwo = Enumerable.Range(8, 8).Select(x => (byte)x).ToArray();
        var staged = new StagedBodyStream(
            prefix: volumeTwo[..2],
            requested: volumeTwo[2..3],
            tail: volumeTwo[3..]);
        using var client = new FakeNntpClient(
            new Dictionary<string, byte[]>
            {
                ["one"] = volumeOne,
                ["two"] = volumeTwo,
            },
            useCachedYencStreams: true,
            segmentRanges: new Dictionary<string, LongRange>
            {
                ["one"] = new(0, 8),
                ["two"] = new(8, 16),
            },
            decodedStreamFactory: (id, bytes) =>
                id == "two" ? staged : new MemoryStream(bytes, writable: false));
        var multipart = new DavMultipartFile
        {
            Id = Guid.NewGuid(),
            Metadata = new DavMultipartFile.Meta
            {
                FileParts =
                [
                    new DavMultipartFile.FilePart
                    {
                        SegmentIds = ["one", "two"],
                        SegmentIdByteRange = new LongRange(0, 16),
                        FilePartByteRange = new LongRange(0, 16),
                        SegmentByteRanges = [new LongRange(0, 8), new LongRange(8, 16)],
                        SegmentByteRangesTrusted = true,
                    }
                ],
            },
        };
        var previousBudget = NzbWebDAV.WebDav.Requests.RangeContext.GetReadBudget();
        NzbWebDAV.WebDav.Requests.RangeContext.SetReadBudget(2L * 1024 * 1024);
        try
        {
            await using var stream = new DavMultipartFileStream(
                multipart,
                client,
                articleBufferSize: 4,
                resolver: null,
                usePipelinedBodyRequests: false,
                fileName: "movie.mkv");
            stream.Seek(10, SeekOrigin.Begin);

            var buffer = new byte[1];
            Assert.Equal(1, await stream.ReadAsync(buffer));
            Assert.Equal(10, buffer[0]);
            Assert.True(staged.TailGateClosed);
            Assert.False(client.BodyRequestCounts.ContainsKey("one"));
            Assert.Equal(1, client.BodyRequestCounts["two"]);
        }
        finally
        {
            NzbWebDAV.WebDav.Requests.RangeContext.SetReadBudget(previousBudget);
        }
    }

    [Fact]
    public async Task ReadAsync_PersistedLazyPartFindsPenultimateSegmentBeforeTrailingArchiveBytes()
    {
        var segmentIds = new[] { "one", "two", "three" };
        var segments = segmentIds.ToDictionary(
            id => id,
            _ => Enumerable.Range(0, 10).Select(value => (byte)value).ToArray());
        var ranges = new[]
        {
            new LongRange(0, 10),
            new LongRange(10, 20),
            new LongRange(20, 30),
        };
        using var client = new FakeNntpClient(
            segments,
            useCachedYencStreams: true,
            segmentRanges: segmentIds.Zip(ranges).ToDictionary(pair => pair.First, pair => pair.Second));
        var multipart = new DavMultipartFile
        {
            Id = Guid.NewGuid(),
            Metadata = new DavMultipartFile.Meta
            {
                FileParts =
                [
                    new DavMultipartFile.FilePart
                    {
                        SegmentIds = segmentIds,
                        SegmentIdByteRange = new LongRange(0, 24),
                        FilePartByteRange = new LongRange(0, 24),
                    }
                ],
            },
        };
        var previousBudget = NzbWebDAV.WebDav.Requests.RangeContext.GetReadBudget();
        NzbWebDAV.WebDav.Requests.RangeContext.SetReadBudget(1024 * 1024 - 1);
        try
        {
            await using var stream = new DavMultipartFileStream(
                multipart,
                client,
                articleBufferSize: 0,
                resolver: null,
                usePipelinedBodyRequests: false,
                fileName: "movie.mkv");
            stream.Seek(18, SeekOrigin.Begin);

            var buffer = new byte[1];
            Assert.Equal(1, await stream.ReadAsync(buffer));
            Assert.Equal(8, buffer[0]);
        }
        finally
        {
            NzbWebDAV.WebDav.Requests.RangeContext.SetReadBudget(previousBudget);
        }
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
    public async Task ReadAsync_PendingPartsWithoutResolver_EndsBeforeDeclaredLength_ThrowsIncompleteFileContent()
    {
        using var client = new FakeNntpClient(new Dictionary<string, byte[]>
        {
            ["segment"] = Enumerable.Range(0, 8).Select(x => (byte)x).ToArray(),
        }, useCachedYencStreams: true);
        var multipart = MultipartFile(
            segmentRange: LongRange.FromStartAndSize(0, 8),
            fileRange: LongRange.FromStartAndSize(0, 8));
        multipart.Metadata.PendingParts =
        [
            new DavMultipartFile.PendingPart
            {
                SegmentIds = ["vol2-seg"],
                SegmentIdByteRange = LongRange.FromStartAndSize(0, 8),
                EstimatedDataSize = 8,
            }
        ];
        await using var stream = new DavMultipartFileStream(
            multipart,
            client,
            articleBufferSize: 0,
            resolver: null,
            usePipelinedBodyRequests: false,
            fileName: "movie.mkv");

        var buffer = new byte[16];
        Assert.Equal(8, await stream.ReadAsync(buffer.AsMemory(0, 8)));

        var failure = await Assert.ThrowsAsync<IncompleteFileContentException>(async () =>
        {
            _ = await stream.ReadAsync(buffer.AsMemory(8));
        });

        Assert.Equal(16, failure.ExpectedBytes);
        Assert.Equal(8, failure.DeliveredBytes);
        Assert.Contains("movie.mkv", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_ExpectedFileSizeKeepsRecoveredLegacyLengthStable()
    {
        using var client = new FakeNntpClient(new Dictionary<string, byte[]>
        {
            ["segment"] = Enumerable.Range(0, 8).Select(x => (byte)x).ToArray(),
        }, useCachedYencStreams: true);
        var multipart = MultipartFile(
            segmentRange: LongRange.FromStartAndSize(0, 8),
            fileRange: LongRange.FromStartAndSize(0, 8));
        multipart.Metadata.IsLazy = true;
        multipart.Metadata.ExpectedFileSize = 8;
        multipart.Metadata.PendingParts =
        [
            new DavMultipartFile.PendingPart
            {
                SegmentIds = ["unrelated-tail"],
                SegmentIdByteRange = LongRange.FromStartAndSize(0, 8),
                EstimatedDataSize = 8,
            }
        ];
        await using var stream = new DavMultipartFileStream(
            multipart,
            client,
            articleBufferSize: 0,
            resolver: null,
            usePipelinedBodyRequests: false,
            fileName: "movie.mkv");

        var buffer = new byte[8];
        Assert.Equal(8, await stream.ReadAsync(buffer));
        Assert.Equal(0, await stream.ReadAsync(new byte[1]));
        Assert.Equal(8, stream.Length);
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
