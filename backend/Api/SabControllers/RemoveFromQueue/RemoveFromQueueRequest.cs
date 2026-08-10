using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.SabControllers.RemoveFromQueue;

public class RemoveFromQueueRequest()
{
    public List<Guid> NzoIds { get; init; } = [];
    public bool DeleteAll { get; init; }
    public string? Category { get; init; }
    public bool DeleteFilesRequested { get; init; }
    public CancellationToken CancellationToken { get; init; }

    public static async Task<RemoveFromQueueRequest> New(HttpContext httpContext)
    {
        var cancellationToken = SigtermUtil.GetCancellationToken();
        var query = SabDeleteValueParser.Parse(httpContext, allowFailed: false);
        var parsed = await SabNzoIdsParser.ParseAsync(httpContext, cancellationToken).ConfigureAwait(false);
        var category = httpContext.GetRequestParam("cat")
                       ?? httpContext.GetRequestParam("category");
        if (category == "*")
            category = null;

        return new RemoveFromQueueRequest()
        {
            NzoIds = query.DeleteAll
                ? []
                : query.NzoIds.Concat(parsed.NzoIds).Distinct().ToList(),
            DeleteAll = query.DeleteAll,
            Category = category,
            DeleteFilesRequested = httpContext.Request.Query["del_files"] == "1",
            CancellationToken = cancellationToken
        };
    }
}
