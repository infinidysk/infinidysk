using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Queue.Inspection;

namespace NzbWebDAV.Api.Controllers.InspectNzb;

/// <summary>
/// Inspects an NZB with the queue import planner without importing it: reports the inner
/// file manifest, archive structure, encryption and whether InfiniDysk can serve it.
/// Fetches bounded metadata (first segments, PAR2 descriptors, archive headers) from the
/// configured providers; creates no queue, history, WebDAV, STRM or *Arr state.
/// One inspection runs at a time; a concurrent request is rejected with 429.
/// </summary>
[ApiController]
[Route("api/inspect-nzb")]
[ProducesResponseType(typeof(InspectNzbResponse), StatusCodes.Status200OK)]
public class InspectNzbController(NzbInspector inspector) : PostOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var request = await InspectNzbRequest.ReadAsync(HttpContext, inspector.Limits).ConfigureAwait(false);
        using var admission = inspector.TryBeginInspection();
        if (admission is null)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new BaseApiResponse
            {
                Status = false,
                Error = "An NZB inspection is already in progress. Inspections run one at a time; retry when it finishes.",
            });
        }

        var report = await inspector
            .InspectAsync(request.NzbBytes, request.Password, request.Name, HttpContext.RequestAborted)
            .ConfigureAwait(false);
        return Ok(new InspectNzbResponse { Inspection = report });
    }
}
