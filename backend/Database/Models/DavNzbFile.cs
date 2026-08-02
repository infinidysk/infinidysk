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

    /// <summary>
    /// How <see cref="SegmentByteRanges"/> were determined. Defaults to
    /// <see cref="GeometrySource.Inferred"/> for blobs written before this field existed.
    /// </summary>
    [NotMapped]
    [MemoryPackOrder(4)]
    public GeometrySource GeometrySource { get; set; }

    /// <summary>
    /// Whether all non-final segments have the same decoded byte size.
    /// When true, <see cref="UniformSegmentSize"/> holds that size.
    /// </summary>
    [NotMapped]
    [MemoryPackOrder(5)]
    public bool IsUniformSegmentSize { get; set; }

    /// <summary>
    /// The decoded byte size of each non-final segment when <see cref="IsUniformSegmentSize"/>
    /// is true. Zero when segment sizes are non-uniform or not yet determined.
    /// </summary>
    [NotMapped]
    [MemoryPackOrder(6)]
    public long UniformSegmentSize { get; set; }

    // navigation helpers
    [MemoryPackIgnore]
    public DavItem? DavItem { get; set; }
}
