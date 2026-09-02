using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.UpdateConfig;

[ApiController]
[Route("api/update-config")]
public class UpdateConfigController(
    ConfigUpdateService configUpdateService,
    IndexerConfigWriteLock indexerConfigWriteLock) : BaseApiController
{
    private async Task<UpdateConfigResponse> UpdateConfig(UpdateConfigRequest request)
    {
        await configUpdateService
            .ApplyAsync(request.ConfigItems, HttpContext.RequestAborted)
            .ConfigureAwait(false);

        return new UpdateConfigResponse { Status = true };
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new UpdateConfigRequest(HttpContext);
        var touchesIndexerConfig = request.ConfigItems.Any(x =>
            x.ConfigName is ConfigKeys.IndexersInstances or ConfigKeys.ProfilesInstances);
        var response = touchesIndexerConfig
            ? await indexerConfigWriteLock.RunAsync(
                () => UpdateConfig(request),
                HttpContext.RequestAborted).ConfigureAwait(false)
            : await UpdateConfig(request).ConfigureAwait(false);
        return Ok(response);
    }
}
