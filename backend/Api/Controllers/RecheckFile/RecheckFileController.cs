using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.RecheckFile;

[ApiController]
[Route("api/recheck-file")]
[ProducesResponseType(typeof(RecheckFileResponse), 202)]
public sealed class RecheckFileController(HealthCheckService healthCheckService, ConfigManager configManager) : PostOnlyApiController
{
    internal static async Task<Guid> ReadItemIdAsync(HttpContext context)
    {
        if (!context.Request.HasFormContentType || context.Request.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) != true)
            throw new BadHttpRequestException("Expected multipart davItemId.");
        var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
        var values = form["davItemId"];
        if (form.Count != 1 || form.Files.Count != 0 || values.Count != 1 || !Guid.TryParse(values[0], out var id) || id == Guid.Empty)
            throw new BadHttpRequestException("Expected one nonempty davItemId GUID.");
        return id;
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var id = await ReadItemIdAsync(HttpContext).ConfigureAwait(false);
        var outcome = await healthCheckService.QueueFileRecheckAsync(id, HttpContext.RequestAborted).ConfigureAwait(false);
        return outcome switch
        {
            HealthCheckService.FileRecheckOutcome.Queued => Accepted(new RecheckFileResponse { DavItemId = id, State = "queued" }),
            HealthCheckService.FileRecheckOutcome.AlreadyQueued => Accepted(new RecheckFileResponse { DavItemId = id, State = "already-queued" }),
            HealthCheckService.FileRecheckOutcome.AlreadyRunning => Accepted(new RecheckFileResponse { DavItemId = id, State = "already-running" }),
            HealthCheckService.FileRecheckOutcome.NotFound => NotFound(new BaseApiResponse { Status = false, Error = "File no longer exists." }),
            HealthCheckService.FileRecheckOutcome.Unsupported => Conflict(new BaseApiResponse { Status = false, Error = "This file type does not support a health recheck." }),
            _ => Conflict(new BaseApiResponse { Status = false, Error = configManager.GetRepairDisabledReason() ?? "Background repairs are disabled." }),
        };
    }
}