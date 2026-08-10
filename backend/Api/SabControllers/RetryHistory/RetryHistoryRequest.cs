using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.SabControllers.RetryHistory;

public class RetryHistoryRequest
{
    public List<Guid> NzoIds { get; init; } = [];
    public CancellationToken CancellationToken { get; init; }

    public static async Task<RetryHistoryRequest> New(HttpContext httpContext)
    {
        var parsed = await SabNzoIdsParser.ParseAsync(httpContext, SigtermUtil.GetCancellationToken())
            .ConfigureAwait(false);
        if (parsed.NzoIds.Count == 0)
            throw new BadHttpRequestException("Missing or invalid value (nzo_id).");

        return new RetryHistoryRequest
        {
            NzoIds = parsed.NzoIds,
            CancellationToken = SigtermUtil.GetCancellationToken(),
        };
    }
}
