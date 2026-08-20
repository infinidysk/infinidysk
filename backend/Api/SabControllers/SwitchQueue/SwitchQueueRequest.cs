using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.SabControllers.SwitchQueue;

public sealed class SwitchQueueRequest
{
    public Guid SourceId { get; init; }
    public string Target { get; init; } = "";
    public CancellationToken CancellationToken { get; init; }

    public static SwitchQueueRequest New(HttpContext context)
    {
        var errors = new ValidationErrors();
        var value = context.GetRequestParam("value");
        var value2 = context.GetRequestParam("value2");
        if (!Guid.TryParse(value, out var sourceId))
            errors.Add("value", "Switch expects a queue item id.");
        if (string.IsNullOrWhiteSpace(value2))
            errors.Add("value2", "Switch expects two parameters.");
        errors.ThrowIfAny();

        return new SwitchQueueRequest
        {
            SourceId = sourceId,
            Target = value2!,
            CancellationToken = context.RequestAborted,
        };
    }
}
