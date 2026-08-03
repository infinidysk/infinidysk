using MemoryPack;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Database;

public class SegmentGeometryTests
{
    [Fact]
    public void DavNzbFile_NewFields_RoundTrip()
    {
        var original = new DavNzbFile
        {
            Id = Guid.NewGuid(),
            SegmentIds = ["seg1", "seg2", "seg3"],
            SegmentByteRanges = [new LongRange(0, 700_000), new LongRange(700_000, 1_400_000), new LongRange(1_400_000, 1_600_000)],
            SegmentFallbackIds = null,
            GeometrySource = GeometrySource.SmartProbed,
            IsUniformSegmentSize = true,
            UniformSegmentSize = 700_000,
        };

        var bytes = MemoryPackSerializer.Serialize(original);
        var deserialized = MemoryPackSerializer.Deserialize<DavNzbFile>(bytes)!;

        Assert.Equal(original.Id, deserialized.Id);
        Assert.Equal(original.SegmentIds, deserialized.SegmentIds);
        Assert.Equal(original.GeometrySource, deserialized.GeometrySource);
        Assert.True(deserialized.IsUniformSegmentSize);
        Assert.Equal(700_000, deserialized.UniformSegmentSize);
    }

    [Fact]
    public void DavNzbFile_OldBlob_DeserializesWithInferredDefaults()
    {
        // Simulate an old blob that only has fields 0-3 (no geometry fields)
        var oldBlob = new DavNzbFile
        {
            Id = Guid.NewGuid(),
            SegmentIds = ["seg1"],
            SegmentByteRanges = [new LongRange(0, 500_000)],
            SegmentFallbackIds = null,
        };

        var bytes = MemoryPackSerializer.Serialize(oldBlob);
        var deserialized = MemoryPackSerializer.Deserialize<DavNzbFile>(bytes)!;

        Assert.Equal(GeometrySource.Inferred, deserialized.GeometrySource);
        Assert.False(deserialized.IsUniformSegmentSize);
        Assert.Equal(0, deserialized.UniformSegmentSize);
    }

    [Fact]
    public void FilePart_NewFields_RoundTrip()
    {
        var original = new DavMultipartFile.FilePart
        {
            SegmentIds = ["seg1", "seg2"],
            SegmentIdByteRange = new LongRange(0, 1_400_000),
            FilePartByteRange = new LongRange(0, 1_400_000),
            SegmentByteRanges = [new LongRange(0, 700_000), new LongRange(700_000, 1_400_000)],
            SegmentFallbackIds = null,
            GeometrySource = GeometrySource.SmartProbed,
            IsUniformSegmentSize = true,
            UniformSegmentSize = 700_000,
        };

        var bytes = MemoryPackSerializer.Serialize(original);
        var deserialized = MemoryPackSerializer.Deserialize<DavMultipartFile.FilePart>(bytes)!;

        Assert.Equal(GeometrySource.SmartProbed, deserialized.GeometrySource);
        Assert.True(deserialized.IsUniformSegmentSize);
        Assert.Equal(700_000, deserialized.UniformSegmentSize);
    }

    [Fact]
    public async Task NzbFile_ProbeSecondSegment_DetectsUniformGeometry()
    {
        var nzbFile = new NzbFile { Subject = "test.mkv" };
        nzbFile.Segments.Add(new NzbSegment { MessageId = "seg0", Bytes = 700_000, ByteRange = new LongRange(0, 700_000) });
        nzbFile.Segments.Add(new NzbSegment { MessageId = "seg1", Bytes = 700_000 });
        nzbFile.Segments.Add(new NzbSegment { MessageId = "seg2", Bytes = 500_000 });

        var client = new FakeNntpClientForGeometry(new Dictionary<string, (long offset, long size)>
        {
            ["seg1"] = (700_000, 700_000),
        });

        await nzbFile.ProbeSecondSegmentGeometryAsync(client, CancellationToken.None);

        Assert.Equal(GeometrySource.SmartProbed, nzbFile.GeometrySource);
        Assert.True(nzbFile.IsUniformSegmentSize);
        Assert.Equal(700_000, nzbFile.UniformSegmentSize);
    }

    [Fact]
    public async Task NzbFile_ProbeSecondSegment_DetectsNonUniform()
    {
        var nzbFile = new NzbFile { Subject = "test.mkv" };
        nzbFile.Segments.Add(new NzbSegment { MessageId = "seg0", Bytes = 700_000, ByteRange = new LongRange(0, 700_000) });
        nzbFile.Segments.Add(new NzbSegment { MessageId = "seg1", Bytes = 700_000 });
        nzbFile.Segments.Add(new NzbSegment { MessageId = "seg2", Bytes = 500_000 });

        var client = new FakeNntpClientForGeometry(new Dictionary<string, (long offset, long size)>
        {
            ["seg1"] = (700_000, 650_000),
        });

        await nzbFile.ProbeSecondSegmentGeometryAsync(client, CancellationToken.None);

        Assert.Equal(GeometrySource.SmartProbed, nzbFile.GeometrySource);
        Assert.False(nzbFile.IsUniformSegmentSize);
        Assert.Equal(0, nzbFile.UniformSegmentSize);
    }

    [Fact]
    public async Task NzbFile_ProbeSecondSegment_SkipsFilesWithFewerThan3Segments()
    {
        var nzbFile = new NzbFile { Subject = "small.txt" };
        nzbFile.Segments.Add(new NzbSegment { MessageId = "seg0", Bytes = 50_000, ByteRange = new LongRange(0, 50_000) });
        nzbFile.Segments.Add(new NzbSegment { MessageId = "seg1", Bytes = 30_000 });

        var client = new FakeNntpClientForGeometry(new Dictionary<string, (long offset, long size)>());

        await nzbFile.ProbeSecondSegmentGeometryAsync(client, CancellationToken.None);

        Assert.Equal(GeometrySource.Inferred, nzbFile.GeometrySource);
        Assert.False(nzbFile.IsUniformSegmentSize);
    }

    private sealed class FakeNntpClientForGeometry(
        Dictionary<string, (long offset, long size)> headers) : WrappingNntpClient(null!)
    {
        public override Task<UsenetYencHeader> GetYencHeadersAsync(
            string segmentId, CancellationToken ct)
        {
            if (headers.TryGetValue(segmentId, out var h))
                return Task.FromResult(new UsenetYencHeader
                {
                    PartOffset = h.offset,
                    PartSize = h.size,
                    LineLength = 128,
                    PartNumber = 2,
                    TotalParts = 10,
                    FileName = "test.mkv",
                    FileSize = 2_000_000,
                });
            throw new Exception($"Segment {segmentId} not configured in fake");
        }
    }
}
