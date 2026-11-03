using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using NzbWebDAV.Services.Catalog;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Extensions;

public static class HttpContextExtensions
{
    /// <summary>Header the frontend proxy uses to scope watch state to the picked profile.</summary>
    public const string ViewerProfileHeaderName = "x-viewer-profile-id";

    /// <summary>
    /// The viewer profile this request acts as. An absent or malformed header means the
    /// default profile, which is what admin sessions, WebDAV, and the SAB API use.
    /// </summary>
    public static int GetViewerProfileId(this HttpContext httpContext)
    {
        var raw = httpContext.Request.Headers[ViewerProfileHeaderName].ToString();
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : ViewerProfileService.DefaultProfileId;
    }

    public static string? GetRequestParam(this HttpContext httpContext, string key)
    {
        return httpContext.GetQueryParam(key)
            ?? httpContext.GetFormParam(key);
    }

    public static string? GetQueryParam(this HttpContext httpContext, string name)
    {
        return StringUtil.EmptyToNull(httpContext.Request.Query[name].FirstOrDefault());
    }

    public static string? GetFormParam(this HttpContext httpContext, string name)
    {
        return httpContext.Request.HasFormContentType
            ? StringUtil.EmptyToNull(httpContext.Request.Form[name].FirstOrDefault())
            : null;
    }

    public static IEnumerable<string> GetQueryParamValues(this HttpContext httpContext, string name)
    {
        return httpContext.Request.Query[name]
            .Where(x => x is not null)
            .Select(x => x!);
    }

    public static string? GetRequestApiKey(this HttpContext httpContext)
    {
        return httpContext.Request.Headers["x-api-key"].FirstOrDefault()
            ?? httpContext.GetRequestParam("apikey");
    }

    // Header, query, and already-buffered form only. Does not touch Request.Form,
    // which parses lazily and can throw InvalidDataException. Warden source import
    // uses this so unauthenticated malformed multipart stays 401.
    public static string? GetRequestApiKeyWithoutFormParse(this HttpContext httpContext)
    {
        return httpContext.Request.Headers["x-api-key"].FirstOrDefault()
            ?? httpContext.GetQueryParam("apikey")
            ?? GetBufferedFormParam(httpContext, "apikey");
    }

    private static string? GetBufferedFormParam(HttpContext httpContext, string name)
    {
        var form = httpContext.Features.Get<IFormFeature>()?.Form;
        if (form is null)
            return null;
        return StringUtil.EmptyToNull(form[name].FirstOrDefault());
    }

    public static string GetPublicBaseUrl(this HttpContext httpContext, string configuredBaseUrl)
    {
        var trimmed = configuredBaseUrl.TrimEnd('/');
        var baseUrl = !string.IsNullOrWhiteSpace(trimmed) && trimmed != "http://localhost:3000"
            ? trimmed
            : $"{httpContext.Request.Scheme}://{httpContext.Request.Host.Value}";

        // The frontend proxy supplies its validated URL_BASE as this prefix, so URLs copied
        // from profile adapters remain routable when the application is mounted at a sub-path.
        var prefix = NormalizePathPrefix(httpContext.Request.Headers["X-Forwarded-Prefix"].FirstOrDefault());
        if (prefix is null || baseUrl.EndsWith(prefix, StringComparison.Ordinal))
            return baseUrl;
        return baseUrl + prefix;
    }

    public static string GetPublicPathPrefix(this HttpContext httpContext, string configuredBaseUrl)
    {
        var configuredPrefix = Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out var configuredUri)
            ? NormalizePathPrefix(configuredUri.AbsolutePath)
            : null;
        var forwardedPrefix = NormalizePathPrefix(
            httpContext.Request.Headers["X-Forwarded-Prefix"].FirstOrDefault());

        if (forwardedPrefix is null || configuredPrefix?.EndsWith(forwardedPrefix, StringComparison.Ordinal) == true)
            return configuredPrefix ?? string.Empty;
        return (configuredPrefix ?? string.Empty) + forwardedPrefix;
    }

    private static string? NormalizePathPrefix(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim().TrimEnd('/');
        if (trimmed.Length == 0 || trimmed == "/") return null;
        if (!trimmed.StartsWith('/')
            || trimmed.Contains("//", StringComparison.Ordinal)
            || trimmed.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '~' or '-' or '/')))
        {
            return null;
        }

        return trimmed;
    }
}
