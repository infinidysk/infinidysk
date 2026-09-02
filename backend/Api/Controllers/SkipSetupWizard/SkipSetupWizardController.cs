using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.SkipSetupWizard;

[ApiController]
[Route("api/setup-wizard/skip")]
public sealed class SkipSetupWizardController(SetupWizardService setupWizardService)
    : PostOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        await setupWizardService
            .SkipAsync(HttpContext.RequestAborted)
            .ConfigureAwait(false);
        return Ok(new BaseApiResponse());
    }
}