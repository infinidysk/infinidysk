using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.CompleteSetupWizard;

[ApiController]
[Route("api/setup-wizard/complete")]
public sealed class CompleteSetupWizardController(SetupWizardService setupWizardService)
    : PostOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new CompleteSetupWizardRequest(HttpContext);
        var result = await setupWizardService.CompleteAsync(
            new CompleteSetupWizardCommand
            {
                Strategy = request.Strategy,
                IngestionMethods = request.IngestionMethods,
                ConfigItems = request.ConfigItems,
            },
            HttpContext.RequestAborted).ConfigureAwait(false);

        return Ok(new CompleteSetupWizardResponse
        {
            ChangedConfigKeys = result.ChangedConfigKeys,
            RestartRequired = result.RestartRequired,
        });
    }
}
