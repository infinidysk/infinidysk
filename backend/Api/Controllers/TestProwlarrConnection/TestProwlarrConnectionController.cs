using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Prowlarr;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.Controllers.TestProwlarrConnection;

[ApiController]
[Route("api/test-prowlarr-connection")]
public class TestProwlarrConnectionController(ConfigManager configManager) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new TestProwlarrConnectionRequest(HttpContext, configManager);
        try
        {
            var client = new ProwlarrClient(request.Url, request.ApiKey);
            var status = await client.GetStatusAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            return Ok(new TestProwlarrConnectionResponse
            {
                Status = true,
                Connected = !string.IsNullOrWhiteSpace(status.Version),
                Error = string.IsNullOrWhiteSpace(status.Version)
                    ? "Prowlarr responded without a version."
                    : null,
            });
        }
        catch (Exception e) when (e is ProwlarrClientException
                                      or HttpRequestException
                                      or InvalidDataException
                                      or ArgumentException
                                      or TimeoutException)
        {
            return Ok(new TestProwlarrConnectionResponse
            {
                Status = true,
                Connected = false,
                Error = e.Message,
            });
        }
    }
}
