namespace NzbWebDAV.Clients.Usenet.Connections;

/// <summary>
/// Effective per-provider transfer and metadata limits for split connection scheduling.
/// A null configured transfer limit is legacy mode and therefore does not produce a budget.
/// </summary>
internal readonly record struct ProviderConnectionBudget(
    int EffectiveProviderLimit,
    int EffectiveTransferLimit,
    int BaseMetadataCapacity,
    int MetadataBurstAllowance,
    int MaxMetadataCapacity)
{
    public static ProviderConnectionBudget Calculate(
        int effectiveProviderLimit,
        int configuredTransferLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configuredTransferLimit);

        var providerLimit = Math.Max(1, effectiveProviderLimit);
        var transferLimit = Math.Min(configuredTransferLimit, providerLimit);
        var baseMetadataCapacity = providerLimit - transferLimit;

        // A one-connection provider still needs an opportunistic metadata lease for
        // STAT/HEAD/DATE and recovery probes while its transfer slot is idle.
        var calculatedMetadataMax = baseMetadataCapacity + transferLimit / 2;
        var maxMetadataCapacity = Math.Min(providerLimit, Math.Max(1, calculatedMetadataMax));
        var metadataBurstAllowance = maxMetadataCapacity - baseMetadataCapacity;

        return new ProviderConnectionBudget(
            providerLimit,
            transferLimit,
            baseMetadataCapacity,
            metadataBurstAllowance,
            maxMetadataCapacity);
    }
}
