using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.SabControllers.Pause;

public class PauseRequest
{
    public List<Guid> NzoIds { get; init; } = [];
    public CancellationToken CancellationToken { get; init; }

    public static async Task<PauseRequest> New(HttpContext httpContext)
    {
        var parsed = await SabNzoIdsParser.ParseAsync(httpContext, SigtermUtil.GetCancellationToken())
            .ConfigureAwait(false);
        return new PauseRequest
        {
            NzoIds = parsed.NzoIds,
            CancellationToken = SigtermUtil.GetCancellationToken(),
        };
    }
}
