using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;
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
        var errors = new ValidationErrors();
        Url = StringUtil.EmptyToNull(context.GetRequestParam("url"))
              ?? configManager.GetProwlarrUrl()
              ?? "";
        if (string.IsNullOrEmpty(Url))
            errors.Add("url", "Prowlarr URL is required.");

        var submittedApiKey = StringUtil.EmptyToNull(context.GetRequestParam("apiKey"));
        string? apiKey = null;
        if (submittedApiKey is null)
        {
            apiKey = configManager.GetProwlarrApiKey();
            if (apiKey is null)
                errors.Add("apiKey", "Prowlarr API key is required.");
        }
        else if (!ConfigSecretMasker.IsMaskToken(submittedApiKey))
        {
            apiKey = submittedApiKey;
        }
        else
        {
            var masker = new ConfigSecretMasker(
                EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY"));
            apiKey = masker.ResolveForUpdate(
                ConfigKeys.ProwlarrApiKey,
                submittedApiKey,
                configManager.GetEffectiveConfigValue(ConfigKeys.ProwlarrApiKey));
        }

        errors.ThrowIfAny();
        ApiKey = apiKey!;
    }
}
