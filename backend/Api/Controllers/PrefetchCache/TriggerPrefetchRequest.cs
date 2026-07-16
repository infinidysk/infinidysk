using Microsoft.AspNetCore.Http;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.Controllers.PrefetchCache;

public class TriggerPrefetchRequest
{
    public Guid DavItemId { get; }
    public CancellationToken CancellationToken { get; }

    public TriggerPrefetchRequest(HttpContext context)
    {
        CancellationToken = context.RequestAborted;

        var davItemIdParam = context.GetRequestParam("davItemId");
        if (davItemIdParam is null)
            throw new BadHttpRequestException("Missing required parameter: davItemId");
        if (!Guid.TryParse(davItemIdParam, out var davItemId))
            throw new BadHttpRequestException("Invalid davItemId parameter");
        DavItemId = davItemId;
    }
}
