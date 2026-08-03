using System.ComponentModel.DataAnnotations.Schema;
using MemoryPack;
using NzbWebDAV.Models;

namespace NzbWebDAV.Database.Models;

[MemoryPackable(GenerateType.VersionTolerant)]
public partial class DavNzbFile
{
    [MemoryPackOrder(0)]
    public Guid Id { get; set; } // foreign key to DavItem.Id

    [MemoryPackOrder(1)]
    public string[] SegmentIds { get; set; } = [];

    [NotMapped]
    [MemoryPackOrder(2)]
    public LongRange[]? SegmentByteRanges { get; set; }

    /// <summary>
    /// Per-segment alternate MessageIds aligned with <see cref="SegmentIds"/>.
    /// Null on blobs written before this field existed.
    /// Blob/MemoryPack only — not an EF column (nested string[][] is unsupported).
    /// </summary>
    [NotMapped]
    [MemoryPackOrder(3)]
    public string[][]? SegmentFallbackIds { get; set; }

    // navigation helpers
    [MemoryPackIgnore]
    public DavItem? DavItem { get; set; }
}
