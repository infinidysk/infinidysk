using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services.StreamTrace;

namespace NzbWebDAV.Api.Controllers.SetStreamTracing;

[ApiController]
[Route("api/set-stream-tracing")]
public sealed class SetStreamTracingController(
    StreamTraceBuffer buffer,
    StreamTraceStatusBroadcaster broadcaster) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new SetStreamTracingRequest(HttpContext);
        StreamTraceStatus status;
        if (request.Enabled)
        {
            status = buffer.EnableFor(
                TimeSpan.FromMinutes(request.Minutes),
                StreamTraceBuffer.DefaultUiCapacity,
                StreamTraceBuffer.SourceUi);
        }
        else
        {
            status = buffer.Disable();
        }

        await broadcaster.BroadcastAsync(status).ConfigureAwait(false);
        return Ok(SetStreamTracingResponse.From(status));
    }
}
