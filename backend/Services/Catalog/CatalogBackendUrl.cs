using System.Globalization;

namespace NzbWebDAV.Services.Catalog;

/// <summary>
/// Builds the loopback URL the transcode path uses to reach the backend directly.
/// <c>BACKEND_URL</c> normally points at the bundled frontend proxy (or a public host
/// name), but FFprobe/FFmpeg run inside the container and must not depend on public
/// DNS, TLS, or the proxy hop, so only the scheme and port are kept.
/// </summary>
internal static class CatalogBackendUrl
{
    internal const string DefaultBaseUrl = "http://127.0.0.1:8080";
    internal const string LoopbackHost = "127.0.0.1";

    internal static string GetLoopbackBase(string? backendUrl)
    {
        if (string.IsNullOrWhiteSpace(backendUrl)) return DefaultBaseUrl;
        if (!Uri.TryCreate(backendUrl.Trim(), UriKind.Absolute, out var uri)) return DefaultBaseUrl;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return DefaultBaseUrl;

        // UriBuilder drops explicit credentials and any path, leaving scheme://127.0.0.1:port.
        var builder = new UriBuilder(uri)
        {
            Host = LoopbackHost,
            UserName = "",
            Password = "",
        };
        return builder.Uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>Appends an unprefixed backend route to the loopback base.</summary>
    internal static string Combine(string loopbackBase, string routePath)
    {
        var trimmedBase = loopbackBase.TrimEnd('/');
        var path = routePath.StartsWith('/')
            ? routePath
            : string.Create(CultureInfo.InvariantCulture, $"/{routePath}");
        return trimmedBase + path;
    }
}
