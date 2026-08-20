using Microsoft.AspNetCore.Http;

namespace NzbWebDAV.Api.Errors;

public static class ApiRequestClassifier
{
    public static bool IsSabApi(HttpContext context)
    {
        var path = context.Request.Path;
        return path.Equals("/api", StringComparison.OrdinalIgnoreCase)
               || path.Equals("/api/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAdminApi(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase, out var rest))
            return false;
        var remainder = rest.Value;
        if (string.IsNullOrEmpty(remainder) || remainder == "/")
            return false;
        // Stremio/Newznab adapter streams under /api/search/{token}/… keep their own bodies.
        return !remainder.StartsWith("/search/", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(remainder, "/search", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsProblemDetailsApi(HttpContext context) =>
        IsSabApi(context) || IsAdminApi(context);
}
