using Microsoft.AspNetCore.Http;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.SabControllers.SwitchQueue;

public sealed class SwitchQueueRequest
{
    public Guid SourceId { get; init; }
    public string Target { get; init; } = "";
    public CancellationToken CancellationToken { get; init; }

    public static SwitchQueueRequest New(HttpContext context)
    {
        var value = context.GetRequestParam("value");
        var value2 = context.GetRequestParam("value2");
        if (!Guid.TryParse(value, out var sourceId) || string.IsNullOrWhiteSpace(value2))
            throw new BadHttpRequestException("Switch expects two parameters.");

        return new SwitchQueueRequest
        {
            SourceId = sourceId,
            Target = value2,
            CancellationToken = context.RequestAborted,
        };
    }
}
