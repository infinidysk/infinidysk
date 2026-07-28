using Microsoft.AspNetCore.Http;
using NWebDav.Server.Handlers;

namespace NzbWebDAV.WebDav.Base;

public class PropFindHandlerPatch(PropFindHandler inner) : IRequestHandler
{
    internal const string FiniteDepthErrorBody =
        """<?xml version="1.0" encoding="utf-8"?><D:error xmlns:D="DAV:"><D:propfind-finite-depth/></D:error>""";

    public async Task<bool> HandleRequestAsync(HttpContext httpContext)
    {
        var handled = await inner.HandleRequestAsync(httpContext).ConfigureAwait(false);
        await TryWriteFiniteDepthErrorAsync(httpContext).ConfigureAwait(false);
        return handled;
    }

    internal static async Task<bool> TryWriteFiniteDepthErrorAsync(HttpContext httpContext)
    {
        var response = httpContext.Response;
        if (response.HasStarted || response.StatusCode != StatusCodes.Status403Forbidden)
            return false;

        response.ContentType = "application/xml; charset=utf-8";
        await response.WriteAsync(FiniteDepthErrorBody, httpContext.RequestAborted).ConfigureAwait(false);
        return true;
    }
}
