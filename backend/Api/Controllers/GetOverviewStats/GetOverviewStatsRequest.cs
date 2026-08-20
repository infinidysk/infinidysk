using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.Controllers.GetOverviewStats;

public class GetOverviewStatsRequest
{
    public OverviewWindow Window { get; init; } = OverviewWindow.Last24Hours;
    public OverviewSections Sections { get; init; } = OverviewSections.All;
    public CancellationToken CancellationToken { get; init; }

    public GetOverviewStatsRequest(HttpContext context)
    {
        CancellationToken = context.RequestAborted;
        var errors = new ValidationErrors();
        var w = context.GetQueryParam("window");
        if (w is not null)
        {
            Window = w.ToLowerInvariant() switch
            {
                "1h" => OverviewWindow.Last1Hour,
                "24h" => OverviewWindow.Last24Hours,
                "7d" => OverviewWindow.Last7Days,
                "30d" => OverviewWindow.Last30Days,
                "all" => OverviewWindow.AllTime,
                _ => OverviewWindow.Last24Hours,
            };
            if (w.ToLowerInvariant() is not ("1h" or "24h" or "7d" or "30d" or "all"))
                errors.Add("window", "Invalid window parameter (use 1h, 24h, 7d, 30d, or all)");
        }

        try
        {
            Sections = ParseSections(context.GetQueryParam("sections"));
        }
        catch (BadHttpRequestException ex)
        {
            errors.Add("sections", ex.Message);
        }

        errors.ThrowIfAny();
    }

    private static OverviewSections ParseSections(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) ||
            raw.Equals("all", StringComparison.OrdinalIgnoreCase))
            return OverviewSections.All;

        var result = OverviewSections.None;
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            result |= part.ToLowerInvariant() switch
            {
                "window" => OverviewSections.Window,
                "detail" => OverviewSections.Detail,
                "static" => OverviewSections.Static,
                "all" => OverviewSections.All,
                _ => throw new BadHttpRequestException(
                    "Invalid sections parameter (use window, detail, static, or all)")
            };
        }

        return result == OverviewSections.None ? OverviewSections.All : result;
    }

    public enum OverviewWindow
    {
        Last1Hour,
        Last24Hours,
        Last7Days,
        Last30Days,
        AllTime,
    }

    [Flags]
    public enum OverviewSections
    {
        None = 0,
        Window = 1,
        Detail = 2,
        Static = 4,
        All = Window | Detail | Static,
    }
}
