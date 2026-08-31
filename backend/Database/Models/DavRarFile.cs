using MemoryPack;
using NzbWebDAV.Models;

namespace NzbWebDAV.Database.Models;

[MemoryPackable(GenerateType.VersionTolerant)]
public partial class DavRarFile
{
    [MemoryPackOrder(0)]
    public Guid Id { get; set; } // foreign key to DavItem.Id

    [MemoryPackOrder(1)]
    public RarPart[] RarParts { get; set; } = [];

    // navigation helpers
    [MemoryPackIgnore]
    public DavItem? DavItem { get; set; }

    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial class RarPart
    {
        [MemoryPackOrder(0)]
        public string[] SegmentIds { get; set; } = [];

        [MemoryPackOrder(1)]
        public long PartSize { get; set; }

        [MemoryPackOrder(2)]
        public long Offset { get; set; }

        [MemoryPackOrder(3)]
        public long ByteCount { get; set; }

        [MemoryPackOrder(4)]
        public LongRange[]? SegmentByteRanges { get; set; }

        [MemoryPackOrder(5)]
        public string[][]? SegmentFallbackIds { get; set; }

        // Null on legacy blobs. Only true permits arithmetic segment seeking.
        [MemoryPackOrder(6)]
        public bool? SegmentByteRangesTrusted { get; set; }
    }

    public DavMultipartFile.Meta ToDavMultipartFileMeta()
    {
        return new DavMultipartFile.Meta
        {
            FileParts = RarParts.Select(x => new DavMultipartFile.FilePart()
            {
                SegmentIds = x.SegmentIds,
                SegmentIdByteRange = LongRange.FromStartAndSize(
                    0,
                    Math.Max(x.PartSize, checked(x.Offset + x.ByteCount))),
                FilePartByteRange = LongRange.FromStartAndSize(x.Offset, x.ByteCount),
                SegmentByteRanges = x.SegmentByteRanges,
                SegmentFallbackIds = x.SegmentFallbackIds,
                SegmentByteRangesTrusted = x.SegmentByteRangesTrusted,
            }).ToArray()
        };
    }
}
