namespace NzbWebDAV.Api.Controllers.GetSetupWizardState;

public sealed class GetSetupWizardStateResponse : BaseApiResponse
{
    public required int CurrentVersion { get; init; }
    public int? RecordedVersion { get; init; }
    public string? Disposition { get; init; }
    public required bool SetupRequired { get; init; }
    public required string[] IngestionMethods { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}