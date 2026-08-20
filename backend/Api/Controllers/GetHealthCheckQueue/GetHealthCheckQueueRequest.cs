using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.Controllers.GetHealthCheckQueue;

public class GetHealthCheckQueueRequest
{
    public int PageSize { get; init; } = 20;
    public CancellationToken CancellationToken { get; init; }

    public GetHealthCheckQueueRequest(HttpContext context)
    {
        var pageSizeParam = context.GetQueryParam("pageSize");
        CancellationToken = context.RequestAborted;
        var errors = new ValidationErrors();

        if (pageSizeParam is not null)
        {
            if (errors.TryParseInt("pageSize", pageSizeParam, "Invalid pageSize parameter", out var pageSize))
                PageSize = pageSize;
        }

        errors.ThrowIfAny();
    }
}
