namespace NzbWebDAV.Services.StreamTrace;

/// <summary>Values of <c>status</c> on <see cref="StreamTraceKind.HedgeResolved"/> events.</summary>
public static class HedgeOutcome
{
    public const string Duplicate = "duplicate";
    public const string DuplicateAfterOriginalExhausted = "duplicate-after-original-exhausted";
    public const string Original = "original";
    public const string OriginalFailed = "original-failed";
    public const string OriginalAfterDuplicateShort = "original-after-duplicate-short";
    public const string OriginalAfterDuplicateFailed = "original-after-duplicate-failed";
    public const string Cancelled = "cancelled";
}
