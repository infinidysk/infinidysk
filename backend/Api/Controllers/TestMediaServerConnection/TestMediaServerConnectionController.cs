using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.MediaServers;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.Controllers.TestMediaServerConnection;

[ApiController]
[Route("api/test-media-server-connection")]
public sealed class TestMediaServerConnectionController(
    ConfigManager configManager,
    IEnumerable<IMediaPlaybackSessionSource> sources) : PostOnlyApiController
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new TestMediaServerConnectionRequest(HttpContext, configManager);
        var source = sources.FirstOrDefault(candidate => candidate.ServerType == request.Type)
                     ?? throw new BadHttpRequestException("Unsupported media-server type.");

        var instance = new MediaServerInstance
        {
            Id = Guid.NewGuid(),
            Type = request.Type,
            Name = "Connection test",
            BaseUrl = request.BaseUrl,
            Token = request.Token,
            Enabled = true,
            PathMappings = [],
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        timeout.CancelAfter(TestTimeout);

        try
        {
            _ = await source.GetSessionsAsync(instance, timeout.Token).ConfigureAwait(false);
            return Ok(new TestMediaServerConnectionResponse
            {
                Status = true,
                Connected = true,
            });
        }
        catch (OperationCanceledException) when (
            !HttpContext.RequestAborted.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return Ok(Failed("Connection timed out"));
        }
        catch (HttpRequestException exception) when (
            exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return Ok(Failed("Authentication failed"));
        }
        catch (HttpRequestException exception)
        {
            return Ok(Failed(DescribeHttpError(exception.StatusCode)));
        }
        catch (Exception exception) when (
            exception is InvalidDataException or JsonException or InvalidOperationException)
        {
            return Ok(Failed("Media server returned an invalid response"));
        }
    }

    internal static string DescribeHttpError(HttpStatusCode? statusCode) =>
        statusCode is { } code ? $"HTTP {(int)code}" : "Connection failed";

    private static TestMediaServerConnectionResponse Failed(string error) => new()
    {
        Status = true,
        Connected = false,
        Error = error,
    };
}
