using Microsoft.AspNetCore.Http;
using NzbWebDAV.Auth;
using NzbWebDAV.Config;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Middlewares;

public sealed class MetricsAuthenticationMiddleware(RequestDelegate next, ConfigManager configManager)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.Equals("/metrics", StringComparison.OrdinalIgnoreCase) ||
            !EnvironmentUtil.IsVariableTrue("METRICS_REQUIRE_API_KEY"))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        try
        {
            ApiKeyValidator.Validate(context, configManager);
        }
        catch (UnauthorizedAccessException)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("Metrics authentication required.").ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}
