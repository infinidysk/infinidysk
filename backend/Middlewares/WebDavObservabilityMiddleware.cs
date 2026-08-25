using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Services.SupportPack;
using Serilog;

namespace NzbWebDAV.Middlewares;

/// <summary>
/// Records slow and failed WebDAV requests so a transient scan-time failure is visible in
/// logs and in the support pack. Only paths under the WebDAV mount roots are counted.
/// </summary>
public class WebDavObservabilityMiddleware(RequestDelegate next)
{
    private static readonly TimeSpan SlowThreshold = TimeSpan.FromSeconds(5);
    private static readonly ConcurrentDictionary<string, long> Counters = new(StringComparer.Ordinal);

    // Test seam: the five-second default is impractical to exercise in a unit test.
    internal static TimeSpan? SlowThresholdOverride { get; set; }

    private static readonly HashSet<string> WebDavRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "/content",
        "/completed-symlinks",
        "/.ids",
        "/nzbs",
        "/view",
    };

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsWebDavRequest(context))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var method = context.Request.Method;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            stopwatch.Stop();
            var status = context.Response.StatusCode;
            var elapsedMs = stopwatch.ElapsedMilliseconds;
            var path = context.Request.Path.Value ?? context.Request.Path.ToUriComponent();

            Increment("total");

            var failed = status >= 500;
            var slow = elapsedMs >= (SlowThresholdOverride ?? SlowThreshold).TotalMilliseconds;
            if (failed) Increment("failed");
            if (slow) Increment("slow");

            if (failed)
            {
                Log.Warning(
                    "WebDAV request failed. Method={Method} Path={Path} Status={Status} DurationMs={DurationMs}",
                    method, path, status, elapsedMs);
            }
            else if (slow)
            {
                Log.Warning(
                    "Slow WebDAV request. Method={Method} Path={Path} Status={Status} DurationMs={DurationMs}",
                    method, path, status, elapsedMs);
            }

            if (context.RequestAborted.IsCancellationRequested)
                Increment("aborted");
        }
    }

    private static bool IsWebDavRequest(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (string.IsNullOrEmpty(path))
            return false;

        return WebDavRoots.Any(root =>
            path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && (path.Length == root.Length || path[root.Length] == '/'));
    }

    private static void Increment(string key) =>
        Counters.AddOrUpdate(key, 1, (_, count) => count + 1);

    internal static IReadOnlyDictionary<string, long> Snapshot() =>
        new Dictionary<string, long>(Counters, StringComparer.Ordinal);

    internal static void Reset() => Counters.Clear();
}
