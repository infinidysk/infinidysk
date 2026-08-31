namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// Immutable construction-time capacity hint for finite-range scheduling. It never
/// reserves permits or provider connections; live admission remains authoritative.
/// </summary>
internal readonly record struct StreamingCapacitySnapshot(
    bool IsPerStreamMode,
    int ConfiguredDownloadBudget,
    int ConfiguredPerStreamBudget,
    int ActiveReaderShareCount,
    int EffectivePrimaryTransferCapacity,
    int EffectiveStreamConnectionTarget,
    int ArticleBufferSize,
    long InFlightArticleBudgetBytes,
    StreamingCapacityReason Reason);

internal enum StreamingCapacityReason
{
    Ok,
    NoHealthyPrimaryCapacity,
}
