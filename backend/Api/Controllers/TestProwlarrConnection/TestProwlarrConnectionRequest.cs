using Microsoft.AspNetCore.Http;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.TestProwlarrConnection;

public sealed class TestProwlarrConnectionRequest
{
    public string Url { get; }
    public string ApiKey { get; }

    public TestProwlarrConnectionRequest(HttpContext context, ConfigManager configManager)
    {
        Url = StringUtil.EmptyToNull(context.GetRequestParam("url"))
              ?? configManager.GetProwlarrUrl()
              ?? throw new BadHttpRequestException("Prowlarr URL is required.");

        var submittedApiKey = StringUtil.EmptyToNull(context.GetRequestParam("apiKey"));
        if (submittedApiKey is null)
        {
            ApiKey = configManager.GetProwlarrApiKey()
                     ?? throw new BadHttpRequestException("Prowlarr API key is required.");
            return;
        }

        if (!ConfigSecretMasker.IsMaskToken(submittedApiKey))
        {
            ApiKey = submittedApiKey;
            return;
        }

        var masker = new ConfigSecretMasker(
            EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY"));
        ApiKey = masker.ResolveForUpdate(
            ConfigKeys.ProwlarrApiKey,
            submittedApiKey,
            configManager.GetEffectiveConfigValue(ConfigKeys.ProwlarrApiKey));
    }
}
