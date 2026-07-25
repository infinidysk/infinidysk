using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;

namespace NzbWebDAV.Api.SabControllers.SpeedLimit;

/// <summary>
/// SAB-compatible <c>mode=speedlimit</c>. Accepted and persisted so the queue
/// JSON reflects the configured value; actual byte/s throttling is tracked
/// separately (see #375) and is not yet enforced here.
/// </summary>
public class SpeedLimitController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager
) : SabApiController.BaseController(httpContext, configManager)
{
    public async Task<SpeedLimitResponse> SetSpeedLimit(SpeedLimitRequest request, CancellationToken ct)
    {
        await ConfigPersistenceUtil.SetValueAsync(
            dbClient, configManager, ConfigKeys.QueueSpeedLimitKbps,
            request.LimitKbps.ToString(), ct).ConfigureAwait(false);
        return new SpeedLimitResponse { Status = true };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = SpeedLimitRequest.New(httpContext);
        return Ok(await SetSpeedLimit(request, httpContext.RequestAborted).ConfigureAwait(false));
    }
}
