using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Services.StreamTrace;
using Serilog;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

public class UnbufferedMultiSegmentStream : FastReadOnlyNonSeekableStream
{
    private const int MaxConsecutiveZeroFills = 3;

    private readonly Memory<string> _segmentIds;
    private readonly string[][]? _segmentFallbacks;
    private readonly INntpClient _usenetClient;
    private readonly SegmentSizes _segmentSizes;
    private readonly string _fileName;
    private readonly bool _useContainerAwareFill;
    private readonly long? _firstSegmentFileOffset;
    private readonly bool _failFastOnFirstSegment;
    private Stream? _stream;
    private int _currentIndex;
    private int _openSegmentIndex = -1;
    private long _openSegmentBytes;
    private long _pendingPadBytes;
    private int _consecutiveZeroFills;
    private bool _disposed;


    public UnbufferedMultiSegmentStream(
        Memory<string> segmentIds,
        INntpClient usenetClient,
        long estimatedSegmentSize,
        string? fileName = null,
        string[][]? segmentFallbacks = null,
        ReadOnlyMemory<long> exactSegmentSizes = default,
        bool useContainerAwareFill = false,
        long? firstSegmentFileOffset = null,
        bool failFastOnFirstSegment = false)
    {
        _segmentIds = segmentIds;
        _segmentFallbacks = segmentFallbacks;
        _usenetClient = usenetClient;
        _segmentSizes = new SegmentSizes(exactSegmentSizes, segmentIds.Length);
        _fileName = string.IsNullOrEmpty(fileName) ? "unknown" : fileName;
        _useContainerAwareFill = useContainerAwareFill;
        _firstSegmentFileOffset = firstSegmentFileOffset;
        _failFastOnFirstSegment = failFastOnFirstSegment;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_pendingPadBytes > 0)
            {
                var fill = (int)Math.Min(_pendingPadBytes, buffer.Length);
                buffer.Span[..fill].Clear();
                _pendingPadBytes -= fill;
                _openSegmentBytes += fill;
                if (_pendingPadBytes == 0)
                    await FinishOpenSegmentAsync().ConfigureAwait(false);
                return fill;
            }

            // if the stream is null, get the next stream.
            if (_stream == null)
            {
                if (_currentIndex >= _segmentIds.Length) return 0;
                var segmentIndex = _currentIndex;
                var segmentId = _segmentIds.Span[_currentIndex++];
                _openSegmentIndex = -1;
                _openSegmentBytes = 0;
                try
                {
                    var body = await _usenetClient.DecodedBodyAsync(segmentId, cancellationToken).ConfigureAwait(false);
                    await SegmentResponseValidator
                        .ThrowOnSegmentIdMismatchAsync(segmentId, body)
                        .ConfigureAwait(false);
                    _stream = body.Stream!;
                    _openSegmentIndex = segmentIndex;
                    _consecutiveZeroFills = 0;
                }
                catch (UsenetArticleNotFoundException e)
                {
                    var fallback = await TryFallbackSegmentsAsync(segmentIndex, cancellationToken)
                        .ConfigureAwait(false);
                    if (fallback is not null)
                    {
                        _stream = fallback;
                        _openSegmentIndex = segmentIndex;
                        _consecutiveZeroFills = 0;
                    }
                    else
                    {
                        if (_failFastOnFirstSegment && segmentIndex == 0)
                            throw;
                        // Only an exactly-known length may stand in for missing data:
                        // anything else shifts every following byte of the file.
                        if (!_segmentSizes.TryGetFillLength(segmentIndex, out var fill, out _))
                            throw CreateUnknownLengthFailure(segmentIndex, e);

                        _consecutiveZeroFills++;
                        ZeroFillLogLimiter.Write(
                            "Article {SegmentId} missing on all providers while reading {FileName}. Filling the {Bytes}-byte gap to preserve later file offsets.",
                            e.SegmentId,
                            _fileName,
                            fill,
                            e);
                        if (MultiProviderNntpClient.CurrentReadSessionId is { } sessionId)
                            StreamTrace.TryZeroFill(sessionId, e.SegmentId, fill);
                        Par2RepairTriggerSink.Current?.ReportZeroFill(_fileName, e.SegmentId, segmentIndex, fill);
                        if (_consecutiveZeroFills >= MaxConsecutiveZeroFills)
                            throw;

                        _stream = CreateGapFillStream(fill, segmentIndex);
                    }
                }
            }

            // Cap the open segment at its recorded length so a too-long body cannot
            // push every following byte out of place.
            if (TryGetRemainingExactBytes(out var remainingExact) && remainingExact == 0)
            {
                await FinishOpenSegmentAsync().ConfigureAwait(false);
                continue;
            }

            var destination = remainingExact > 0 && remainingExact < buffer.Length
                ? buffer[..(int)remainingExact]
                : buffer;
            var read = await _stream.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                _openSegmentBytes += read;
                return read;
            }

            // Body ended early: pad to the recorded length so the next segment still
            // starts at the offset the rest of the file expects.
            if (TryGetRemainingExactBytes(out remainingExact) && remainingExact > 0)
            {
                ZeroFillLogLimiter.Write(
                    "Segment {SegmentId} of {FileName} decoded {Bytes} bytes short of its recorded size. " +
                    "Filling the gap to keep the rest of the file aligned.",
                    _segmentIds.Span[_openSegmentIndex],
                    _fileName,
                    remainingExact);
                if (MultiProviderNntpClient.CurrentReadSessionId is { } sessionId)
                    StreamTrace.TryZeroFill(sessionId, _segmentIds.Span[_openSegmentIndex], remainingExact);

                _pendingPadBytes = remainingExact;
                continue;
            }

            await FinishOpenSegmentAsync().ConfigureAwait(false);
        }
    }

    private Stream CreateGapFillStream(long fill, int segmentIndex)
    {
        if (!_useContainerAwareFill)
            return new ZeroStream(fill);

        long? fileOffset = _firstSegmentFileOffset;
        if (fileOffset is not null)
        {
            try
            {
                for (var i = 0; i < segmentIndex; i++)
                {
                    if (!_segmentSizes.TryGetExactSize(i, out var size))
                    {
                        fileOffset = null;
                        break;
                    }

                    fileOffset = checked(fileOffset.Value + size);
                }
            }
            catch (OverflowException)
            {
                fileOffset = null;
            }
        }

        return ContainerAwareFillStream.Create(_fileName, fill, fileOffset);
    }

    private bool TryGetRemainingExactBytes(out long remaining)
    {
        remaining = 0;
        if (_openSegmentIndex < 0) return false;
        if (!_segmentSizes.TryGetExactSize(_openSegmentIndex, out var exact)) return false;
        remaining = Math.Max(0, exact - _openSegmentBytes);
        return true;
    }

    private async Task FinishOpenSegmentAsync()
    {
        if (_openSegmentIndex >= 0
            && !_segmentSizes.TryGetExactSize(_openSegmentIndex, out _))
            _segmentSizes.RecordObservedSize(_openSegmentIndex, _openSegmentBytes);
        _openSegmentIndex = -1;
        _pendingPadBytes = 0;
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }
    }

    private Exception CreateUnknownLengthFailure(int segmentIndex, Exception failure)
    {
        var message =
            $"Segment {segmentIndex + 1} of {_segmentIds.Length} could not be downloaded while reading " +
            $"\"{_fileName}\", and its exact length is unknown, so the rest of the file cannot be " +
            "delivered at the right offsets. Repair the item to restore its segment sizes.";
        return failure.IsNonRetryableDownloadException()
            ? new NonRetryableDownloadException(message, failure)
            : new RetryableDownloadException(message, failure);
    }

    private async Task<Stream?> TryFallbackSegmentsAsync(
        int segmentIndex,
        CancellationToken cancellationToken)
    {
        if (_segmentFallbacks is null ||
            segmentIndex < 0 ||
            segmentIndex >= _segmentFallbacks.Length)
            return null;

        var fallbacks = _segmentFallbacks[segmentIndex] ?? [];
        foreach (var fallbackId in fallbacks)
        {
            try
            {
                var body = await _usenetClient
                    .DecodedBodyAsync(fallbackId, cancellationToken)
                    .ConfigureAwait(false);
                await SegmentResponseValidator
                    .ThrowOnSegmentIdMismatchAsync(fallbackId, body)
                    .ConfigureAwait(false);
                return body.Stream!;
            }
            catch (UsenetArticleNotFoundException)
            {
                // Try the next alternate MessageId.
            }
            catch (UsenetUnexpectedResponseException e)
            {
                Log.Debug(e, "Fallback MessageId {FallbackId} returned another article.", fallbackId);
            }
        }

        return null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (!disposing) return;
        _disposed = true;
        _stream?.Dispose();
        base.Dispose(disposing);
    }
}
