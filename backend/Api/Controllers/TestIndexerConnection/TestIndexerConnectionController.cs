using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Indexers;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;

namespace NzbWebDAV.Api.Controllers.TestIndexerConnection;

[ApiController]
[Route("api/test-indexer-connection")]
public class TestIndexerConnectionController(NzbWebDAV.Config.ConfigManager configManager) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new TestIndexerConnectionRequest(HttpContext, configManager);
        try
        {
            var indexerConfig = configManager.GetIndexerConfig();
            var ua = string.IsNullOrWhiteSpace(request.UserAgent) ? configManager.GetSearchUserAgent() : request.UserAgent;
            var proxy = string.IsNullOrWhiteSpace(request.ProxyUrl)
                ? indexerConfig.ProxyUrl
                : request.ProxyUrl;
            var timeout = request.TimeoutSeconds
                          ?? (indexerConfig.TimeoutSeconds is int g && g > 0 ? g : NzbWebDAV.Config.IndexerConfig.DefaultTimeoutSeconds);
            var probe = new IndexerConfig.ConnectionDetails
            {
                Name = "test",
                Url = request.Url,
                ApiKey = request.ApiKey,
                MaxResponseBytes = request.MaxResponseBytes,
            };
            var client = new NewznabClient(
                request.Url,
                request.ApiKey,
                indexerConfig.GetEffectiveMaxResponseBytes(probe),
                ua,
                proxy,
                timeout,
                request.SkipTlsVerification);
            var ok = await client.TestAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            return Ok(new TestIndexerConnectionResponse { Status = true, Connected = ok });
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TimeoutException
                                       or InvalidOperationException or RemoteResponseException)
        {
            return Ok(new TestIndexerConnectionResponse { Status = true, Connected = false, Error = e.Message });
        }
    }
}
