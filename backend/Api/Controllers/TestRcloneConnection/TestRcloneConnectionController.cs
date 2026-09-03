using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.Controllers.TestRcloneConnection;

[ApiController]
[Route("api/test-rclone-connection")]
public class TestRcloneConnectionController(
    ConfigManager configManager,
    IRcloneClient rcloneClient) : BaseApiController
{
    private async Task<TestRcloneConnectionResponse> TestRcloneConnection(TestRcloneConnectionRequest request)
    {
        try
        {
            var result = await RcloneClient
                .TestConnection(request.Host, request.User, request.Pass, HttpContext.RequestAborted)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                return new TestRcloneConnectionResponse
                {
                    Status = true,
                    Connected = false,
                    Error = DescribeConnectionError(request.Host, result.Error),
                };
            }

            var vfsStats = await RcloneClient
                .GetVfsStats(request.Host, request.User, request.Pass, HttpContext.RequestAborted)
                .ConfigureAwait(false);

            return new TestRcloneConnectionResponse
            {
                Status = true,
                Connected = true,
                ReadAheadBytes = vfsStats.Success ? vfsStats.Options?.ReadAhead : null,
                CacheMode = vfsStats.Success ? vfsStats.Options?.CacheMode : null,
                VfsInspectionError = vfsStats.Success ? null : vfsStats.Error,
                LastInvalidationError = rcloneClient.LastForgetError?.Message,
                LastInvalidationErrorAt = rcloneClient.LastForgetError?.At
            };
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TimeoutException or InvalidOperationException)
        {
            return new TestRcloneConnectionResponse
            {
                Status = true,
                Connected = false,
                Error = e.Message
            };
        }
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new TestRcloneConnectionRequest(HttpContext, configManager);
        var response = await TestRcloneConnection(request).ConfigureAwait(false);
        return Ok(response);
    }

    internal static string DescribeConnectionError(string host, string? error)
    {
        var reason = string.IsNullOrWhiteSpace(error) ? "Connection test failed" : error.TrimEnd('.');
        return Uri.TryCreate(host, UriKind.Absolute, out var uri) && uri.IsLoopback
            ? $"{reason}. Loopback addresses refer to the InfiniDysk container; use the rclone service name unless both processes share a network namespace."
            : reason;
    }
}
