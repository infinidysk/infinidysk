using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.ResetHealthCheckQueue;

[ApiController]
[Route("api/reset-health-check-queue")]
public class ResetHealthCheckQueueController(DavDatabaseClient dbClient) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        if (!HttpMethods.IsPost(HttpContext.Request.Method))
        {
            return StatusCode(
                StatusCodes.Status405MethodNotAllowed,
                new BaseApiResponse { Status = false, Error = "POST required" });
        }

        var resetCount = await dbClient.Ctx.Items
            .Where(x => x.Type == DavItem.ItemType.UsenetFile)
            .Where(x => x.NextHealthCheck != null && x.NextHealthCheck != DateTimeOffset.UnixEpoch)
            .ExecuteUpdateAsync(
                x => x.SetProperty(item => item.NextHealthCheck, (DateTimeOffset?)null),
                HttpContext.RequestAborted)
            .ConfigureAwait(false);

        return Ok(new ResetHealthCheckQueueResponse
        {
            Status = true,
            ResetCount = resetCount,
        });
    }
}
