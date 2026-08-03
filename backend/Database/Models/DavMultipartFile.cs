using MemoryPack;
using NzbWebDAV.Models;

namespace NzbWebDAV.Database.Models;

[MemoryPackable(GenerateType.VersionTolerant)]
public partial class DavMultipartFile
{
    [MemoryPackOrder(0)]
    public Guid Id { get; set; } // foreign key to DavItem.Id

    [MemoryPackOrder(1)]
    public Meta Metadata { get; set; } = null!;

    // navigation helpers
    [MemoryPackIgnore]
    public DavItem? DavItem { get; set; }

    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial class Meta
    {
        [MemoryPackOrder(0)]
        public AesParams? AesParams { get; set; }

        [MemoryPackOrder(1)]
        public FilePart[] FileParts { get; set; } = [];

        // Lazy RAR fields. When IsLazy=true, FileParts holds only the
        // already-resolved leading parts (at least the first). Trailing
        // parts live in PendingParts and get resolved on demand by
        // LazyRarResolver, which appends them to FileParts and persists.
        [MemoryPackOrder(2)]
        public bool IsLazy { get; set; }

        [MemoryPackOrder(3)]
        public string? PathInArchive { get; set; }

        [MemoryPackOrder(4)]
        public string? ArchivePassword { get; set; }

        [MemoryPackOrder(5)]
        public PendingPart[] PendingParts { get; set; } = [];
    }

    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial class FilePart
    {
        // a subsequence of segments from an NzbFile
        [MemoryPackOrder(0)]
        public string[] SegmentIds { get; set; } = [];

        // what byte range is contained within the segmentIds? (relative to the full NzbFile)
        [MemoryPackOrder(1)]
        public LongRange SegmentIdByteRange { get; set; } = default!;

        // what byte range contains the file part contents? (relative to the full NzbFile)
        // note: this range should always be fully contained within the SegmentIdByteRange above.
        [MemoryPackOrder(2)]
        public LongRange FilePartByteRange { get; set; } = default!;

        [MemoryPackOrder(3)]
        public LongRange[]? SegmentByteRanges { get; set; }

        [MemoryPackOrder(4)]
        public string[][]? SegmentFallbackIds { get; set; }
    }

    // A RAR part whose internal byte range hasn't been parsed yet.
    // LazyRarResolver materializes it into a FilePart on first read.
    // EstimatedDataSize is the worst-case data contribution (raw NzbFile
    // size minus a fixed RAR continuation-header guess) used only to
    // route seeks before resolution — the exact range replaces it once
    // resolved, and downstream reads are constrained by the real range.
    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial class PendingPart
    {
        [MemoryPackOrder(0)]
        public string[] SegmentIds { get; set; } = [];

        [MemoryPackOrder(1)]
        public LongRange SegmentIdByteRange { get; set; } = default!;

        [MemoryPackOrder(2)]
        public long EstimatedDataSize { get; set; }

        [MemoryPackOrder(3)]
        public string[][]? SegmentFallbackIds { get; set; }
    }
}
