using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services.SupportPack;

namespace NzbWebDAV.Api.Controllers.DownloadSupportPack;

[ApiController]
[Route("api/download-support-pack")]
public sealed class DownloadSupportPackController(SupportPackService supportPack) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        Response.ContentType = "application/zip";
        Response.Headers.ContentDisposition = $"attachment; filename=\"infinidysk-support-{timestamp}.zip\"";
        Response.Headers.CacheControl = "no-store";

        // Quality warnings must precede the streaming body so the Support UI can show
        // them after download. They are cheap point-in-time reads, not pack content.
        var packQuality = supportPack.GetPackQualityWarnings();
        if (packQuality.Count > 0)
            Response.Headers["X-Support-Pack-Quality"] = JsonSerializer.Serialize(packQuality);

        // ZipArchive performs synchronous finalization writes. BodyWriter's stream
        // supports those writes whereas Kestrel's Response.Body does not.
        await supportPack.WriteAsync(Response.BodyWriter.AsStream(), HttpContext.RequestAborted)
            .ConfigureAwait(false);
        return new EmptyResult();
    }
}
