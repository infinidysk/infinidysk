using NzbWebDAV.Queue.NestedRarExpansion;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Queue.Inspection;

/// <summary>
/// Defensive caps for NZB inspection, sized to accommodate large season packs and archive
/// sets while bounding the work one admin request can cause; they are guardrails, not tuned
/// optima. Reaching any cap stops inspection with an incomplete
/// manifest and a <see cref="InspectionFailureKinds.LimitExceeded"/> failure; a partial
/// manifest is never presented as complete.
/// </summary>
public sealed class NzbInspectionLimits
{
    public static NzbInspectionLimits Default { get; } = new();

    /// <summary>Largest NZB document accepted (the same cap applied to NZBs fetched from indexers).</summary>
    public long MaxNzbBytes { get; init; } = NzbFetchLimits.MaxResponseBytes;

    /// <summary>NZB files (with segments) considered; each costs at least one first-segment fetch.</summary>
    public int MaxNzbFiles { get; init; } = 1_000;

    /// <summary>Distinct RAR/7z archive sets planned.</summary>
    public int MaxArchiveSets { get; init; } = 200;

    /// <summary>Planned output files reported.</summary>
    public int MaxManifestFiles { get; init; } = 5_000;

    /// <summary>
    /// Article requests (BODY, ARTICLE or yEnc header) issued to the provider client. A request
    /// is counted once, not per provider attempt; see <see cref="InspectionDiagnostics.ArticleRequests"/>.
    /// </summary>
    public int MaxArticleRequests { get; init; } = 3_000;

    /// <summary>
    /// Budget of NZB-declared encoded sizes of requested articles. A budget, not a measurement
    /// of bytes transferred.
    /// </summary>
    public long MaxDeclaredArticleBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Wall-clock budget for one inspection.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Nested stored-RAR expansion depth; deeper archives stay unexpanded and the manifest is incomplete.</summary>
    public static int MaxNestedDepth => NestedRarExpansionStep.DefaultMaxDepth;
}
