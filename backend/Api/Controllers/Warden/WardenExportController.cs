using System.IO.Compression;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Warden;

[ApiController]
[Route("api/warden-export")]
public class WardenExportController(WardenStore warden) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var ct = HttpContext.RequestAborted;
        var scope = HttpContext.Request.Query["scope"].ToString();
        var dedup = HttpContext.Request.Query["dedup"].ToString() != "0";

        List<string> sourceIds;
        if (scope == "merged")
        {
            var requested = HttpContext.Request.Query["sources"].ToString();
            sourceIds = string.IsNullOrWhiteSpace(requested)
                ? warden.GetSources().Select(s => s.Id).ToList()
                : requested.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
        else
        {
            sourceIds = new List<string> { WardenStore.LocalSourceId };
        }

        Response.ContentType = "application/gzip";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"warden.ndjson.gz\"";
        await using var gz = new GZipStream(Response.Body, CompressionLevel.Optimal, leaveOpen: true);
        await warden.ExportToAsync(gz, sourceIds, dedup, ct).ConfigureAwait(false);
        return new EmptyResult();
    }
}
