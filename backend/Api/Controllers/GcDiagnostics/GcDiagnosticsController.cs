using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services.Diagnostics;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Api.Controllers.GcDiagnostics;

[ApiController]
[Route("api/gc-diagnostics")]
public sealed class GcDiagnosticsController(
    InFlightArticleBudget inFlightArticleBudget,
    GcDiagnosticsStore store) : BaseApiController
{
    protected override Task<IActionResult> HandleRequest()
    {
        if (!HttpMethods.IsPost(HttpContext.Request.Method))
        {
            return Task.FromResult<IActionResult>(
                StatusCode(StatusCodes.Status405MethodNotAllowed,
                    new BaseApiResponse { Status = false, Error = "POST required" }));
        }

        if (!store.TryBegin())
        {
            return Task.FromResult<IActionResult>(
                StatusCode(StatusCodes.Status429TooManyRequests,
                    new BaseApiResponse
                    {
                        Status = false,
                        Error = "A GC diagnostics run is already in progress.",
                    }));
        }

        try
        {
            var before = GcSnapshotBuilder.Capture();
            var stopwatch = Stopwatch.StartNew();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            stopwatch.Stop();

            var bufferPool = BufferPoolDiagnostics.Shared.Snapshot();
            var segmentPool = (PooledBufferStream.DefaultPool as SegmentBufferPool)?.Snapshot();
            var result = new GcDiagnosticsResult(
                DateTimeOffset.UtcNow,
                before,
                GcSnapshotBuilder.Capture(),
                stopwatch.ElapsedMilliseconds,
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
                segmentPool);
            store.Store(result);

            return Task.FromResult<IActionResult>(Ok(GcDiagnosticsResponse.FromResult(result)));
        }
        finally
        {
            store.End();
        }
    }
}
