using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services.Catalog;

namespace NzbWebDAV.Api.Controllers.Catalog;

/// <summary>
/// Serves the VOD manifest for one playback session. Authenticated like every other admin
/// route: the session id alone never grants media access.
/// </summary>
[ApiController]
[Route("api/catalog/playback/{sessionId:guid}/playlist.m3u8")]
public sealed class CatalogPlaybackManifestController(CatalogTranscodeService transcodeService) : GetOnlyApiController
{
    internal const string ManifestContentType = "application/vnd.apple.mpegurl";

    protected override async Task<IActionResult> HandleRequest()
    {
        var sessionId = ReadSessionId();
        var playlist = await transcodeService
            .GetPlaylistAsync(sessionId, HttpContext.RequestAborted)
            .ConfigureAwait(false);

        Response.Headers.CacheControl = "no-store";
        return Content(playlist, ManifestContentType);
    }

    private Guid ReadSessionId()
        => HttpContext.Request.RouteValues["sessionId"] is string raw && Guid.TryParse(raw, out var sessionId)
            ? sessionId
            : throw CatalogException.Validation("A valid playback session is required.");
}

/// <summary>
/// Produces and streams one MPEG-TS segment. Segments are generated on demand, so a seek
/// only pays for the six seconds it needs.
/// </summary>
[ApiController]
[Route("api/catalog/playback/{sessionId:guid}/segments/{index:int}.ts")]
public sealed class CatalogPlaybackSegmentController(CatalogTranscodeService transcodeService) : GetOnlyApiController
{
    internal const string SegmentContentType = "video/mp2t";

    protected override async Task<IActionResult> HandleRequest()
    {
        var sessionId = ReadSessionId();
        var index = ReadIndex();

        using var lease = await transcodeService
            .GetSegmentAsync(sessionId, index, HttpContext.RequestAborted)
            .ConfigureAwait(false);

        var file = new FileInfo(lease.FilePath);
        if (!file.Exists)
            throw CatalogException.NotFound(CatalogTranscodeService.SegmentNotFoundMessage);

        Response.ContentType = SegmentContentType;
        Response.Headers.CacheControl = "no-store";
        Response.ContentLength = file.Length;

        await using var stream = lease.OpenRead();
        await stream.CopyToAsync(Response.Body, HttpContext.RequestAborted).ConfigureAwait(false);
        return new EmptyResult();
    }

    private Guid ReadSessionId()
        => HttpContext.Request.RouteValues["sessionId"] is string raw && Guid.TryParse(raw, out var sessionId)
            ? sessionId
            : throw CatalogException.Validation("A valid playback session is required.");

    private int ReadIndex()
    {
        if (HttpContext.Request.RouteValues["index"] is not string raw
            || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            throw CatalogException.Validation("Segment index must be a whole number.");
        }

        if (index < 0)
            throw CatalogException.Validation("Segment index must be zero or greater.");
        return index;
    }
}
