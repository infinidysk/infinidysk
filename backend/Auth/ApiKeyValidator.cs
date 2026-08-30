using Microsoft.AspNetCore.Http;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Auth;

/// <summary>
/// The single api-key check shared by both API surfaces — the custom <c>/api/*</c>
/// controllers and the SABnzbd-compatible <c>/api?mode=</c> dispatcher. Previously the two
/// used different rules (one accepted only the env key, the other accepted either the env key
/// or the configured key), which was an inconsistency on a security boundary. Both now accept
/// the same set: the user-configured api key and the frontend/backend env key.
/// </summary>
public static class ApiKeyValidator
{
    public static void Validate(HttpContext httpContext, ConfigManager configManager)
        => ValidateKey(httpContext.GetRequestApiKey(), configManager);

    public static void ValidateWithoutFormParse(HttpContext httpContext, ConfigManager configManager)
        => ValidateKey(httpContext.GetRequestApiKeyWithoutFormParse(), configManager);

    private static void ValidateKey(string? apiKey, ConfigManager configManager)
    {
        if (apiKey == null)
            throw new UnauthorizedAccessException("API Key Required");

        var isValid = apiKey.IsAny(
            configManager.GetApiKey(),
            EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY"));
        if (!isValid)
            throw new UnauthorizedAccessException("API Key Incorrect");
    }
}
