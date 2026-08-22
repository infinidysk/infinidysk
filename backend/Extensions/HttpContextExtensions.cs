using Microsoft.AspNetCore.Http;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Extensions;

public static class HttpContextExtensions
{
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
