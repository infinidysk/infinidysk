using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;
using Serilog.Context;

namespace NzbWebDAV.Middlewares;

public sealed class RequestCorrelationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var traceId = RequestCorrelation.Resolve(context);
        RequestCorrelation.ApplyResponseHeader(context);
        using (LogContext.PushProperty(RequestCorrelation.LogPropertyName, traceId))
        {
            await next(context).ConfigureAwait(false);
        }
    }
}
