using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Controllers.GetWebdavItem;
using NzbWebDAV.Models.Playback;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Playback;

[ApiController]
[Route("api/playback/native")]
[RequestSizeLimit(16 * 1024)]
public sealed class NativePlaybackController(
    NativePlaybackSessionService nativePlaybackSessionService) : PostOnlyApiController
{
    private const int MaxTitleLength = 512;
    private const int MaxMediaTypeLength = 64;

    protected override async Task<IActionResult> HandleRequest()
    {
        NativePlaybackRequest? request;
        try
        {
            request = await Request.ReadFromJsonAsync<NativePlaybackRequest>(
                    cancellationToken: HttpContext.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new BadHttpRequestException("Invalid native playback report.", exception);
        }

        if (request is null)
            throw new BadHttpRequestException("Native playback report is required.");

        var playerSession = GetWebdavItemRequest.NormalizePlayerSession(request.PlayerSession);
        if (playerSession is null)
            throw new BadHttpRequestException("A valid playerSession is required.");

        if (!Enum.IsDefined(request.Event))
            throw new BadHttpRequestException("Unsupported native playback event.");

        if (request.Event == NativePlaybackEvent.End)
        {
            nativePlaybackSessionService.End(playerSession);
            return Ok(new { status = true });
        }

        ValidateReport(request);

        if (!nativePlaybackSessionService.Report(
                playerSession,
                request.State,
                request.PositionMs,
                request.DurationMs,
                request.Title,
                request.MediaType,
                DateTimeOffset.UtcNow))
        {
            return Conflict(new
            {
                status = false,
                error = "playerSession has no exact active InfiniDysk item association.",
            });
        }

        return Ok(new { status = true });
    }

    private static void ValidateReport(NativePlaybackRequest request)
    {
        if (!Enum.IsDefined(request.State) || request.State == PlaybackState.Unknown)
            throw new BadHttpRequestException("A concrete playback state is required.");
        if (request.PositionMs is < 0)
            throw new BadHttpRequestException("positionMs cannot be negative.");
        if (request.DurationMs is < 0)
            throw new BadHttpRequestException("durationMs cannot be negative.");
        if (request.Title?.Length > MaxTitleLength)
            throw new BadHttpRequestException($"title cannot exceed {MaxTitleLength} characters.");
        if (request.MediaType?.Length > MaxMediaTypeLength)
            throw new BadHttpRequestException($"mediaType cannot exceed {MaxMediaTypeLength} characters.");
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NativePlaybackEvent
{
    Report,
    End,
}

public sealed class NativePlaybackRequest
{
    public string? PlayerSession { get; init; }
    public NativePlaybackEvent Event { get; init; } = NativePlaybackEvent.Report;
    public PlaybackState State { get; init; }
    public long? PositionMs { get; init; }
    public long? DurationMs { get; init; }
    public string? Title { get; init; }
    public string? MediaType { get; init; }
}
