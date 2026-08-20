using System.Buffers;
using System.Runtime.ExceptionServices;
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
    private const int MaxCorruptionRetries = 3;

    private readonly Memory<string> _segmentIds;
    private readonly string[][]? _segmentFallbacks;
    private readonly INntpClient _usenetClient;
    private readonly SegmentSizes _segmentSizes;
    private readonly string _fileName;
    private readonly bool _useContainerAwareFill;
    private readonly long? _firstSegmentFileOffset;
    private readonly bool _failFastOnFirstSegment;
    private readonly HashSet<string>? _knownCorruptSegmentIds;
    private readonly IReadOnlySet<int>? _knownMissingSegmentIndices;
    private readonly byte[] _scratch = new byte[16];
    private Stream? _stream;
    private int _currentIndex;
    private int _openSegmentIndex = -1;
    private long _openSegmentBytes;
    private long _pendingPadBytes;
    private int _consecutiveZeroFills;
    private bool _openSegmentFromLiveFetch;
    private bool _hasProbedByte;
    private byte _probedByte;
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
        bool failFastOnFirstSegment = false,
        HashSet<string>? knownCorruptSegmentIds = null,
        IReadOnlySet<int>? knownMissingSegmentIndices = null)
    {
        _segmentIds = segmentIds;
        _segmentFallbacks = segmentFallbacks;
        _usenetClient = usenetClient;
        _segmentSizes = new SegmentSizes(exactSegmentSizes, segmentIds.Length);
        _fileName = string.IsNullOrEmpty(fileName) ? "unknown" : fileName;
        _useContainerAwareFill = useContainerAwareFill;
        _firstSegmentFileOffset = firstSegmentFileOffset;
        _failFastOnFirstSegment = failFastOnFirstSegment;
        _knownCorruptSegmentIds = knownCorruptSegmentIds;
        _knownMissingSegmentIndices = knownMissingSegmentIndices;
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

            var written = 0;
            if (_hasProbedByte)
            {
                if (TryGetRemainingExactBytes(out var remainingForProbe) && remainingForProbe == 0)
                {
                    _hasProbedByte = false;
                }
                else
                {
                    buffer.Span[0] = _probedByte;
                    _hasProbedByte = false;
                    _openSegmentBytes += 1;
                    written = 1;
                    if (written == buffer.Length)
                        return written;
                }
            }

            // if the stream is null, get the next stream.
            if (_stream == null)
            {
                if (written > 0)
                    return written;
                if (_currentIndex >= _segmentIds.Length) return 0;
                var segmentIndex = _currentIndex;
                var segmentId = _segmentIds.Span[_currentIndex++];
                _openSegmentIndex = -1;
                _openSegmentBytes = 0;
                if (_knownMissingSegmentIndices?.Contains(segmentIndex) == true)
                {
                    await OpenKnownMissingSegmentAsync(segmentIndex, segmentId, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                Stream? fetched = null;
                try
                {
                    var body = await _usenetClient.DecodedBodyAsync(segmentId, cancellationToken).ConfigureAwait(false);
                    fetched = body.Stream;
                    await SegmentResponseValidator
                        .ThrowOnSegmentIdMismatchAsync(segmentId, body)
                        .ConfigureAwait(false);
                    _stream = fetched;
                    fetched = null;
                    _openSegmentIndex = segmentIndex;
                    _openSegmentFromLiveFetch = true;
                    _consecutiveZeroFills = 0;
                }
                catch (UsenetArticleNotFoundException e)
                {
                    await DisposeBodyStreamAsync(fetched).ConfigureAwait(false);
                    var fallback = await TryFallbackSegmentsAsync(segmentIndex, cancellationToken)
                        .ConfigureAwait(false);
                    if (fallback is not null)
                    {
                        _stream = fallback;
                        _openSegmentIndex = segmentIndex;
                        _openSegmentFromLiveFetch = true;
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

                        ApplyZeroFill(segmentIndex, e.SegmentId, fill, e, isCorruption: false);
                    }
                }
                catch (UsenetCorruptArticleException e) when (
                    !cancellationToken.IsCancellationRequested
                    && _openSegmentBytes == 0
                    && _pendingPadBytes == 0)
                {
                    await DisposeBodyStreamAsync(fetched).ConfigureAwait(false);
                    await HandlePreEmissionCorruptionAsync(segmentIndex, segmentId, e, cancellationToken)
                        .ConfigureAwait(false);
                }

                // Re-enter so a 1-byte retry/donor probe is served before the next body read.
                continue;
            }

            // Cap the open segment at its recorded length so a too-long body cannot
            // push every following byte out of place.
            if (TryGetRemainingExactBytes(out var remainingExact) && remainingExact == 0)
            {
                if (written > 0)
                    return written;
                try
                {
                    await ObserveLiveSegmentTrailerAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (UsenetCorruptArticleException e) when (!cancellationToken.IsCancellationRequested)
                {
                    await HandlePostEmissionCorruptionAsync(
                            _openSegmentIndex, _segmentIds.Span[_openSegmentIndex], e, cancellationToken)
                        .ConfigureAwait(false);
                }

                await FinishOpenSegmentAsync().ConfigureAwait(false);
                continue;
            }

            var available = buffer[written..];
            var destination = remainingExact > 0 && remainingExact < available.Length
                ? available[..(int)remainingExact]
                : available;
            int read;
            try
            {
                read = await _stream!.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
            }
            catch (UsenetCorruptArticleException e) when (!cancellationToken.IsCancellationRequested)
            {
                var openIndex = _openSegmentIndex;
                var openId = _segmentIds.Span[openIndex];
                if (_openSegmentBytes == 0 && _pendingPadBytes == 0)
                {
                    await HandlePreEmissionCorruptionAsync(openIndex, openId, e, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                await HandlePostEmissionCorruptionAsync(openIndex, openId, e, cancellationToken)
                    .ConfigureAwait(false);
                throw;
            }

            if (read > 0)
            {
                _openSegmentBytes += read;
                return written + read;
            }

            if (written > 0)
                return written;

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

    private async Task OpenKnownMissingSegmentAsync(
        int segmentIndex,
        string segmentId,
        CancellationToken cancellationToken)
    {
        var local = await TryGetLocalSegmentAsync(segmentId, segmentIndex, cancellationToken)
            .ConfigureAwait(false)
            ?? await TryGetLocalFallbackAsync(segmentIndex, cancellationToken).ConfigureAwait(false);
        if (local is not null)
        {
            _stream = local;
            _openSegmentIndex = segmentIndex;
            _openSegmentFromLiveFetch = false;
            _consecutiveZeroFills = 0;
            return;
        }

        var missing = new UsenetArticleNotFoundException(segmentId);
        if (!_segmentSizes.TryGetFillLength(segmentIndex, out var fill, out _))
            throw CreateUnknownLengthFailure(segmentIndex, missing);
        ApplyZeroFill(segmentIndex, segmentId, fill, missing, isCorruption: false);
    }

    private async Task<Stream?> TryGetLocalSegmentAsync(
        string segmentId,
        int segmentIndex,
        CancellationToken cancellationToken)
    {
        var body = await _usenetClient.TryGetLocalDecodedBodyAsync(segmentId, cancellationToken)
            .ConfigureAwait(false);
        if (body?.Stream is not { } stream) return null;

        try
        {
            await SegmentResponseValidator.ThrowOnSegmentIdMismatchAsync(segmentId, body).ConfigureAwait(false);
            if (!await SegmentResponseValidator.IsFallbackPartSizeCompatibleAsync(
                    stream, _segmentSizes, segmentIndex, cancellationToken).ConfigureAwait(false))
            {
                await DisposeBodyStreamAsync(stream).ConfigureAwait(false);
                return null;
            }

            return stream;
        }
        catch
        {
            await DisposeBodyStreamAsync(stream).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<Stream?> TryGetLocalFallbackAsync(int segmentIndex, CancellationToken cancellationToken)
    {
        if (_segmentFallbacks is null || segmentIndex >= _segmentFallbacks.Length) return null;

        foreach (var fallbackId in _segmentFallbacks[segmentIndex] ?? [])
        {
            var local = await TryGetLocalSegmentAsync(fallbackId, segmentIndex, cancellationToken)
                .ConfigureAwait(false);
            if (local is not null) return local;
        }

        return null;
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
        _openSegmentFromLiveFetch = false;
        _hasProbedByte = false;
        await DisposeOpenBodyAsync().ConfigureAwait(false);
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

    private async Task HandlePreEmissionCorruptionAsync(
        int segmentIndex,
        string segmentId,
        UsenetCorruptArticleException initialFailure,
        CancellationToken cancellationToken)
    {
        // Dispose first so the transport drains without parsing and fires its
        // completion callback exactly once before we issue another BODY.
        await DisposeOpenBodyAsync().ConfigureAwait(false);
        _hasProbedByte = false;

        var failure = initialFailure;
        for (var attempt = 1; attempt <= GetCorruptionRetryLimit(segmentId); attempt++)
        {
            Log.Debug(
                failure,
                "Corrupt segment {SegmentId} from provider {Provider}; retrying to allow provider failover (attempt {Attempt}).",
                segmentId,
                failure.ProviderKey,
                attempt);
            await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken)
                .ConfigureAwait(false);

            Stream? retryStream = null;
            try
            {
                var body = await _usenetClient.DecodedBodyAsync(segmentId, cancellationToken)
                    .ConfigureAwait(false);
                retryStream = body.Stream;
                await SegmentResponseValidator
                    .ThrowOnSegmentIdMismatchAsync(segmentId, body)
                    .ConfigureAwait(false);
                await ProbeLiveStreamAsync(retryStream!, cancellationToken).ConfigureAwait(false);
                AcceptLiveStream(retryStream!, segmentIndex);
                return;
            }
            catch (UsenetCorruptArticleException e)
            {
                await DisposeBodyStreamAsync(retryStream).ConfigureAwait(false);
                failure = e;
            }
        }

        var fallback = await TryFallbackSegmentsAsync(segmentIndex, cancellationToken)
            .ConfigureAwait(false);
        if (fallback is not null)
        {
            _stream = fallback;
            _openSegmentIndex = segmentIndex;
            _openSegmentFromLiveFetch = true;
            _consecutiveZeroFills = 0;
            return;
        }

        if (_failFastOnFirstSegment && segmentIndex == 0)
        {
            Par2RepairTriggerSink.ReportCorruption(_fileName, segmentId);
            failure.LogWarningKnownOrStack(
                "First article {SegmentId} persistently corrupt at playback start while reading {FileName}. " +
                "Failing the stream so the player surfaces an error.",
                segmentId, _fileName);
            ExceptionDispatchInfo.Capture(failure).Throw();
            throw failure;
        }

        if (!_segmentSizes.TryGetFillLength(segmentIndex, out var fill, out _))
        {
            Par2RepairTriggerSink.ReportCorruption(_fileName, segmentId);
            throw CreateUnknownLengthFailure(segmentIndex, failure);
        }

        ApplyZeroFill(segmentIndex, segmentId, fill, failure, isCorruption: true);
    }

    private int GetCorruptionRetryLimit(string segmentId) =>
        _knownCorruptSegmentIds is not null && _knownCorruptSegmentIds.Contains(segmentId)
            ? 0
            : MaxCorruptionRetries;

    private async Task HandlePostEmissionCorruptionAsync(
        int segmentIndex,
        string segmentId,
        UsenetCorruptArticleException corrupt,
        CancellationToken cancellationToken)
    {
        await DisposeOpenBodyAsync().ConfigureAwait(false);
        _hasProbedByte = false;

        try
        {
            var body = await _usenetClient.DecodedBodyAsync(segmentId, cancellationToken)
                .ConfigureAwait(false);
            await SegmentResponseValidator
                .ThrowOnSegmentIdMismatchAsync(segmentId, body)
                .ConfigureAwait(false);
            await DrainAndDisposeAsync(body.Stream!, cancellationToken).ConfigureAwait(false);

            Log.Debug(
                corrupt,
                "Segment {SegmentId} of {FileName} failed CRC after bytes were delivered, but a confirmation re-fetch was clean; corruption was transient/provider-specific.",
                segmentId,
                _fileName);
            throw new TransientSegmentExhaustionException(
                $"Segment {segmentIndex + 1} of {_segmentIds.Length} ({segmentId}) of \"{_fileName}\" failed CRC after bytes were delivered, but a confirmation re-fetch was clean; corruption was transient/provider-specific. The client should retry this range request.");
        }
        catch (TransientSegmentExhaustionException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch (UsenetCorruptArticleException)
        {
            Par2RepairTriggerSink.ReportCorruption(_fileName, segmentId);
            ExceptionDispatchInfo.Capture(corrupt).Throw();
            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Debug(
                e,
                "Confirmation re-fetch of corrupt segment {SegmentId} of {FileName} failed",
                segmentId,
                _fileName);
            Par2RepairTriggerSink.ReportCorruption(_fileName, segmentId);
            ExceptionDispatchInfo.Capture(corrupt).Throw();
            throw;
        }
    }

    private async Task ObserveLiveSegmentTrailerAsync(CancellationToken cancellationToken)
    {
        if (!_openSegmentFromLiveFetch || _stream is null)
            return;

        // One extra read against the in-memory pipe: 0 is clean EOF, a trailer CRC
        // throw is post-emission corruption on a fully emitted exact-size segment.
        _ = await _stream.ReadAsync(_scratch, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProbeLiveStreamAsync(Stream stream, CancellationToken cancellationToken)
    {
        var probed = await stream.ReadAsync(_scratch.AsMemory(0, 1), cancellationToken)
            .ConfigureAwait(false);
        if (probed > 0)
        {
            _hasProbedByte = true;
            _probedByte = _scratch[0];
        }
    }

    private void AcceptLiveStream(Stream stream, int segmentIndex)
    {
        _stream = stream;
        _openSegmentIndex = segmentIndex;
        _openSegmentFromLiveFetch = true;
        _consecutiveZeroFills = 0;
    }

    private void ApplyZeroFill(
        int segmentIndex,
        string segmentId,
        long fill,
        Exception cause,
        bool isCorruption)
    {
        _consecutiveZeroFills++;
        var template = isCorruption
            ? "Article {SegmentId} persistently corrupt while reading {FileName}. Filling the {Bytes}-byte gap to preserve later file offsets."
            : "Article {SegmentId} missing on all providers while reading {FileName}. Filling the {Bytes}-byte gap to preserve later file offsets.";
        ZeroFillLogLimiter.Write(template, segmentId, _fileName, fill, cause);
        if (MultiProviderNntpClient.CurrentReadSessionId is { } sessionId)
            StreamTrace.TryZeroFill(sessionId, segmentId, fill);
        if (isCorruption)
            Par2RepairTriggerSink.ReportCorruption(_fileName, segmentId);
        else
            Par2RepairTriggerSink.Current?.ReportZeroFill(_fileName, segmentId, segmentIndex, fill);
        if (_consecutiveZeroFills >= GapFillLimits.MaxConsecutiveZeroFills)
        {
            ExceptionDispatchInfo.Capture(cause).Throw();
            throw cause;
        }

        _stream = CreateGapFillStream(fill, segmentIndex);
        _openSegmentIndex = segmentIndex;
        _openSegmentFromLiveFetch = false;
        _openSegmentBytes = 0;
        _hasProbedByte = false;
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
            Stream? fallbackStream = null;
            try
            {
                var body = await _usenetClient
                    .DecodedBodyAsync(fallbackId, cancellationToken)
                    .ConfigureAwait(false);
                fallbackStream = body.Stream;
                await SegmentResponseValidator
                    .ThrowOnSegmentIdMismatchAsync(fallbackId, body)
                    .ConfigureAwait(false);
                if (!await SegmentResponseValidator.IsFallbackPartSizeCompatibleAsync(
                        fallbackStream!, _segmentSizes, segmentIndex, cancellationToken)
                        .ConfigureAwait(false))
                {
                    Log.Debug(
                        "Fallback MessageId {FallbackId} for segment {PrimaryIndex} of {FileName} has a mismatched yEnc part size; skipping.",
                        fallbackId, segmentIndex, _fileName);
                    await DisposeBodyStreamAsync(fallbackStream).ConfigureAwait(false);
                    fallbackStream = null;
                    continue;
                }

                await ProbeLiveStreamAsync(fallbackStream!, cancellationToken).ConfigureAwait(false);
                var accepted = fallbackStream;
                fallbackStream = null;
                return accepted;
            }
            catch (UsenetArticleNotFoundException)
            {
                await DisposeBodyStreamAsync(fallbackStream).ConfigureAwait(false);
            }
            catch (UsenetCorruptArticleException)
            {
                // Corrupt fallback — try the next alternate MessageId.
                await DisposeBodyStreamAsync(fallbackStream).ConfigureAwait(false);
            }
            catch (UsenetUnexpectedResponseException e)
            {
                await DisposeBodyStreamAsync(fallbackStream).ConfigureAwait(false);
                Log.Debug(e, "Fallback MessageId {FallbackId} returned another article.", fallbackId);
            }
        }

        return null;
    }

    private async Task DisposeOpenBodyAsync()
    {
        var stream = _stream;
        _stream = null;
        await DisposeBodyStreamAsync(stream).ConfigureAwait(false);
    }

    private static async Task DisposeBodyStreamAsync(Stream? stream)
    {
        if (stream is null) return;
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Debug(e, "Failed to dispose a BODY stream before re-fetch");
        }
    }

    private static async Task DrainAndDisposeAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, 8192), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) break;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            await DisposeBodyStreamAsync(stream).ConfigureAwait(false);
        }
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
