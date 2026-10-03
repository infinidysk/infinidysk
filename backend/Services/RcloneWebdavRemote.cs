namespace NzbWebDAV.Services;

/// <summary>
/// Builds the rclone remote definition that points the built-in daemon at
/// InfiniDysk's own WebDAV server.
///
/// The password cannot be read from configuration: <c>webdav.pass</c> stores a
/// hash, never the plaintext. So the operator enters it once, it is handed to
/// rclone, and it is stored only in rclone's own config file, obscured — it is
/// never written to InfiniDysk's database.
/// </summary>
public static class RcloneWebdavRemote
{
    /// <summary>Port the backend listens on when nothing else is configured.</summary>
    internal const int DefaultBackendPort = 8080;

    /// <summary>
    /// The daemon shares the container with the backend, so it talks to the
    /// WebDAV server directly on loopback. Going through the frontend proxy would
    /// add overhead for no benefit, which is the same advice the docs give for an
    /// external rclone.
    /// </summary>
    /// <remarks>
    /// Resolved from the listener the backend was actually started with, because
    /// an installation that moves the backend off 8080 would otherwise get a
    /// remote that can never connect.
    /// </remarks>
    public static string BackendWebdavUrl =>
        Resolve(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"));

    /// <summary>
    /// Turns an ASP.NET Core listener list into the loopback URL rclone should
    /// use. Wildcard hosts become loopback, plain HTTP wins over HTTPS, and an
    /// unset or unparseable value falls back to the documented default port.
    /// </summary>
    internal static string Resolve(string? aspnetCoreUrls)
    {
        string? httpsFallback = null;

        foreach (var candidate in (aspnetCoreUrls ?? string.Empty)
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // "http://+:8080" and "http://*:8080" are valid listener syntax but not
            // valid URIs; both mean "every interface", which loopback satisfies.
            var normalized = candidate
                .Replace("//+:", "//127.0.0.1:", StringComparison.Ordinal)
                .Replace("//*:", "//127.0.0.1:", StringComparison.Ordinal);

            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)) continue;

            if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                return $"http://localhost:{uri.Port}/";

            if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                httpsFallback ??= $"https://localhost:{uri.Port}/";
        }

        return httpsFallback ?? $"http://localhost:{DefaultBackendPort}/";
    }

    public static Dictionary<string, string> BuildParameters(string? user, string? password)
    {
        if (string.IsNullOrWhiteSpace(user))
            throw new ArgumentException("A WebDAV username is required to create the rclone remote.", nameof(user));

        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("A WebDAV password is required to create the rclone remote.", nameof(password));

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["type"] = "webdav",
            ["url"] = BackendWebdavUrl,
            ["vendor"] = "other",
            ["user"] = user,
            ["pass"] = password,
        };
    }
}
