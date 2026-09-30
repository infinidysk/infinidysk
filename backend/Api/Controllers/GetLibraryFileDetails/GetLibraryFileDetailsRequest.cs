using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.Controllers.GetLibraryFileDetails;

public class GetLibraryFileDetailsRequest
{
    public Guid DavItemId { get; init; }
    public CancellationToken CancellationToken { get; init; }

    public GetLibraryFileDetailsRequest(HttpContext context)
    {
        CancellationToken = context.RequestAborted;
        var errors = new ValidationErrors();
        var raw = context.GetQueryParam("davItemId");
        if (!Guid.TryParse(raw, out var davItemId))
            errors.Add("davItemId", "Invalid davItemId parameter (use a UUID).");
        else
            DavItemId = davItemId;
        errors.ThrowIfAny();
    }
}
