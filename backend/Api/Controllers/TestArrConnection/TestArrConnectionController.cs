using System.Net;
using System.Net.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.Controllers.TestArrConnection;

[ApiController]
[Route("api/test-arr-connection")]
public class TestArrConnectionController(ConfigManager configManager) : BaseApiController
{
    private static async Task<TestArrConnectionResponse> TestArrConnection(TestArrConnectionRequest request)
    {
        try
        {
            var client = new ArrClient(request.Host, request.ApiKey);
            var apiInfo = await client.GetApiInfo().ConfigureAwait(false);
            if (apiInfo.Current?.Length > 0)
            {
                return new TestArrConnectionResponse
                {
                    Status = true,
                    Connected = true
                };
            }

            return new TestArrConnectionResponse
            {
                Status = true,
                Connected = false,
                Error = "Connected but received empty API info"
            };
        }
        catch (HttpRequestException e) when (
            e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new TestArrConnectionResponse
            {
                Status = true,
                Connected = false,
                Error = "Authentication failed"
            };
        }
        catch (HttpRequestException e)
        {
            var error = e.StatusCode is { } code
                ? $"HTTP {(int)code}"
                : e.Message;
            return new TestArrConnectionResponse
            {
                Status = true,
                Connected = false,
                Error = error
            };
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TimeoutException or InvalidOperationException)
        {
            return new TestArrConnectionResponse
            {
                Status = true,
                Connected = false,
                Error = e.Message
            };
        }
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new TestArrConnectionRequest(HttpContext, configManager);
        var response = await TestArrConnection(request).ConfigureAwait(false);
        return Ok(response);
    }
}
