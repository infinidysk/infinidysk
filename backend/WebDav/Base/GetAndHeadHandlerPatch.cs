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
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services;
using NzbWebDAV.Services.StreamTrace;
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
    private readonly StreamTraceBuffer _streamTrace;
    private readonly StreamingFailureTracker _failureTracker;

    public GetAndHeadHandlerPatch(
        IStore store,
        ConfigManager configManager,
        ProviderUsageTracker providerUsageTracker,
        ActiveReadRegistry activeReadRegistry,
        StreamTraceBuffer streamTrace,
        StreamingFailureTracker failureTracker)
    {
        _store = store;
        _configManager = configManager;
        _providerUsageTracker = providerUsageTracker;
        _activeReadRegistry = activeReadRegistry;
        _streamTrace = streamTrace;
        _failureTracker = failureTracker;
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
        catch (OperationCanceledException oce) when (!httpContext.RequestAborted.IsCancellationRequested)
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

        // Stream the actual entry
        var stream = await entry.GetReadableStreamAsync(ct).ConfigureAwait(false);
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
                    // Cap segment prefetch at the range end (open-ended ranges leave budget null).
                    if (copyEnd.HasValue)
                        RangeContext.SetReadBudget(copyEnd.Value - copyStart + 1);
                    else
                        RangeContext.SetReadBudget(null);

                    var path = request.GetUri().AbsolutePath;
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
                    _streamTrace.RangeOpen(
                        sessionId, path, request.Method, copyStart, copyEnd,
                        stream.CanSeek ? stream.Length : null, userAgent, clientIp);
                    using var scope = _providerUsageTracker.BeginScope(sessionId);
                    using var metricsScope = MultiProviderNntpClient.BeginReadSessionScope(sessionId);
                    try
                    {
                        // Body transfer can run for minutes; drop the admission/open
                        // deadline and rely on per-segment mid-stream timeouts.
                        readCts.CancelAfter(Timeout.InfiniteTimeSpan);
                        await CopyToAsync(stream, response.Body, copyStart, copyEnd,
                            (n, pos) => _activeReadRegistry.Touch(sessionId, n, pos),
                            sessionId, ct).ConfigureAwait(false);
                        FinishRange(sessionId, ReadSession.EndReasonCode.Completed);
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
                        FinishRange(sessionId, ReadSession.EndReasonCode.Aborted);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        FinishRange(sessionId, ReadSession.EndReasonCode.Error, ex.Message);
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

    private void FinishRange(Guid sessionId, ReadSession.EndReasonCode reason, string? message = null)
    {
        _activeReadRegistry.SetEndReason(sessionId, reason);
        _streamTrace.RangeEnd(sessionId, reason, _activeReadRegistry.GetBytesRead(sessionId), message);
    }

    private async Task CopyToAsync(
        Stream src,
        Stream dest,
        long start,
        long? end,
        Action<long, long>? onBytesServed,
        Guid? sessionId,
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
                    return;

                // Write the data to the destination stream
                var writeStarted = Stopwatch.GetTimestamp();
                await dest.WriteAsync(
                    buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                if (sessionId is { } id)
                    _streamTrace.AddStall(
                        id, StreamStallKind.ClientWrite, Stopwatch.GetElapsedTime(writeStarted));

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
}
