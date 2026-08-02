namespace NzbWebDAV.Database.Models;

/// <summary>
/// How segment byte ranges were determined for a file. Values are persisted
/// in MemoryPack blobs — do not reorder or reuse ordinals.
/// </summary>
public enum GeometrySource : byte
{
    /// <summary>
    /// Inferred from first + last segment only (current default; also the
    /// automatic value for blobs written before this field existed).
    /// </summary>
    Inferred = 0,

    /// <summary>
    /// Probed first, second, and last segment to detect uniformity.
    /// </summary>
    SmartProbed = 1,

    /// <summary>
    /// Every segment was individually verified.
    /// </summary>
    FullyProbed = 2,

    /// <summary>
    /// Accumulated from runtime playback observations of all segments.
    /// </summary>
    RuntimeLearned = 3,
}
