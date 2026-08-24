namespace NzbWebDAV.Api.Errors;

/// <summary>
/// Bounded NZB ingest limits. Defaults are high enough for legitimate large
/// releases (selected above current accepted fixtures) and are enforced before
/// a queue item is inserted.
/// </summary>
public sealed class NzbInputLimits
{
    public static NzbInputLimits Default { get; } = new();

    public int MaxXmlBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxFiles { get; init; } = 10_000;
    public int MaxTotalSegments { get; init; } = 500_000;
    public int MaxNameLength { get; init; } = 255;

    /// <summary>
    /// Cap on the NZB <c>&lt;file subject&gt;</c> attribute. Subjects are Usenet
    /// header lines (index prefix, quoted filename, yEnc part counts), not
    /// filenames, so they routinely exceed <see cref="MaxNameLength"/>.
    /// </summary>
    public int MaxSubjectLength { get; init; } = 1024;
    public int MaxMessageIdLength { get; init; } = 248;
}
