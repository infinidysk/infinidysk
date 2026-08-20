using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.SabControllers.SpeedLimit;

public class SpeedLimitRequest
{
    /// <summary>Requested speed limit in KB/s. 0 (or absent) means unlimited.</summary>
    public int LimitKbps { get; init; }

    public static SpeedLimitRequest New(HttpContext httpContext)
    {
        // SABnzbd accepts the value under either "value" (percentage of max
        // line speed) or "value2"/"limit" depending on client. NzbDAV has no
        // "max line speed" setting to compute a percentage against, so the
        // raw number is stored directly as a KB/s override.
        var raw = httpContext.GetRequestParam("value")
            ?? httpContext.GetRequestParam("limit");

        if (string.IsNullOrWhiteSpace(raw))
            return new SpeedLimitRequest { LimitKbps = 0 };

        var errors = new ValidationErrors();
        if (!errors.TryParseInt("value", raw, "Invalid speed limit value.", out var limitKbps) || limitKbps < 0)
        {
            if (limitKbps < 0)
                errors.Add("value", "Invalid speed limit value.");
            errors.ThrowIfAny();
        }

        return new SpeedLimitRequest { LimitKbps = limitKbps };
    }
}
