using Microsoft.AspNetCore.Http;

namespace NzbWebDAV.Api.Controllers.TestIndexerConnection;

public class TestIndexerConnectionRequest
{
    public string Url { get; init; }
    public string ApiKey { get; init; }
    public string? UserAgent { get; init; }
    public string? ProxyUrl { get; init; }
    public int? TimeoutSeconds { get; init; }
    public bool SkipTlsVerification { get; init; }
    public bool UseHealthProxy { get; init; }

    public TestIndexerConnectionRequest(HttpContext context)
    {
        Url = context.Request.Form["url"].FirstOrDefault()
              ?? throw new BadHttpRequestException("Indexer url is required");

        ApiKey = context.Request.Form["apiKey"].FirstOrDefault()
                 ?? throw new BadHttpRequestException("Indexer apiKey is required");

        UserAgent = context.Request.Form["userAgent"].FirstOrDefault();
        ProxyUrl = context.Request.Form["proxyUrl"].FirstOrDefault();
        var rawTimeout = context.Request.Form["timeoutSeconds"].FirstOrDefault();
        TimeoutSeconds = int.TryParse(rawTimeout, out var t) && t > 0 ? t : null;
        SkipTlsVerification = bool.TryParse(
            context.Request.Form["skipTlsVerification"].FirstOrDefault(),
            out var skipTlsVerification)
            && skipTlsVerification;

        UseHealthProxy = string.Equals(
            context.Request.Form["useHealthProxy"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);
    }
}
