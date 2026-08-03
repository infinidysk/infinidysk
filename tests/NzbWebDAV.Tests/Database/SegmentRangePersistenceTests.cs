using MemoryPack;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Queue.FileProcessors;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Database;

public class SegmentRangePersistenceTests
{
    [Fact]
    public void DavNzbFile_SegmentRanges_RoundTrip()
    {
        var original = new DavNzbFile
        {
            Id = Guid.NewGuid(),
            SegmentIds = ["seg0", "seg1", "seg2", "seg3"],
            SegmentByteRanges =
            [
                new LongRange(0, 700_000),
                new LongRange(700_000, 1_400_000),
                new LongRange(1_400_000, 2_100_000),
                new LongRange(2_100_000, 2_300_000),
            ],
        };

        var bytes = MemoryPackSerializer.Serialize(original);
        var deserialized = MemoryPackSerializer.Deserialize<DavNzbFile>(bytes)!;

        Assert.Equal(original.Id, deserialized.Id);
        Assert.Equal(original.SegmentIds, deserialized.SegmentIds);
        Assert.Equal(original.SegmentByteRanges, deserialized.SegmentByteRanges);
    }

    [Fact]
    public async Task ProbeSecondSegmentRange_ValidatesAndMaterializesPersistableRanges()
    {
        var nzbFile = CreateFourSegmentFile();
        var client = new HeaderProbeNntpClient(new Dictionary<string, LongRange>
        {
            ["seg1"] = new LongRange(700_000, 1_400_000),
        });

        await nzbFile.ProbeSecondSegmentRangeAsync(client, 2_300_000, CancellationToken.None);

        Assert.Equal(1, client.HeaderRequestCount);
        var ranges = Assert.IsType<LongRange[]>(nzbFile.GetSegmentByteRanges());
        Assert.Equal(
            [
                new LongRange(0, 700_000),
                new LongRange(700_000, 1_400_000),
                new LongRange(1_400_000, 2_100_000),
                new LongRange(2_100_000, 2_300_000),
            ],
            ranges);
    }

    [Fact]
    public async Task ProbeSecondSegmentRange_RejectsNonUniformInference()
    {
        var nzbFile = CreateFourSegmentFile();
        nzbFile.Segments[^1].ByteRange = new LongRange(2_000_000, 2_300_000);
        var client = new HeaderProbeNntpClient(new Dictionary<string, LongRange>
        {
            ["seg1"] = new LongRange(700_000, 1_350_000),
        });

        await nzbFile.ProbeSecondSegmentRangeAsync(client, 2_300_000, CancellationToken.None);

        Assert.Equal(1, client.HeaderRequestCount);
        Assert.Null(nzbFile.GetSegmentByteRanges());
    }

    [Fact]
    public async Task ProbeSecondSegmentRange_ReusesKnownSecondRangeWithoutAnotherRequest()
    {
        var nzbFile = CreateFourSegmentFile();
        nzbFile.Segments[1].ByteRange = new LongRange(700_000, 1_400_000);
        var client = new HeaderProbeNntpClient(new Dictionary<string, LongRange>());

        await nzbFile.ProbeSecondSegmentRangeAsync(client, 2_300_000, CancellationToken.None);

        Assert.Equal(0, client.HeaderRequestCount);
        Assert.NotNull(nzbFile.GetSegmentByteRanges());
    }

    [Fact]
    public async Task ProbeSecondSegmentRange_SkipsFilesWithoutAMiddleSegment()
    {
        var nzbFile = new NzbFile { Subject = "small.txt" };
        nzbFile.Segments.Add(new NzbSegment
        {
            MessageId = "seg0",
            Bytes = 50_000,
            ByteRange = new LongRange(0, 50_000),
        });
        nzbFile.Segments.Add(new NzbSegment
        {
            MessageId = "seg1",
            Bytes = 30_000,
            ByteRange = new LongRange(50_000, 80_000),
        });
        var client = new HeaderProbeNntpClient(new Dictionary<string, LongRange>());

        await nzbFile.ProbeSecondSegmentRangeAsync(client, 80_000, CancellationToken.None);

        Assert.Equal(0, client.HeaderRequestCount);
        Assert.NotNull(nzbFile.GetSegmentByteRanges());
    }

    [Fact]
    public async Task ProbeSecondSegmentRange_FailureDisablesUnvalidatedInference()
    {
        var nzbFile = CreateFourSegmentFile();
        nzbFile.Segments[^1].ByteRange = new LongRange(2_100_000, 2_300_000);
        var client = new HeaderProbeNntpClient(new Dictionary<string, LongRange>());

        await nzbFile.ProbeSecondSegmentRangeAsync(client, 2_300_000, CancellationToken.None);

        Assert.Equal(1, client.HeaderRequestCount);
        Assert.Null(nzbFile.GetSegmentByteRanges());
    }

    [Fact]
    public async Task MultipartMkvProcessor_ValidatesInferenceBeforePersistingPartRanges()
    {
        var uniform = CreateFourSegmentFile("a-");
        var nonUniform = CreateFourSegmentFile("b-");
        var client = new HeaderProbeNntpClient(new Dictionary<string, LongRange>
        {
            ["a-seg1"] = new LongRange(700_000, 1_400_000), // confirms the uniform split
            ["b-seg1"] = new LongRange(700_000, 1_350_000), // rejects the inference
        });
        var processor = new MultipartMkvProcessor(
            [
                new GetFileInfosStep.FileInfo
                {
                    NzbFile = uniform,
                    FileName = "movie.mkv.001",
                    ReleaseDate = DateTimeOffset.UtcNow,
                    FileSize = 2_300_000,
                },
                new GetFileInfosStep.FileInfo
                {
                    NzbFile = nonUniform,
                    FileName = "movie.mkv.002",
                    ReleaseDate = DateTimeOffset.UtcNow,
                    FileSize = 2_300_000,
                },
            ],
            client,
            CancellationToken.None);

        var result = Assert.IsType<MultipartMkvProcessor.Result>(await processor.ProcessAsync());

        Assert.Equal(2, client.HeaderRequestCount);
        Assert.NotNull(result.Parts[0].SegmentByteRanges);
        Assert.Null(result.Parts[1].SegmentByteRanges);
    }

    private static NzbFile CreateFourSegmentFile(string prefix = "")
    {
        var nzbFile = new NzbFile { Subject = "\"test.mkv\"" };
        nzbFile.Segments.Add(new NzbSegment
        {
            MessageId = $"{prefix}seg0",
            Bytes = 700_000,
            ByteRange = new LongRange(0, 700_000),
        });
        nzbFile.Segments.Add(new NzbSegment { MessageId = $"{prefix}seg1", Bytes = 700_000 });
        nzbFile.Segments.Add(new NzbSegment { MessageId = $"{prefix}seg2", Bytes = 700_000 });
        nzbFile.Segments.Add(new NzbSegment { MessageId = $"{prefix}seg3", Bytes = 200_000 });
        return nzbFile;
    }

    private sealed class HeaderProbeNntpClient(
        IReadOnlyDictionary<string, LongRange> ranges) : WrappingNntpClient(null!)
    {
        public int HeaderRequestCount { get; private set; }

        public override Task<UsenetYencHeader> GetYencHeadersAsync(
            string segmentId,
            CancellationToken ct)
        {
            HeaderRequestCount++;
            if (!ranges.TryGetValue(segmentId, out var range))
                throw new InvalidOperationException($"No header configured for {segmentId}");

            return Task.FromResult(new UsenetYencHeader
            {
                PartOffset = range.StartInclusive,
                PartSize = range.Count,
                LineLength = 128,
                PartNumber = 2,
                TotalParts = 4,
                FileName = "test.mkv",
                FileSize = 2_300_000,
            });
        }
    }
}
