using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Benchmark;

namespace NzbWebDAV.Api.Controllers.BenchmarkUsenetConnection;

[ApiController]
[Route("api/benchmark-usenet-connection")]
public class BenchmarkUsenetConnectionController(
    UsenetBenchmarkService benchmarkService,
    BenchmarkGate benchmarkGate,
    BenchmarkRunControl runControl,
    ActiveReadRegistry activeReads,
    QueueManager queueManager,
    ConfigManager configManager
) : BaseApiController
{
    // One benchmark at a time, process-wide: overlapping tests contend for the same
    // provider budget and poison each other's numbers.
    private static readonly SemaphoreSlim SingleFlight = new(1, 1);

    private async Task<BenchmarkUsenetConnectionResponse> BenchmarkAsync(BenchmarkUsenetConnectionRequest request)
    {
        if (request.Cancel)
        {
            runControl.Cancel();
            return new BenchmarkUsenetConnectionResponse { Status = true };
        }

        if (!await SingleFlight.WaitAsync(TimeSpan.Zero).ConfigureAwait(false))
            return new BenchmarkUsenetConnectionResponse
            {
                Status = false,
                Error = "A speed test is already running. Wait for it to finish (or cancel it) and try again."
            };

        // Do not link to HttpContext.RequestAborted: intermediaries often drop the
        // long-lived POST (~30s TTFB/idle) while the UI is still watching websocket
        // progress. Only an explicit cancel (Cancel button / modal close) aborts.
        var ct = runControl.Begin();

        try
        {
            // Pause the download queue + background verifiers for the duration so
            // the test gets the provider's full connection budget. The gate is
            // released on dispose — including on cancel or error.
            using var pause = benchmarkGate.Enter();

            // Activity we can't pause — a download already mid-flight or live
            // streams — still uses connections; capture it so we can flag it.
            var streamsBefore = activeReads.Count;
            var inProgressBefore = queueManager.HasActiveQueueItems;

            var result = await benchmarkService.RunAsync(
                request.ToConnectionDetails(),
                request.MaxConnections,
                request.TransferTestConnections,
                request.Intensity,
                request.PipeliningOnly,
                request.DataBudgetBytes,
                request.VerifyConnections,
                ct
            ).ConfigureAwait(false);

            var streamsAfter = activeReads.Count;
            var inProgressAfter = queueManager.HasActiveQueueItems;
            AddContentionWarnings(
                result,
                streamsBefore,
                streamsAfter,
                inProgressBefore,
                inProgressAfter);

            return new BenchmarkUsenetConnectionResponse { Status = true, Result = result };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new BenchmarkUsenetConnectionResponse
            {
                Status = false,
                Error = "Speed test cancelled."
            };
        }
        catch (CouldNotConnectToUsenetException)
        {
            const string error = "Couldn't connect to the provider. Check the host, port and SSL settings.";
            benchmarkService.ReportFailure(error);
            return new BenchmarkUsenetConnectionResponse
            {
                Status = false,
                Error = error
            };
        }
        catch (CouldNotLoginToUsenetException)
        {
            const string error = "Couldn't log in to the provider. Check the username and password.";
            benchmarkService.ReportFailure(error);
            return new BenchmarkUsenetConnectionResponse
            {
                Status = false,
                Error = error
            };
        }
        finally
        {
            SingleFlight.Release();
        }
    }

    private static void AddContentionWarnings(
        BenchmarkResult result,
        int streamsBefore,
        int streamsAfter,
        bool downloadBefore,
        bool downloadAfter)
    {
        var bits = new List<string>();
        if (downloadBefore) bits.Add("a download was still finishing");
        else if (downloadAfter) bits.Add("a download started mid-test");
        var streams = Math.Max(streamsBefore, streamsAfter);
        if (streams > 0) bits.Add($"{streams} active stream{(streams == 1 ? "" : "s")}");
        if (bits.Count == 0) return;

        result.ContentionWarnings.Add(
            $"{string.Join(" and ", bits)} during the test — those connections can't be paused, " +
            "so the reported MB/s is what we saw while other traffic was active, not your idle line rate. " +
            "Re-run when fully idle for the cleanest result.");

        // Concurrent traffic caps how much the numbers can be trusted.
        result.Confidence = downloadBefore || downloadAfter
            ? "low"
            : (result.Confidence == "high" ? "medium" : result.Confidence);
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new BenchmarkUsenetConnectionRequest(HttpContext, configManager);
        var response = await BenchmarkAsync(request).ConfigureAwait(false);
        return Ok(response);
    }
}
