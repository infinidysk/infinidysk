using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace NzbWebDAV.Api.Errors;

public static partial class RequestCorrelation
{
    public const string HeaderName = "X-Correlation-ID";
    public const string ItemKey = "NzbWebDAV.TraceId";
    public const string LogPropertyName = "TraceId";

    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex IncomingIdPattern();

    public static string Resolve(HttpContext context)
    {
        if (context.Items.TryGetValue(ItemKey, out var existing) && existing is string cached && cached.Length > 0)
            return cached;

        var traceId = ResolveFresh(context);
        context.Items[ItemKey] = traceId;
        return traceId;
    }

    public static void ApplyResponseHeader(HttpContext context)
    {
        if (context.Response.HasStarted) return;
        context.Response.Headers[HeaderName] = Resolve(context);
    }

    public static bool TrySanitizeIncoming(string? value, out string sanitized)
    {
        sanitized = "";
        if (string.IsNullOrWhiteSpace(value)) return false;
        var candidate = value.Trim();
        if (!IncomingIdPattern().IsMatch(candidate)) return false;
        sanitized = candidate;
        return true;
    }

    private static string ResolveFresh(HttpContext context)
    {
        var activityTrace = Activity.Current?.TraceId;
        if (context.Request.Headers.TryGetValue(HeaderName, out var incoming)
            && TrySanitizeIncoming(incoming.ToString(), out var sanitized))
        {
            return sanitized;
        }

        if (activityTrace is { } trace && trace != default)
            return trace.ToString();

        return context.TraceIdentifier;
    }
}
