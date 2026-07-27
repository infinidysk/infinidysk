using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services.StreamTrace;
using Serilog;

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
            // Recorded so a support pack shows when tracing started and for how
            // long — the env-var path logs an equivalent line at startup.
            Log.Information(
                "Stream tracing enabled from the UI for {Minutes} minutes with a capacity of {Capacity} events",
                request.Minutes,
                status.Capacity);
        }
        else
        {
            var before = buffer.GetStatus();
            status = buffer.Disable();
            Log.Information(
                "Stream tracing disabled from the UI; released {Events:n0} buffered events",
                before.EventCount);
        }

        await broadcaster.BroadcastAsync(status).ConfigureAwait(false);
        return Ok(SetStreamTracingResponse.From(status));
    }
}
