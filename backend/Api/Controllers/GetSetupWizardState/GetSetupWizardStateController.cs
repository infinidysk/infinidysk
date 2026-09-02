using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Database;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.GetSetupWizardState;

[ApiController]
[Route("api/setup-wizard-state")]
[ProducesResponseType(typeof(GetSetupWizardStateResponse), StatusCodes.Status200OK)]
public sealed class GetSetupWizardStateController(SetupWizardService setupWizardService)
    : GetOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var state = await setupWizardService
            .GetStateAsync(HttpContext.RequestAborted)
            .ConfigureAwait(false);

        return Ok(new GetSetupWizardStateResponse
        {
            CurrentVersion = state.CurrentVersion,
            RecordedVersion = state.RecordedVersion,
            Disposition = state.Disposition?.ToString().ToLowerInvariant(),
            SetupRequired = state.SetupRequired,
            IngestionMethods = state.IngestionMethods,
            UpdatedAt = state.UpdatedAt,
            MainDatabaseProvider = DatabaseProviderConfig.IsPostgres ? "postgres" : "sqlite",
            MainDatabaseBackupSupported = !DatabaseProviderConfig.IsPostgres,
        });
    }
}
