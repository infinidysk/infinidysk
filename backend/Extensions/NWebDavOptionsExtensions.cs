using Microsoft.AspNetCore.Http;
using NWebDav.Server;

namespace NzbWebDAV.Extensions;

public static class NWebDavOptionsExtensions
{
    public static Func<HttpContext, bool> GetFilter(this NWebDavOptions options)
    {
        return context => !context.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal) &&
                          !context.Request.Path.StartsWithSegments("/view", StringComparison.Ordinal) &&
                          !context.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal) &&
                          !context.Request.Path.StartsWithSegments("/ready", StringComparison.Ordinal) &&
                          !context.Request.Path.StartsWithSegments("/metrics", StringComparison.Ordinal) &&
                          !context.Request.Path.StartsWithSegments("/ws", StringComparison.Ordinal) &&
                          !context.Request.Path.StartsWithSegments("/p", StringComparison.Ordinal) &&
                          !context.Request.Path.StartsWithSegments("/adapters", StringComparison.Ordinal);
    }
}
