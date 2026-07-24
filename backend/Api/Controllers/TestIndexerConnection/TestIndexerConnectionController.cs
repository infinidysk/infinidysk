using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Indexers;

namespace NzbWebDAV.Api.Controllers.TestIndexerConnection;

[ApiController]
[Route("api/test-indexer-connection")]
public class TestIndexerConnectionController(NzbWebDAV.Config.ConfigManager configManager) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new TestIndexerConnectionRequest(HttpContext);
        try
        {
            var indexerConfig = configManager.GetIndexerConfig();
            var ua = string.IsNullOrWhiteSpace(request.UserAgent) ? configManager.GetSearchUserAgent() : request.UserAgent;
            var proxy = string.IsNullOrWhiteSpace(request.ProxyUrl)
                ? indexerConfig.ProxyUrl
                : request.ProxyUrl;
            var timeout = request.TimeoutSeconds
                          ?? (indexerConfig.TimeoutSeconds is int g && g > 0 ? g : NzbWebDAV.Config.IndexerConfig.DefaultTimeoutSeconds);
            var client = new NewznabClient(
                request.Url,
                request.ApiKey,
                ua,
                proxy,
                timeout,
                request.SkipTlsVerification,
                request.UseHealthProxy);
            var ok = await client.TestAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            return Ok(new TestIndexerConnectionResponse { Status = true, Connected = ok });
        }
        catch (Exception e)
        {
            return Ok(new TestIndexerConnectionResponse { Status = true, Connected = false, Error = e.Message });
        }
    }
}
