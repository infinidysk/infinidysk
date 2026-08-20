using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.Controllers.TestIndexerConnection;

public class TestIndexerConnectionRequest
{
    public string Url { get; init; }
    public string ApiKey { get; init; }
    public string? UserAgent { get; init; }
    public string? ProxyUrl { get; init; }
    public int? TimeoutSeconds { get; init; }
    public bool SkipTlsVerification { get; init; }

    public TestIndexerConnectionRequest(HttpContext context, ConfigManager configManager)
    {
        var errors = new ValidationErrors();
        Url = context.Request.Form["url"].FirstOrDefault() ?? "";
        var submittedApiKey = context.Request.Form["apiKey"].FirstOrDefault();
        if (string.IsNullOrEmpty(Url))
            errors.Add("url", "Indexer url is required");
        if (submittedApiKey is null)
            errors.Add("apiKey", "Indexer apiKey is required");
        errors.ThrowIfAny();
        ApiKey = IndexerApiKeyResolver.Resolve(submittedApiKey!, configManager);

        UserAgent = context.Request.Form["userAgent"].FirstOrDefault();
        ProxyUrl = context.Request.Form["proxyUrl"].FirstOrDefault();
        var rawTimeout = context.Request.Form["timeoutSeconds"].FirstOrDefault();
        TimeoutSeconds = int.TryParse(rawTimeout, out var t) && t > 0 ? t : null;
        SkipTlsVerification = bool.TryParse(
            context.Request.Form["skipTlsVerification"].FirstOrDefault(),
            out var skipTlsVerification)
            && skipTlsVerification;
    }
}
