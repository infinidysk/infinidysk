using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Api.SabControllers.AddFile;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.SabControllers.SetQueuePriority;

public class SetQueuePriorityRequest
{
    public List<Guid> NzoIds { get; init; } = [];
    public QueueItem.PriorityOption Priority { get; init; }
    public CancellationToken CancellationToken { get; init; }

    public static async Task<SetQueuePriorityRequest> New(HttpContext httpContext)
    {
        var parsed = await SabNzoIdsParser.ParseAsync(httpContext, SigtermUtil.GetCancellationToken())
            .ConfigureAwait(false);
        var priority = AddFileRequest.MapPriorityOption(
            httpContext.GetRequestParam("value2") ?? httpContext.GetRequestParam("priority"));
        return new SetQueuePriorityRequest
        {
            NzoIds = parsed.NzoIds,
            Priority = priority,
            CancellationToken = SigtermUtil.GetCancellationToken(),
        };
    }
}
