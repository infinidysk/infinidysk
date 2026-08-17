using Microsoft.AspNetCore.Http;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.Controllers.GetHealthCheckHistory;

public class GetHealthCheckHistoryRequest
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public IReadOnlySet<HealthCheckResult.RepairAction>? RepairStatuses { get; init; }
    public CancellationToken CancellationToken { get; init; }

    public GetHealthCheckHistoryRequest(HttpContext context)
    {
        var pageParam = context.GetQueryParam("page");
        var pageSizeParam = context.GetQueryParam("pageSize");
        var repairStatusParam = context.GetQueryParam("repairStatus");
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
                repairStatuses.Add(status.ToLowerInvariant() switch
                {
                    "none" => HealthCheckResult.RepairAction.None,
                    "repaired" => HealthCheckResult.RepairAction.Repaired,
                    "deleted" => HealthCheckResult.RepairAction.Deleted,
                    "action-needed" => HealthCheckResult.RepairAction.ActionNeeded,
                    _ => throw new BadHttpRequestException(
                        "Invalid repairStatus parameter (use none, repaired, deleted, or action-needed)")
                });
            }
            // An empty or comma-only repairStatus value (e.g. "?repairStatus=") means "no filter"
            // rather than filtering out every row.
            RepairStatuses = repairStatuses.Count > 0 ? repairStatuses : null;
        }
    }
}
