using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.SabControllers.Resume;

public class ResumeRequest
{
    public List<Guid> NzoIds { get; init; } = [];
    public CancellationToken CancellationToken { get; init; }

    public static async Task<ResumeRequest> New(HttpContext httpContext)
    {
        var parsed = await SabNzoIdsParser.ParseAsync(httpContext, SigtermUtil.GetCancellationToken())
            .ConfigureAwait(false);
        return new ResumeRequest
        {
            NzoIds = parsed.NzoIds,
            CancellationToken = SigtermUtil.GetCancellationToken(),
        };
    }
}
