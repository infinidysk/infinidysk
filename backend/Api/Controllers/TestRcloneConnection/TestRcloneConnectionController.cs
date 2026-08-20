using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.Controllers.TestRcloneConnection;

[ApiController]
[Route("api/test-rclone-connection")]
public class TestRcloneConnectionController(ConfigManager configManager) : BaseApiController
{
    private async Task<TestRcloneConnectionResponse> TestRcloneConnection(TestRcloneConnectionRequest request)
    {
        try
        {
            var result = await RcloneClient
                .TestConnection(request.Host, request.User, request.Pass, HttpContext.RequestAborted)
                .ConfigureAwait(false);

            return new TestRcloneConnectionResponse
            {
                Status = true,
                Connected = result.Success,
                Error = result.Error,
                LastInvalidationError = RcloneClient.LastForgetError?.Message,
                LastInvalidationErrorAt = RcloneClient.LastForgetError?.At
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
}
