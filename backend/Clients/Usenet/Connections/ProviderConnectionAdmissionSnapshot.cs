namespace NzbWebDAV.Clients.Usenet.Connections;

/// <summary>
/// Point-in-time operation admission state for a provider using split connection
/// budgeting. Legacy shared-pool providers do not produce this snapshot.
/// </summary>
public sealed record ProviderConnectionAdmissionSnapshot(
    int ConfiguredTransferLimit,
    int EffectiveTransferLimit,
    int BaseMetadataCapacity,
    int MetadataBurstAllowance,
    int MaxMetadataCapacity,
    int ActiveTransferOperations,
    int ActiveMetadataOperations,
    int WaitingTransferOperations,
    int WaitingMetadataOperations);
