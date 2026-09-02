namespace NzbWebDAV.Api.Controllers.CompleteSetupWizard;

public sealed class CompleteSetupWizardResponse : BaseApiResponse
{
    public required string[] ChangedConfigKeys { get; init; }
    public required bool RestartRequired { get; init; }
}