using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NWebDav.Server.Stores;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Services;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;
using NzbWebDAV.WebDav.Requests;

namespace NzbWebDAV.Api.Controllers.GetWebdavItem;

[ApiController]
[Route("view/{*path}")]
public class GetWebdavItemController(
    DatabaseStore store,
    ConfigManager configManager,
    ProviderUsageTracker providerUsageTracker,
    ActiveReadRegistry activeReadRegistry,
    ConcurrentReadTracker concurrentReadTracker,
    CandidateNegativeCache negativeCache,
    StreamTraceBuffer streamTrace
) : ControllerBase
{
    private async Task<Stream> GetWebdavItem(GetWebdavItemRequest request, CancellationToken ct)
    {
        // /view streams outside NWebDav; attach the same streaming timeout context
        // BaseStoreStreamFile sets for WebDAV so segment fetches fail fast.
        // BaseStoreStreamFile may overwrite this on the same token — both scopes
        // dispose safely (second Remove is a no-op).
        var streamingTimeoutContext = new StreamingTimeoutContext
        {
            PerSegmentTimeout = configManager.GetStreamingSegmentTimeout(),
            MaxRetries = configManager.GetStreamingSegmentRetries(),
        };
#pragma warning disable CA2000 // scoped context is disposed via Response.OnCompleted when the response completes
        var scopedStreamingTimeoutContext = ct.SetContext(streamingTimeoutContext);
#pragma warning restore CA2000
        HttpContext.Response.OnCompleted(() =>
        {
            scopedStreamingTimeoutContext.Dispose();
            return Task.CompletedTask;
        });

        var item = await store.GetItemAsync(request.Item, ct).ConfigureAwait(false);
        if (item is null) throw new BadHttpRequestException("The file does not exist.");
        if (item is IStoreCollection) throw new BadHttpRequestException("The file does not exist.");

        // disable compression to keep Content-Length intact for clients that need seeking
        Response.Headers["Content-Encoding"] = "identity";

        // handle par2 preview
        if (string.Equals(Path.GetExtension(item.Name), ".par2", StringComparison.OrdinalIgnoreCase) && configManager.IsPreviewPar2FilesEnabled())
            return await GetPar2PreviewStream(item, ct).ConfigureAwait(false);

        // Provisional budget for fully-specified ranges before stream creation.
        if (request.RangeStart is { } provisionalStart && request.RangeEnd is { } provisionalEnd)
            RangeContext.SetReadBudget(provisionalEnd - provisionalStart + 1);

        // get the file stream and set the file-size in header
        var stream = await item.GetReadableStreamAsync(ct).ConfigureAwait(false);
        var fileSize = stream.Length;

        var idFile = item as DatabaseStoreIdFile;

        if (idFile?.HistoryItemId is { } hid)
            HttpContext.Items["historyItemId"] = hid;

        // .ids items expose the GUID as Name so symlink targets stay stable.
        // Use the human-readable name for response headers and active reads.
        var fileName = idFile?.FriendlyName ?? item.Name;

        if (HttpContext.Items["readSessionId"] is Guid sid)
            activeReadRegistry.UpdateInfo(sid, fileName, fileSize);

        // set the content-type and content-disposition headers
        Response.Headers["Content-Type"] = ContentHeaderUtil.GetContentType(fileName);
        Response.Headers["Content-Disposition"] =
            ContentHeaderUtil.GetContentDisposition(fileName, request.ShouldDownload);

        // disable compression to keep Content-Length intact for clients that need seeking
        Response.Headers["Content-Encoding"] = "identity";
        Response.Headers["Accept-Ranges"] = "bytes";

        // Resolve the suffix form ("bytes=-N", last N bytes) now that fileSize
        // is known. Clamp at zero so an oversized suffix means "the whole file"
        // rather than seeking before byte 0.
        long? rangeStart = request.RangeStart;
        long? rangeEnd = request.RangeEnd;
        if (request.SuffixLength is { } suffixLen)
        {
            rangeStart = Math.Max(0, fileSize - suffixLen);
            rangeEnd = fileSize - 1;
        }

        // Stash the effective start so HandleRequest can report playback
        // position from the real offset (not from 0) for suffix-range reads.
        HttpContext.Items["effectiveRangeStart"] = rangeStart ?? 0L;

        if (rangeStart is not null)
        {
            // clamp a range end that runs past the file to the last byte
            // so the response headers stay valid.
            var end = ResolveRangeEnd(rangeEnd, fileSize);

            // Syntactically valid but unsatisfiable → 416 (mirror WebDAV handler).
            if (rangeStart.Value < 0 || rangeStart.Value >= fileSize || rangeStart.Value > end)
            {
                Response.Headers["Content-Range"] = $"bytes */{fileSize}";
                Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                await stream.DisposeAsync().ConfigureAwait(false);
                return Stream.Null;
            }

            var chunkSize = 1 + end - rangeStart.Value;

            // Cap prefetch at the range end before Seek recreates segment streams.
            RangeContext.SetReadBudget(chunkSize);

            // seek
            stream.Seek(rangeStart.Value, SeekOrigin.Begin);
#pragma warning disable CA2000 // the length-limited wrapper is returned as the response stream; the response pipeline disposes it (and the inner stream)
            if (rangeEnd is not null) stream = stream.LimitLength(chunkSize);
#pragma warning restore CA2000

            // set response headers
            Response.Headers["Content-Range"] = $"bytes {rangeStart}-{end}/{fileSize}";
            Response.Headers["Content-Length"] = chunkSize.ToString();
            Response.StatusCode = 206;
            HttpContext.Items["effectiveRangeEnd"] = end;
        }
        else
        {
            RangeContext.SetReadBudget(null);
            Response.Headers["Content-Length"] = fileSize.ToString();
            HttpContext.Items["effectiveRangeEnd"] = (long?)null;
        }

        return stream;
    }

    [HttpGet]
    public async Task HandleRequest()
    {
        try
        {
            HttpContext.Items["configManager"] = configManager;
            var request = new GetWebdavItemRequest(HttpContext);
            using var concurrentReadScope = concurrentReadTracker.BeginRead(
                request.Item,
                request.SuffixLength.HasValue ? null : request.RangeStart ?? 0,
                ResolveReadRegion(request));
            var sessionId = TrackReadSession(request.Item);
            HttpContext.Items["readSessionId"] = sessionId;
            using var scope = providerUsageTracker.BeginScope(sessionId);
            using var metricsScope = MultiProviderNntpClient.BeginReadSessionScope(sessionId);

            // Bound the initial backend wait (store lookup + stream open). Cleared once
            // body copy starts — mid-stream stalls use per-segment timeouts. See
            // GetAndHeadHandlerPatch for the WebDAV parity path.
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            readCts.CancelAfter(configManager.GetStreamingReadTimeout());
            var ct = readCts.Token;

            StreamTraceRangeContext? traceRange = null;
            try
            {
                await using var response = await GetWebdavItem(request, ct).ConfigureAwait(false);
                if (response == Stream.Null)
                    return;
                var effectiveStart = (long)(HttpContext.Items["effectiveRangeStart"] ?? 0L);
                concurrentReadScope.UpdateStart(effectiveStart);
                var rangeEnd = HttpContext.Items["effectiveRangeEnd"] as long?;
                traceRange = streamTrace.RangeOpen(
                    sessionId,
                    request.Item,
                    "GET",
                    effectiveStart,
                    rangeEnd,
                    response.CanSeek ? response.Length : null,
                    Request.Headers.UserAgent.ToString(),
                    HttpContext.Connection.RemoteIpAddress?.ToString());
                using var traceRangeScope = MultiProviderNntpClient.BeginStreamTraceRangeScope(traceRange);
                try
                {
                    // Body transfer can run for minutes; drop the admission/open
                    // deadline and rely on per-segment mid-stream timeouts.
                    readCts.CancelAfter(Timeout.InfiniteTimeSpan);
                    await CopyAndReportAsync(response, Response.Body, sessionId, effectiveStart, traceRange, ct).ConfigureAwait(false);
                    FinishRange(sessionId, traceRange, ReadSession.EndReasonCode.Completed);
                }
                catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
                {
                    FinishRange(sessionId, traceRange, ReadSession.EndReasonCode.Aborted);
                    throw;
                }
                catch (Exception ex)
                {
                    FinishRange(sessionId, traceRange, ReadSession.EndReasonCode.Error, ex.Message);
                    throw;
                }
            }
            catch (OperationCanceledException oce) when (!HttpContext.RequestAborted.IsCancellationRequested)
            {
                FinishRange(sessionId, traceRange, ReadSession.EndReasonCode.Error, "streaming-read-timeout");
                throw new StreamingReadTimeoutException(
                    "WebDAV /view read exceeded the " +
                    $"{configManager.GetStreamingReadTimeout().TotalSeconds:0}s streaming-read-timeout " +
                    "while waiting for the Usenet backend.",
                    oce);
            }
        }
        catch (UnauthorizedAccessException)
        {
            Response.StatusCode = 401;
        }
    }

    private static ConcurrentReadRegion ResolveReadRegion(GetWebdavItemRequest request)
    {
        if (request.SuffixLength.HasValue) return ConcurrentReadRegion.SuffixRange;
        if (!request.RangeStart.HasValue) return ConcurrentReadRegion.Full;
        return request.RangeStart.Value == 0
            ? ConcurrentReadRegion.StartRange
            : ConcurrentReadRegion.OffsetRange;
    }

    private void FinishRange(
        Guid sessionId,
        StreamTraceRangeContext? traceRange,
        ReadSession.EndReasonCode reason,
        string? message = null)
    {
        activeReadRegistry.SetEndReason(sessionId, reason);
        streamTrace.RangeEnd(
            sessionId, traceRange, reason, activeReadRegistry.GetBytesRead(sessionId), message);
    }

    private async Task CopyAndReportAsync(
        Stream src,
        Stream dest,
        Guid sessionId,
        long startOffset,
        StreamTraceRangeContext? traceRange,
        CancellationToken ct)
    {
        // 64 KB chunks; after each write report (bytesRead, absolutePosition)
        // so the Right-Now panel can show real playback location and the
        // throughput rate populates correctly.
        var buffer = new byte[64 * 1024];
        var position = startOffset;
        while (true)
        {
            int read;
            try
            {
                read = await src.ReadAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                if (HttpContext.Items["historyItemId"] is Guid hid)
                {
                    negativeCache.MarkHistoryItemBroken(hid);
                    Serilog.Log.Warning(
                        "Mid-read failed at offset {Offset} for HistoryItem {HistoryItemId}: {Message}",
                        position, hid, e.Message);
                    PoisonFileNameAsync(hid);
                }
                throw;
            }
            if (read <= 0) break;
            var writeStarted = Stopwatch.GetTimestamp();
            await dest.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            streamTrace.AddStall(
                traceRange, StreamStallKind.ClientWrite, Stopwatch.GetElapsedTime(writeStarted));
            position += read;
            activeReadRegistry.Touch(sessionId, read, position);
        }
    }

    private void PoisonFileNameAsync(Guid historyItemId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await using var ctx = new DavDatabaseContext();
                var fileName = await ctx.HistoryItems.AsNoTracking()
                    .Where(h => h.Id == historyItemId)
                    .Select(h => h.FileName)
                    .FirstOrDefaultAsync().ConfigureAwait(false);
                if (!string.IsNullOrEmpty(fileName))
                    negativeCache.MarkFileNameBroken(fileName);
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "PoisonFileNameAsync for {HistoryItemId} failed", historyItemId);
            }
        });
    }

    private Guid TrackReadSession(string itemPath)
    {
        // Provisional name from the URL path. GetWebdavItem replaces it with
        // item.Name (the real human-readable filename) once the store lookup runs.
        var fileName = Path.GetFileName(itemPath);
        var userAgent = Request.Headers.UserAgent.ToString();
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        var clientKey = $"{clientIp}|{userAgent}";
        return activeReadRegistry.GetOrCreate(itemPath, clientKey, fileName, fileSize: null, userAgent, clientIp);
    }

    [HttpHead]
    public async Task HandleHeadRequest()
    {
        try
        {
            HttpContext.Items["configManager"] = configManager;
            var request = new GetWebdavItemRequest(HttpContext);

            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            readCts.CancelAfter(configManager.GetStreamingReadTimeout());
            var ct = readCts.Token;

            try
            {
                await using var response = await GetWebdavItem(request, ct).ConfigureAwait(false);
                // HEAD: headers already set, body omitted
            }
            catch (OperationCanceledException oce) when (!HttpContext.RequestAborted.IsCancellationRequested)
            {
                throw new StreamingReadTimeoutException(
                    "WebDAV /view HEAD exceeded the " +
                    $"{configManager.GetStreamingReadTimeout().TotalSeconds:0}s streaming-read-timeout " +
                    "while waiting for the Usenet backend.",
                    oce);
            }
        }
        catch (UnauthorizedAccessException)
        {
            Response.StatusCode = 401;
        }
    }

    /// <summary>
    /// Resolves the inclusive range end for a /view response, clamping past-EOF
    /// ends to the last byte (RFC 7233 / WebDAV parity).
    /// </summary>
    internal static long ResolveRangeEnd(long? rangeEnd, long fileSize) =>
        Math.Min(rangeEnd ?? (fileSize - 1), fileSize - 1);

    private async Task<Stream> GetPar2PreviewStream(IStoreItem item, CancellationToken ct)
    {
        Response.Headers.ContentType = "text/plain";
        await using var stream = await item.GetReadableStreamAsync(ct).ConfigureAwait(false);
        var fileDescriptors = await Par2.ReadFileDescriptions(stream, ct).GetAllAsync(ct: ct)
            .ConfigureAwait(false);
        return new MemoryStream(Encoding.UTF8.GetBytes(fileDescriptors.ToIndentedJson()));
    }
}
