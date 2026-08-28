using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services.Diagnostics;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Api.Controllers.GcDiagnostics;

[ApiController]
[Route("api/gc-diagnostics")]
public sealed class GcDiagnosticsController(
    InFlightArticleBudget inFlightArticleBudget,
    GcDiagnosticsStore store,
    IGcDiagnosticsExecutor executor) : PostOnlyApiController
{
    protected override Task<IActionResult> HandleRequest()
    {
        var admission = store.TryBegin();
        if (admission.Status != GcDiagnosticsAdmission.Started)
        {
            if (admission.RetryAfterSeconds is { } retryAfter)
                Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);

            var error = admission.Status == GcDiagnosticsAdmission.AlreadyRunning
                ? "A GC diagnostics run is already in progress."
                : "A GC diagnostics run recently completed. Wait before retrying.";
            return Task.FromResult<IActionResult>(
                StatusCode(StatusCodes.Status429TooManyRequests,
                    new BaseApiResponse
                    {
                        Status = false,
                        Error = error,
                    }));
        }

        try
        {
            var execution = executor.Execute();
            var bufferPool = BufferPoolDiagnostics.Shared.Snapshot();
            var segmentPool = (PooledBufferStream.DefaultPool as SegmentBufferPool)?.Snapshot();
            var result = new GcDiagnosticsResult(
                DateTimeOffset.UtcNow,
                execution.Before,
                execution.After,
                execution.PauseMs,
                new GcBufferRetention(
                    inFlightArticleBudget.LeasedBytes,
                    inFlightArticleBudget.CapBytes,
                    inFlightArticleBudget.ThrottleEvents,
                    bufferPool.Rents,
                    bufferPool.Returns,
                    bufferPool.Growths,
                    bufferPool.CheckedOutBytes,
                    bufferPool.RequestedBytes,
                    bufferPool.RentedBytes,
                    bufferPool.BucketWasteBytes),
                segmentPool)
            {
                CollectionMode = "Aggressive",
                FullBlockingCollectionsRequested = 2,
            };
            store.Store(result);

            return Task.FromResult<IActionResult>(Ok(GcDiagnosticsResponse.FromResult(result)));
        }
        finally
        {
            store.End();
        }
    }
}
