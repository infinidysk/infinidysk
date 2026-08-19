using System.Buffers;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using NWebDav.Server;
using NWebDav.Server.Handlers;
using NWebDav.Server.Helpers;
using NWebDav.Server.Props;
using NWebDav.Server.Stores;
using NzbWebDAV.Api.Controllers.GetWebdavItem;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Services;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav.Requests;

namespace NzbWebDAV.WebDav.Base;

/// <summary>
/// Implementation of the GET and HEAD method.
/// </summary>
/// <remarks>
/// The specification of the WebDAV GET and HEAD methods for collections
/// can be found in the
/// <see href="http://www.webdav.org/specs/rfc2518.html#rfc.section.8.4">
/// WebDAV specification
/// </see>.
/// </remarks>
public class GetAndHeadHandlerPatch : IRequestHandler
{
    private readonly IStore _store;
    private readonly ConfigManager _configManager;
    private readonly ProviderUsageTracker _providerUsageTracker;
    private readonly ActiveReadRegistry _activeReadRegistry;
    private readonly ConcurrentReadTracker _concurrentReadTracker;
    private readonly StreamTraceBuffer _streamTrace;
    private readonly StreamingFailureTracker _failureTracker;
    private readonly SharedStreamRegistry _sharedStreams;

    public GetAndHeadHandlerPatch(
        IStore store,
        ConfigManager configManager,
        ProviderUsageTracker providerUsageTracker,
        ActiveReadRegistry activeReadRegistry,
        ConcurrentReadTracker concurrentReadTracker,
        StreamTraceBuffer streamTrace,
        StreamingFailureTracker failureTracker,
        SharedStreamRegistry sharedStreams)
    {
        _store = store;
        _configManager = configManager;
        _providerUsageTracker = providerUsageTracker;
        _activeReadRegistry = activeReadRegistry;
        _concurrentReadTracker = concurrentReadTracker;
        _streamTrace = streamTrace;
        _failureTracker = failureTracker;
        _sharedStreams = sharedStreams;
    }

    /// <summary>
    /// Handle a GET or HEAD request.
    /// </summary>
    /// <param name="httpContext">
    /// The HTTP context of the request.
    /// </param>
    /// <returns>
    /// A task that represents the asynchronous GET or HEAD operation. The
    /// task will always return <see langword="true"/> upon completion.
    /// </returns>
    public async Task<bool> HandleRequestAsync(HttpContext httpContext)
    {
        // Obtain request and response
        var request = httpContext.Request;
        var response = httpContext.Response;

        // Determine if we are invoked as HEAD
        var isHeadRequest = request.Method == HttpMethods.Head;

        // Determine the requested range (ignore malformed / non-bytes / HEAD)
        var rangeHeader = request.Headers["Range"].FirstOrDefault() ?? "";
        var range = TryResolveRange(isHeadRequest, rangeHeader);
        var copyStart = 0L;
        long? copyEnd = null;

        // Provisional budget from a fully-specified Range before the stream is
        // constructed so MultiSegmentStream can cap prefetch from the start.
        if (range is { Start: not null, End: not null })
            RangeContext.SetReadBudget(range.End.Value - range.Start.Value + 1);

        // Bound the initial backend wait (store lookup + stream open / first segment)
        // so a stuck provider fails the HTTP request instead of blocking until the
        // client disconnects. Cleared once body copy starts — mid-stream stalls use
        // the per-segment StreamingTimeoutContext, not this wall clock. Every
        // downstream await below must observe `ct` (not httpContext.RequestAborted
        // directly) — StreamingTimeoutContext / CancellationTokenContext key by the
        // exact token instance passed in.
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted);
        readCts.CancelAfter(_configManager.GetStreamingReadTimeout());
        var ct = readCts.Token;

        try
        {
            return await HandleRequestCoreAsync(
                    httpContext, request, response, isHeadRequest, range, copyStart, copyEnd, readCts, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException oce) when (
            oce is not StreamingWriteTimeoutException
            && !httpContext.RequestAborted.IsCancellationRequested)
        {
            throw new StreamingReadTimeoutException(
                "WebDAV read exceeded the " +
                $"{_configManager.GetStreamingReadTimeout().TotalSeconds:0}s streaming-read-timeout " +
                "while waiting for the Usenet backend.",
                oce);
        }
    }

    private async Task<bool> HandleRequestCoreAsync(
        HttpContext httpContext,
        HttpRequest request,
        HttpResponse response,
        bool isHeadRequest,
        NWebDav.Server.Helpers.Range? range,
        long copyStart,
        long? copyEnd,
        CancellationTokenSource readCts,
        CancellationToken ct)
    {
        // Obtain the WebDAV collection
        var entry = await _store.GetItemAsync(request.GetUri(), ct).ConfigureAwait(false);
        if (entry == null)
        {
            // Set status to not found
            response.SetStatus(DavStatusCode.NotFound);
            return true;
        }

        var path = request.GetUri().AbsolutePath;
        // Key the shared-stream audit by the decoded store path (the same form
        // /view passes), otherwise overlap between the two entry points on the
        // same file is missed whenever the path needs percent-encoding.
        using var concurrentReadScope = isHeadRequest
            ? null
            : _concurrentReadTracker.BeginRead(
                Uri.UnescapeDataString(path),
                range?.Start ?? (range is null ? 0 : null),
                ResolveReadRegion(range));

        // ETag might be used for a conditional request
        string? etag = null;

        // Add non-expensive headers based on properties
        var propertyManager = entry.PropertyManager;
        if (propertyManager != null)
        {
            // Add Last-Modified header
            var lastModifiedUtc = (string?)await propertyManager.GetPropertyAsync(entry, DavGetLastModified<IStoreItem>.PropertyName, true, ct).ConfigureAwait(false);
            if (lastModifiedUtc != null)
                response.Headers.LastModified = lastModifiedUtc;

            // Add ETag
            etag = (string?)await propertyManager.GetPropertyAsync(entry, DavGetEtag<IStoreItem>.PropertyName, true, ct).ConfigureAwait(false);
            if (etag != null)
                response.Headers.ETag = etag;

            // Add type
            var contentType = (string?)await propertyManager.GetPropertyAsync(entry, DavGetContentType<IStoreItem>.PropertyName, true, ct).ConfigureAwait(false);
            if (contentType != null)
                response.ContentType = contentType;

            // Add language
            var contentLanguage = (string?)await propertyManager.GetPropertyAsync(entry, DavGetContentLanguage<IStoreItem>.PropertyName, true, ct).ConfigureAwait(false);
            if (contentLanguage != null)
                response.Headers.ContentLanguage = contentLanguage;
        }

        if (entry is DatabaseStoreIdFile friendlyIdFile)
        {
            response.ContentType = ContentHeaderUtil.GetContentType(friendlyIdFile.FriendlyName);
            response.Headers.ContentDisposition =
                ContentHeaderUtil.GetContentDisposition(friendlyIdFile.FriendlyName, shouldDownload: false);
        }

        // Every in-tree file exposes its persisted size as metadata. HEAD must
        // not create a streaming read just to retrieve that already-known value.
        // Other IStoreItem implementations retain the stream-based fallback below
        // because their length may only be available from the opened stream.
        if (isHeadRequest && entry is BaseStoreItem file)
        {
            response.SetStatus(DavStatusCode.Ok);
            response.ContentLength = file.FileSize;

            if (etag != null && request.Headers.IfNoneMatch == etag)
            {
                response.ContentLength = 0;
                response.SetStatus(DavStatusCode.NotModified);
            }

            return true;
        }

        // Stream the actual entry
        var stream = await TryGetSharedOrPrivateStreamAsync(
            entry, path, range, isHeadRequest, httpContext, ct).ConfigureAwait(false);
        if (stream is null)
            return true;

        await using (stream.ConfigureAwait(false))
        {
            if (stream != Stream.Null)
            {
                // Set the response
                response.SetStatus(DavStatusCode.Ok);

                // Set the expected content length
                try
                {
                    // We can only specify the Content-Length header if the
                    // length is known (this is typically true for seekable streams)
                    if (stream.CanSeek)
                    {
                        // Add a header that we accept ranges (bytes only)
                        response.Headers.AcceptRanges = "bytes";

                        // Determine the total length
                        var length = stream.Length;

                        // Check if a range was specified
                        if (range != null)
                        {
                            long start;
                            long end;
                            if (!range.Start.HasValue && range.End.HasValue)
                            {
                                var suffixLength = range.End.Value;
                                start = suffixLength > 0 ? Math.Max(0, length - suffixLength) : length;
                                end = length - 1;
                            }
                            else
                            {
                                start = range.Start ?? 0;
                                end = Math.Min(range.End ?? length - 1, length - 1);
                            }

                            // Return 416 if the range start is beyond the end of the file
                            if (start < 0 || start > end)
                            {
                                response.Headers.ContentRange = $"bytes */{stream.Length}";
                                response.SetStatus((DavStatusCode)416);
                                return true;
                            }

                            length = end - start + 1;
                            copyStart = start;
                            copyEnd = end;

                            // Write the range
                            response.Headers.ContentRange = $"bytes {start}-{end}/{stream.Length}";
                            response.SetStatus(DavStatusCode.PartialContent);
                        }

                        // Set the header, so the client knows how much data is required
                        response.ContentLength = length;
                    }
                }
                catch (NotSupportedException)
                {
                    // If the content length is not supported, then we just skip it
                }

                // Do not return the actual item data if ETag matches
                if (etag != null && request.Headers.IfNoneMatch == etag)
                {
                    response.ContentLength = 0;
                    response.SetStatus(DavStatusCode.NotModified);
                    return true;
                }

                // HEAD method doesn't require the actual item data
                if (!isHeadRequest)
                {
                    concurrentReadScope!.UpdateStart(copyStart);

                    // Cap segment prefetch at the range end (open-ended ranges leave budget null).
                    if (copyEnd.HasValue)
                        RangeContext.SetReadBudget(copyEnd.Value - copyStart + 1);
                    else
                        RangeContext.SetReadBudget(null);

                    var userAgent = request.Headers.UserAgent.ToString();
                    var clientIp = httpContext.Connection.RemoteIpAddress?.ToString();
                    var clientKey = $"{clientIp}|{userAgent}";
                    // DatabaseStoreIdFile.Name returns the GUID (it backs rclone symlink
                    // targets), so prefer FriendlyName when that's what we got.
                    var fileName = entry switch
                    {
                        NzbWebDAV.WebDav.DatabaseStoreIdFile idFile => idFile.FriendlyName,
                        _ => !string.IsNullOrEmpty(entry.Name) ? entry.Name : System.IO.Path.GetFileName(path)
                    };
                    var sessionId = _activeReadRegistry.GetOrCreate(
                        path, clientKey, fileName, stream.CanSeek ? stream.Length : null,
                        userAgent, clientIp);
                    var traceRange = _streamTrace.RangeOpen(
                        sessionId, path, request.Method, copyStart, copyEnd,
                        stream.CanSeek ? stream.Length : null, userAgent, clientIp);
                    using var scope = _providerUsageTracker.BeginScope(sessionId);
                    using var metricsScope = MultiProviderNntpClient.BeginReadSessionScope(sessionId);
                    using var traceRangeScope = MultiProviderNntpClient.BeginStreamTraceRangeScope(traceRange);
                    try
                    {
                        // Body transfer can run for minutes; drop the admission/open
                        // deadline and rely on per-segment mid-stream timeouts.
                        readCts.CancelAfter(Timeout.InfiniteTimeSpan);
                        await CopyToAsync(stream, response.Body, copyStart, copyEnd,
                            (n, pos) => _activeReadRegistry.Touch(sessionId, n, pos),
                            traceRange, readCts, path, ct).ConfigureAwait(false);
                        FinishRange(sessionId, traceRange, ReadSession.EndReasonCode.Completed);
                        ClearStreamingFailureAfterCompletedRead(
                            _failureTracker,
                            httpContext.Items["DavItem"],
                            isHeadRequest,
                            true,
                            copyStart,
                            copyEnd,
                            stream.CanSeek ? stream.Length : null);
                    }
                    catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
                    {
                        FinishRange(sessionId, traceRange, ReadSession.EndReasonCode.Aborted);
                        throw;
                    }
                    catch (StreamingWriteTimeoutException)
                    {
                        // Watchdog-fired write timeout: the client stopped reading but kept the
                        // connection open. Treat as a client abort so the response is a clean
                        // close, not a 500 with a stack trace.
                        FinishRange(sessionId, traceRange, ReadSession.EndReasonCode.Aborted);
                        throw;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        FinishRange(sessionId, traceRange, ReadSession.EndReasonCode.Error, ex.Message);
                        throw;
                    }
                }
            }
            else
            {
                // Set the response
                response.SetStatus(DavStatusCode.NoContent);
            }
        }
        return true;
    }

    /// <summary>
    /// GET-only shared-stream attach. HEAD never touches the registry. 416 is
    /// checked against FileSize before any attach or private open for eligible
    /// items; misses fall through to today's GetReadableStreamAsync path.
    /// Returns null when this method already wrote a 416 response.
    /// </summary>
    private async Task<Stream?> TryGetSharedOrPrivateStreamAsync(
        IStoreItem entry,
        string path,
        NWebDav.Server.Helpers.Range? range,
        bool isHeadRequest,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (!isHeadRequest &&
            entry is IDetachedStreamSource detachedSource &&
            entry is BaseStoreItem sizedItem)
        {
            var fileSize = sizedItem.FileSize;
            var (start, endOffset, unsatisfiable) = ResolveAttachRange(range, fileSize);
            if (unsatisfiable)
            {
                httpContext.Response.Headers.AcceptRanges = "bytes";
                httpContext.Response.Headers.ContentRange = $"bytes */{fileSize}";
                httpContext.Response.SetStatus((DavStatusCode)416);
                return null;
            }

            var attach = await _sharedStreams.TryAttachAsync(
                Uri.UnescapeDataString(path),
                start,
                endOffset,
                fileSize,
                detachedSource,
                async (offset, readerCt) =>
                {
                    var privateStream = await entry.GetReadableStreamAsync(readerCt).ConfigureAwait(false);
                    if (offset != 0)
                        privateStream.Seek(offset, SeekOrigin.Begin);
                    return privateStream;
                },
                ct).ConfigureAwait(false);

            if (attach is not null)
            {
                if (attach.DavItem is not null)
                    httpContext.Items["DavItem"] = attach.DavItem;
                return attach.Stream;
            }
        }

        return await entry.GetReadableStreamAsync(ct).ConfigureAwait(false);
    }

    internal static (long Start, long? EndOffset, bool Unsatisfiable) ResolveAttachRange(
        NWebDav.Server.Helpers.Range? range,
        long fileSize)
    {
        if (range is null)
            return (0, null, false);

        long start;
        long end;
        if (!range.Start.HasValue && range.End.HasValue)
        {
            var suffixLength = range.End.Value;
            start = suffixLength > 0 ? Math.Max(0, fileSize - suffixLength) : fileSize;
            end = fileSize - 1;
        }
        else
        {
            start = range.Start ?? 0;
            end = Math.Min(range.End ?? fileSize - 1, fileSize - 1);
        }

        return (start, end, start < 0 || start > end);
    }

    private static ConcurrentReadRegion ResolveReadRegion(NWebDav.Server.Helpers.Range? range)
    {
        if (range is null) return ConcurrentReadRegion.Full;
        if (!range.Start.HasValue) return ConcurrentReadRegion.SuffixRange;
        return range.Start.Value == 0
            ? ConcurrentReadRegion.StartRange
            : ConcurrentReadRegion.OffsetRange;
    }

    internal static bool ClearStreamingFailureAfterCompletedRead(
        StreamingFailureTracker failureTracker,
        object? requestDavItem,
        bool isHeadRequest,
        bool copySucceeded,
        long copyStart,
        long? copyEnd,
        long? streamLength)
    {
        if (!copySucceeded ||
            isHeadRequest ||
            copyStart != 0 ||
            requestDavItem is not DavItem davItem ||
            (copyEnd.HasValue && (streamLength is null || copyEnd.Value != streamLength.Value - 1)))
        {
            return false;
        }

        failureTracker.ClearFailure(davItem.Id);
        return true;
    }

    /// <summary>
    /// Resolve a single bytes Range for GET. Returns null for HEAD, missing,
    /// malformed, non-bytes, multi-range, or overflow so the caller serves full content.
    /// Suffix form <c>bytes=-N</c> maps to <c>Start=null</c>, <c>End=N</c>.
    /// </summary>
    internal static NWebDav.Server.Helpers.Range? TryResolveRange(bool isHeadRequest, string rangeHeader)
    {
        if (isHeadRequest)
            return null;

        if (!GetWebdavItemRequest.TryParseRangeHeader(
                rangeHeader, out var rStart, out var rEnd, out var rSuffix))
            return null;

        if (rStart is not null)
            return new NWebDav.Server.Helpers.Range { Start = rStart, End = rEnd };

        if (rSuffix is not null)
            return new NWebDav.Server.Helpers.Range { Start = null, End = rSuffix };

        return null;
    }

    private void FinishRange(
        Guid sessionId,
        StreamTraceRangeContext? traceRange,
        ReadSession.EndReasonCode reason,
        string? message = null)
    {
        _activeReadRegistry.SetEndReason(sessionId, reason);
        _streamTrace.RangeEnd(
            sessionId, traceRange, reason, _activeReadRegistry.GetBytesRead(sessionId), message);
    }

    private async Task CopyToAsync(
        Stream src,
        Stream dest,
        long start,
        long? end,
        Action<long, long>? onBytesServed,
        StreamTraceRangeContext? traceRange,
        CancellationTokenSource readCts,
        string filePath,
        CancellationToken cancellationToken)
    {
        // Skip to the first offset
        if (start > 0)
        {
            // We prefer seeking instead of draining data
            if (!src.CanSeek)
                throw new IOException("Cannot use range, because the source stream isn't seekable");

            src.Seek(start, SeekOrigin.Begin);
        }

        // Determine the number of bytes to read
        var bytesToRead = end - start + 1 ?? long.MaxValue;

        // Read in 64KB blocks without allocating a large array for every request.
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var position = start;
        try
        {
            // Copy, until we don't get any data anymore
            while (bytesToRead > 0)
            {
                // Read the requested bytes into memory
                var requestedBytes = (int)Math.Min(bytesToRead, buffer.Length);
                var bytesRead = await src.ReadAsync(
                    buffer.AsMemory(0, requestedBytes), cancellationToken).ConfigureAwait(false);

                // We're done, if we cannot read any data anymore
                if (bytesRead == 0)
                {
                    ThrowIfCopyEndedEarly(
                        bytesRemaining: bytesToRead,
                        rangeEnd: end,
                        rangeStart: start,
                        bytesDeliveredInRange: position - start,
                        filePath,
                        src);
                    return;
                }

                // Write the data to the destination stream. Bound the write so a client
                // that stopped reading but kept the connection open (HTTP/2 flow control,
                // tunnel, or proxy) cannot hold its in-flight article budget until restart.
                var writeStarted = Stopwatch.GetTimestamp();
                await WriteWithProgressTimeoutAsync(
                    dest, buffer.AsMemory(0, bytesRead), readCts, cancellationToken).ConfigureAwait(false);
                _streamTrace.AddStall(
                    traceRange, StreamStallKind.ClientWrite, Stopwatch.GetElapsedTime(writeStarted));

                // Report chunk size + new absolute file position so dashboards can
                // surface real playback location (not cumulative transferred bytes).
                position += bytesRead;
                onBytesServed?.Invoke(bytesRead, position);

                // Decrement the number of bytes left to read
                bytesToRead -= bytesRead;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Range GETs promise a finite Content-Length; a clean EOF before that many bytes
    /// are copied must fail explicitly so ExceptionMiddleware can abort instead of letting
    /// Kestrel log an unhandled Content-Length mismatch. Full GETs with a known stream
    /// length use the same rule; natural EOF at the declared length is allowed.
    /// </summary>
    internal static void ThrowIfCopyEndedEarly(
        long bytesRemaining,
        long? rangeEnd,
        long rangeStart,
        long bytesDeliveredInRange,
        string filePath,
        Stream src)
    {
        if (rangeEnd.HasValue)
        {
            if (bytesRemaining > 0)
            {
                throw new IncompleteFileContentException(
                    filePath,
                    rangeEnd.Value - rangeStart + 1,
                    bytesDeliveredInRange);
            }

            return;
        }

        if (src.CanSeek && bytesDeliveredInRange < src.Length - rangeStart)
        {
            throw new IncompleteFileContentException(
                filePath,
                src.Length - rangeStart,
                bytesDeliveredInRange);
        }
    }

    private ValueTask WriteWithProgressTimeoutAsync(
        Stream dest,
        Memory<byte> chunk,
        CancellationTokenSource readCts,
        CancellationToken cancellationToken) =>
        WriteWithProgressTimeoutAsync(
            dest, chunk, _configManager.GetStreamingWriteTimeout(), readCts, cancellationToken);

    /// <summary>
    /// Writes one chunk to the client, enforcing a per-write progress deadline. A healthy
    /// client completes a 64 KB write in milliseconds; a write that has not completed within
    /// the configured window means the client stopped reading but kept the connection open,
    /// which would otherwise pin the in-flight article budget until the container restarts.
    /// On timeout the linked read token is cancelled so the whole pipeline unwinds and the
    /// stream's leases are released.
    /// </summary>
    internal static async ValueTask WriteWithProgressTimeoutAsync(
        Stream dest,
        Memory<byte> chunk,
        TimeSpan timeout,
        CancellationTokenSource readCts,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            await dest.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await dest.WriteAsync(chunk, cancellationToken).AsTask()
                .WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Cancel the linked token so the producer stops prefetching and the stream is
            // disposed, releasing its in-flight article budget. Surface as a
            // StreamingWriteTimeoutException (an OperationCanceledException) so the request
            // unwinds through the client-abort path rather than a 500 with a stack trace.
            await readCts.CancelAsync().ConfigureAwait(false);
            throw new StreamingWriteTimeoutException(
                "Client stopped reading; streaming write timed out.");
        }
    }
}
