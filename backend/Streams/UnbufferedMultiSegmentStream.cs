using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services.StreamTrace;
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
    private Stream? _stream;
    private int _currentIndex;
    private int _openSegmentIndex = -1;
    private long _openSegmentBytes;
    private int _consecutiveZeroFills;
    private bool _disposed;


    public UnbufferedMultiSegmentStream(
        Memory<string> segmentIds,
        INntpClient usenetClient,
        long estimatedSegmentSize,
        string? fileName = null,
        string[][]? segmentFallbacks = null,
        ReadOnlyMemory<long> exactSegmentSizes = default)
    {
        _segmentIds = segmentIds;
        _segmentFallbacks = segmentFallbacks;
        _usenetClient = usenetClient;
        _segmentSizes = new SegmentSizes(exactSegmentSizes, segmentIds.Length);
        _fileName = string.IsNullOrEmpty(fileName) ? "unknown" : fileName;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
                    var body = await _usenetClient.DecodedBodyAsync(segmentId, cancellationToken);
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
                        // Only an exactly-known length may stand in for missing data:
                        // anything else shifts every following byte of the file.
                        if (!_segmentSizes.TryGetFillLength(segmentIndex, out var fill, out _))
                            throw CreateUnknownLengthFailure(segmentIndex, e);

                        _consecutiveZeroFills++;
                        ZeroFillLogLimiter.Write(
                            "Article {SegmentId} missing on all providers while reading {FileName}. Zero-filling {Bytes} bytes to keep playback alive.",
                            e.SegmentId,
                            _fileName,
                            fill,
                            e);
                        if (MultiProviderNntpClient.CurrentReadSessionId is { } sessionId)
                            StreamTrace.TryZeroFill(sessionId, e.SegmentId, fill);
                        if (_consecutiveZeroFills >= MaxConsecutiveZeroFills)
                            throw;

                        _stream = new MemoryStream(new byte[fill], writable: false);
                    }
                }
            }

            // read from the stream
            var read = await _stream.ReadAsync(buffer, cancellationToken);
            if (read > 0)
            {
                _openSegmentBytes += read;
                return read;
            }

            // if the stream ended, continue to the next stream.
            if (_openSegmentIndex >= 0)
                _segmentSizes.RecordObservedSize(_openSegmentIndex, _openSegmentBytes);
            _openSegmentIndex = -1;
            await _stream.DisposeAsync();
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
                return body.Stream!;
            }
            catch (UsenetArticleNotFoundException)
            {
                // Try the next alternate MessageId.
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
        base.Dispose();
    }
}
