using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.ProwlarrSync;

/// <summary>
/// GET reads the last pull-sync status without network access. POST runs a sync now.
/// </summary>
[ApiController]
[Route("api/prowlarr-sync")]
public class ProwlarrSyncController(
    ProwlarrSyncService syncService,
    ConfigManager configManager) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var isSyncNow = HttpMethods.IsPost(HttpContext.Request.Method);
        var snapshot = isSyncNow
            ? await syncService.SyncNowAsync(HttpContext.RequestAborted).ConfigureAwait(false)
            : syncService.GetSnapshot();
        var response = ProwlarrSyncResponse.FromSnapshot(snapshot);

        if (isSyncNow && snapshot.LastError is null)
        {
            var masker = new ConfigSecretMasker(
                EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY"));
            var indexerJson = configManager.GetEffectiveConfigValue(ConfigKeys.IndexersInstances);
            if (indexerJson is not null)
            {
                response.IndexerConfigJson = masker.MaskForResponse(
                    ConfigKeys.IndexersInstances,
                    indexerJson);
            }
            response.ProfileConfigJson = configManager.GetEffectiveConfigValue(ConfigKeys.ProfilesInstances);
        }

        return Ok(response);
    }
}
