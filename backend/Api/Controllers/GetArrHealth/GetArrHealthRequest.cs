using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.Controllers.GetArrHealth;

public class GetArrHealthRequest
{
    public ArrHealthWindow Window { get; init; } = ArrHealthWindow.Last24Hours;
    public CancellationToken CancellationToken { get; init; }

    public GetArrHealthRequest(HttpContext context)
    {
        CancellationToken = context.RequestAborted;
        var w = context.GetQueryParam("window");
        if (w is not null)
            Window = ParseWindow(w);
    }

    internal static ArrHealthWindow ParseWindow(string window)
    {
        return window.ToLowerInvariant() switch
        {
            "1h" => ArrHealthWindow.Last1Hour,
            "24h" => ArrHealthWindow.Last24Hours,
            "7d" => ArrHealthWindow.Last7Days,
            "30d" => ArrHealthWindow.Last30Days,
            "all" => ArrHealthWindow.AllTime,
            _ => ThrowInvalidWindow(),
        };

        static ArrHealthWindow ThrowInvalidWindow()
        {
            var errors = new ValidationErrors();
            errors.Add("window", "Invalid window parameter (use 1h, 24h, 7d, 30d, or all)");
            errors.ThrowIfAny();
            return default;
        }
    }

    public enum ArrHealthWindow
    {
        Last1Hour,
        Last24Hours,
        Last7Days,
        Last30Days,
        AllTime,
    }
}
