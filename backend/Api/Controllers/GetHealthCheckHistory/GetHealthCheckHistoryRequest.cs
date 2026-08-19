using Microsoft.AspNetCore.Http;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.Controllers.GetHealthCheckHistory;

public class GetHealthCheckHistoryRequest
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public IReadOnlySet<HealthCheckResult.RepairAction>? RepairStatuses { get; init; }
    public IReadOnlySet<HealthCheckResult.HealthResult>? Results { get; init; }
    public CancellationToken CancellationToken { get; init; }

    public GetHealthCheckHistoryRequest(HttpContext context)
    {
        var pageParam = context.GetQueryParam("page");
        var pageSizeParam = context.GetQueryParam("pageSize");
        var repairStatusParam = context.GetQueryParam("repairStatus");
        var resultParam = context.GetQueryParam("result");
        CancellationToken = context.RequestAborted;

        if (pageParam is not null)
        {
            if (!int.TryParse(pageParam, out var page) || page < 1)
                throw new BadHttpRequestException("Invalid page parameter");
            Page = page;
        }

        if (pageSizeParam is not null)
        {
            if (!int.TryParse(pageSizeParam, out var pageSize) || pageSize is < 1 or > 250)
                throw new BadHttpRequestException("Invalid pageSize parameter");
            PageSize = pageSize;
        }

        if (repairStatusParam is not null)
        {
            var repairStatuses = new HashSet<HealthCheckResult.RepairAction>();
            foreach (var status in repairStatusParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (status.ToLowerInvariant())
                {
                    case "none":
                        repairStatuses.Add(HealthCheckResult.RepairAction.None);
                        break;
                    case "repaired":
                        // "repaired" covers both repair paths so PAR2 fixes show up in
                        // the Repaired filter and the default deleted,repaired view.
                        repairStatuses.Add(HealthCheckResult.RepairAction.Repaired);
                        repairStatuses.Add(HealthCheckResult.RepairAction.RepairedViaPar2);
                        break;
                    case "deleted":
                        repairStatuses.Add(HealthCheckResult.RepairAction.Deleted);
                        break;
                    case "action-needed":
                        repairStatuses.Add(HealthCheckResult.RepairAction.ActionNeeded);
                        break;
                    default:
                        throw new BadHttpRequestException(
                            "Invalid repairStatus parameter (use none, repaired, deleted, or action-needed)");
                }
            }
            // An empty or comma-only repairStatus value (e.g. "?repairStatus=") means "no filter"
            // rather than filtering out every row.
            RepairStatuses = repairStatuses.Count > 0 ? repairStatuses : null;
        }

        if (resultParam is not null)
        {
            var results = new HashSet<HealthCheckResult.HealthResult>();
            foreach (var result in resultParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                results.Add(result.ToLowerInvariant() switch
                {
                    "healthy" => HealthCheckResult.HealthResult.Healthy,
                    "unhealthy" => HealthCheckResult.HealthResult.Unhealthy,
                    "degraded" => HealthCheckResult.HealthResult.Degraded,
                    _ => throw new BadHttpRequestException(
                        "Invalid result parameter (use healthy, unhealthy, or degraded)")
                });
            }
            // Same convention as repairStatus: an empty or comma-only value means "no filter".
            Results = results.Count > 0 ? results : null;
        }
    }
}
