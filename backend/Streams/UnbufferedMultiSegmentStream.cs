using System.Buffers;
using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Services.StreamTrace;
using Serilog;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

public class UnbufferedMultiSegmentStream : FastReadOnlyNonSeekableStream
{
    private const int MaxCorruptionRetries = 3;
    private const int MaxTransportRetries = 2;

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
    private bool _openSegmentHole;
    private bool _hasProbedByte;
    private byte _probedByte;
    private long _positionPrefixBytes;
    private long _positionPrefixRemaining;
    private bool _isPositioning;
    private long _openSegmentCallerBytes;
    private SegmentRecoveryState? _recoveryState;
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

    // Positioning is distinct from emission. If a candidate BODY is replaced
    // before this segment returns a caller-visible byte, the full prefix is
    // replayed against the replacement before any data escapes.
    internal async Task DiscardPrefixBytesAsync(long prefixBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(prefixBytes);
        if (prefixBytes == 0)
            return;

        _positionPrefixBytes = prefixBytes;
        _positionPrefixRemaining = prefixBytes;
        await EnsurePositionedAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsurePositionedAsync(CancellationToken cancellationToken)
    {
        if (_positionPrefixRemaining == 0)
            return;

        _isPositioning = true;
        var throwaway = ArrayPool<byte>.Shared.Rent(
            (int)Math.Min(_positionPrefixRemaining, 64 * 1024));
        try
        {
            while (_positionPrefixRemaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(_positionPrefixRemaining, throwaway.Length);
                var read = await ReadAsync(throwaway.AsMemory(0, toRead), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        $"Stream ended {_positionPrefixRemaining} bytes before " +
                        $"{_positionPrefixBytes} bytes could be skipped.");
                }

                _positionPrefixRemaining -= read;
            }
        }
        finally
        {
            _isPositioning = false;
            ArrayPool<byte>.Shared.Return(throwaway);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_isPositioning && _positionPrefixRemaining > 0)
            {
                await EnsurePositionedAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (_pendingPadBytes > 0)
            {
                var fill = (int)Math.Min(_pendingPadBytes, buffer.Length);
                buffer.Span[..fill].Clear();
                _pendingPadBytes -= fill;
                _openSegmentBytes += fill;
                if (_pendingPadBytes == 0)
                    await FinishOpenSegmentAsync().ConfigureAwait(false);
                return ReturnToCaller(fill);
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
                        return ReturnToCaller(written);
                }
            }

            // if the stream is null, get the next stream.
            if (_stream == null)
            {
                if (written > 0)
                    return ReturnToCaller(written);
                if (_currentIndex >= _segmentIds.Length) return 0;
                var segmentIndex = _currentIndex;
                var segmentId = _segmentIds.Span[_currentIndex++];
                BeginSegment(segmentIndex, segmentId);
                if (_knownMissingSegmentIndices?.Contains(segmentIndex) == true)
                {
                    await OpenKnownMissingSegmentAsync(segmentIndex, segmentId, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                ThrowIfPlaybackFailFast();

                Stream? fetched = null;
                try
                {
                    var body = await FetchBodyAsync(segmentId, cancellationToken).ConfigureAwait(false);
                    fetched = body.Stream;
                    await SegmentResponseValidator
                        .ThrowOnSegmentIdMismatchAsync(segmentId, body)
                        .ConfigureAwait(false);
                    _stream = fetched;
                    fetched = null;
                    _openSegmentFromLiveFetch = true;
                    _openSegmentHole = false;
                }
                catch (UsenetArticleNotFoundException e)
                {
                    await DisposeBodyStreamAsync(fetched).ConfigureAwait(false);
                    await HandleMissingAsync(segmentIndex, segmentId, e, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (UsenetCorruptArticleException e) when (!cancellationToken.IsCancellationRequested)
                {
                    await DisposeBodyStreamAsync(fetched).ConfigureAwait(false);
                    await HandleCorruptionAsync(segmentIndex, segmentId, e, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception e) when (IsRecoverableTransportFailure(e, cancellationToken))
                {
                    await DisposeBodyStreamAsync(fetched).ConfigureAwait(false);
                    await HandleTransportFailureAsync(segmentIndex, segmentId, e, cancellationToken)
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
                    return ReturnToCaller(written);
                try
                {
                    await ObserveLiveSegmentTrailerAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (UsenetCorruptArticleException e) when (!cancellationToken.IsCancellationRequested)
                {
                    await HandleCorruptionAsync(
                            _openSegmentIndex, _segmentIds.Span[_openSegmentIndex], e, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }
                catch (Exception e) when (
                    _openSegmentCallerBytes == 0 &&
                    IsRecoverableTransportFailure(e, cancellationToken))
                {
                    await HandleTransportFailureAsync(
                            _openSegmentIndex, _segmentIds.Span[_openSegmentIndex], e, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
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
                await HandleCorruptionAsync(
                        _openSegmentIndex, _segmentIds.Span[_openSegmentIndex], e, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }
            catch (Exception e) when (
                _openSegmentCallerBytes == 0 &&
                IsRecoverableTransportFailure(e, cancellationToken))
            {
                await HandleTransportFailureAsync(
                        _openSegmentIndex, _segmentIds.Span[_openSegmentIndex], e, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (read > 0)
            {
                _openSegmentBytes += read;
                return ReturnToCaller(written + read);
            }

            if (written > 0)
                return ReturnToCaller(written);

            // Body ended early: pad to the recorded length so the next segment still
            // starts at the offset the rest of the file expects.
            if (TryGetRemainingExactBytes(out remainingExact) && remainingExact > 0)
            {
                var shortId = _segmentIds.Span[_openSegmentIndex];
                var hole = SegmentHoleReporter.ReportShortDecode(
                    _fileName, shortId, _openSegmentIndex, remainingExact);
                _consecutiveZeroFills++;
                _openSegmentHole = true;
                var cap = _consecutiveZeroFills >= GapFillLimits.MaxConsecutiveZeroFills;
                var trackerFail = PlaybackHoleTracker.ShouldFailFast(_fileName, out var failFast);
                if (cap || trackerFail)
                    ExceptionDispatchInfo.Capture(failFast ?? hole).Throw();

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
        ThrowIfPlaybackFailFast();
        var local = await TryGetLocalSegmentAsync(segmentId, segmentIndex, cancellationToken)
            .ConfigureAwait(false)
            ?? await TryGetLocalFallbackAsync(segmentIndex, cancellationToken).ConfigureAwait(false);
        if (local is not null)
        {
            _stream = local;
            _openSegmentIndex = segmentIndex;
            _openSegmentFromLiveFetch = false;
            _openSegmentHole = false;
            return;
        }

        var missing = new UsenetArticleNotFoundException(segmentId);
        if (_failFastOnFirstSegment && segmentIndex == 0)
        {
            missing.LogWarningKnownOrStack(
                "First article {SegmentId} is health-confirmed missing at playback start while reading {FileName}. " +
                "Failing the stream so the player surfaces an error.",
                segmentId, _fileName);
            throw missing;
        }
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

    private void BeginSegment(int segmentIndex, string segmentId)
    {
        _openSegmentIndex = segmentIndex;
        _openSegmentBytes = 0;
        _openSegmentCallerBytes = 0;
        _pendingPadBytes = 0;
        _hasProbedByte = false;
        _recoveryState = new SegmentRecoveryState(
            segmentIndex,
            segmentId,
            GetCorruptionRetryLimit(segmentId));
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
        var completedIndex = _openSegmentIndex;
        if (_openSegmentIndex >= 0
            && !_segmentSizes.TryGetExactSize(_openSegmentIndex, out _))
            _segmentSizes.RecordObservedSize(_openSegmentIndex, _openSegmentBytes);
        if (!_openSegmentHole)
        {
            _consecutiveZeroFills = 0;
            PlaybackHoleTracker.RecordGoodSegment(_fileName);
        }

        _openSegmentIndex = -1;
        _pendingPadBytes = 0;
        _openSegmentFromLiveFetch = false;
        _openSegmentHole = false;
        _hasProbedByte = false;
        _openSegmentCallerBytes = 0;
        _recoveryState = null;
        if (completedIndex == 0)
        {
            _positionPrefixBytes = 0;
            _positionPrefixRemaining = 0;
        }
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

    private async Task HandleCorruptionAsync(
        int segmentIndex,
        string segmentId,
        UsenetCorruptArticleException exception,
        CancellationToken cancellationToken)
    {
        if (_openSegmentCallerBytes > 0)
        {
            await HandlePostEmissionCorruptionAsync(
                    segmentIndex, segmentId, exception, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var state = GetRecoveryState(segmentIndex, segmentId);
        await ResetCandidateAsync(segmentIndex).ConfigureAwait(false);
        Exception failure = exception;
        var persistent = false;
        try
        {
            state.PersistentCorruption.NoteOrThrow(exception);
        }
        catch (PersistentUsenetCorruptionException e)
        {
            failure = e;
            persistent = true;
        }

        while (!persistent && state.CorruptionAttempts < state.CorruptionRetryLimit)
        {
            var attempt = ++state.CorruptionAttempts;
            Log.Debug(
                failure,
                "Corrupt segment {SegmentId} from provider {Provider}; retrying to allow provider failover (attempt {Attempt}).",
                segmentId,
                exception.ProviderKey,
                attempt);
            if (MultiProviderNntpClient.CurrentReadSessionId is { } sessionId)
                StreamTrace.TryRetry(sessionId, segmentId, attempt, failure.Message);
            await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken)
                .ConfigureAwait(false);

            try
            {
                await FetchAndAcceptCandidateAsync(segmentId, segmentIndex, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (UsenetCorruptArticleException e)
            {
                failure = e;
                try
                {
                    state.PersistentCorruption.NoteOrThrow(e);
                }
                catch (PersistentUsenetCorruptionException persistentFailure)
                {
                    failure = persistentFailure;
                    persistent = true;
                }
            }
            catch (UsenetArticleNotFoundException e)
            {
                await HandleMissingAsync(segmentIndex, segmentId, e, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception e) when (IsRecoverableTransportFailure(e, cancellationToken))
            {
                await HandleTransportFailureAsync(
                        segmentIndex, segmentId, e, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
        }

        var fallback = await TryFallbackSegmentsAsync(segmentIndex, state, cancellationToken)
            .ConfigureAwait(false);
        if (fallback is not null)
        {
            _stream = fallback;
            _openSegmentFromLiveFetch = true;
            _openSegmentHole = false;
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

    private async Task HandleMissingAsync(
        int segmentIndex,
        string segmentId,
        UsenetArticleNotFoundException failure,
        CancellationToken cancellationToken)
    {
        var state = GetRecoveryState(segmentIndex, segmentId);
        await ResetCandidateAsync(segmentIndex).ConfigureAwait(false);
        var fallback = await TryFallbackSegmentsAsync(segmentIndex, state, cancellationToken)
            .ConfigureAwait(false);
        if (fallback is not null)
        {
            _stream = fallback;
            _openSegmentFromLiveFetch = true;
            _openSegmentHole = false;
            return;
        }

        if (_failFastOnFirstSegment && segmentIndex == 0)
            throw failure;
        if (!_segmentSizes.TryGetFillLength(segmentIndex, out var fill, out _))
            throw CreateUnknownLengthFailure(segmentIndex, failure);

        ApplyZeroFill(segmentIndex, failure.SegmentId, fill, failure, isCorruption: false);
    }

    private async Task HandleTransportFailureAsync(
        int segmentIndex,
        string segmentId,
        Exception initialFailure,
        CancellationToken cancellationToken)
    {
        var state = GetRecoveryState(segmentIndex, segmentId);
        await ResetCandidateAsync(segmentIndex).ConfigureAwait(false);
        var failure = initialFailure;

        while (state.TransportAttempts < MaxTransportRetries)
        {
            var attempt = ++state.TransportAttempts;
            Log.Debug(
                failure,
                "Segment {SegmentId} failed before emitting bytes; retrying BODY (attempt {Attempt}).",
                segmentId,
                attempt);
            if (MultiProviderNntpClient.CurrentReadSessionId is { } sessionId)
                StreamTrace.TryRetry(sessionId, segmentId, attempt, failure.Message);
            await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken)
                .ConfigureAwait(false);

            try
            {
                await FetchAndAcceptCandidateAsync(segmentId, segmentIndex, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (UsenetArticleNotFoundException e)
            {
                await HandleMissingAsync(segmentIndex, segmentId, e, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (UsenetCorruptArticleException e)
            {
                await HandleCorruptionAsync(segmentIndex, segmentId, e, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception e) when (IsRecoverableTransportFailure(e, cancellationToken))
            {
                failure = e;
            }
        }

        if (_failFastOnFirstSegment && segmentIndex == 0)
            ExceptionDispatchInfo.Capture(failure).Throw();

        throw new TransientSegmentExhaustionException(
            $"Segment {segmentIndex + 1} of {_segmentIds.Length} ({segmentId}) could not be downloaded " +
            $"while reading \"{_fileName}\" after all retry attempts were exhausted. " +
            "The client should retry this range request.",
            failure);
    }

    private async Task FetchAndAcceptCandidateAsync(
        string segmentId,
        int segmentIndex,
        CancellationToken cancellationToken)
    {
        Stream? candidate = null;
        try
        {
            var body = await FetchBodyAsync(segmentId, cancellationToken).ConfigureAwait(false);
            candidate = body.Stream;
            await SegmentResponseValidator
                .ThrowOnSegmentIdMismatchAsync(segmentId, body)
                .ConfigureAwait(false);
            await ProbeLiveStreamAsync(candidate!, cancellationToken).ConfigureAwait(false);
            AcceptLiveStream(candidate!, segmentIndex);
            candidate = null;
        }
        finally
        {
            await DisposeBodyStreamAsync(candidate).ConfigureAwait(false);
        }
    }

    private async Task ResetCandidateAsync(int segmentIndex)
    {
        await DisposeOpenBodyAsync().ConfigureAwait(false);
        MarkCandidateRestarted(segmentIndex);
    }

    private void MarkCandidateRestarted(int segmentIndex)
    {
        _hasProbedByte = false;
        _openSegmentBytes = 0;
        _pendingPadBytes = 0;
        if (segmentIndex == 0 && _positionPrefixBytes > 0 && _openSegmentCallerBytes == 0)
            _positionPrefixRemaining = _positionPrefixBytes;
    }

    private SegmentRecoveryState GetRecoveryState(int segmentIndex, string segmentId)
    {
        if (_recoveryState is null ||
            _recoveryState.SegmentIndex != segmentIndex ||
            !string.Equals(_recoveryState.SegmentId, segmentId, StringComparison.Ordinal))
        {
            _recoveryState = new SegmentRecoveryState(
                segmentIndex,
                segmentId,
                GetCorruptionRetryLimit(segmentId));
        }

        return _recoveryState;
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

        var persistent = new PersistentCorruptionTracker();
        persistent.NoteOrThrow(corrupt);
        try
        {
            var body = await FetchBodyAsync(segmentId, cancellationToken)
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
        catch (PersistentUsenetCorruptionException)
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
        catch (UsenetCorruptArticleException confirmation)
        {
            persistent.NoteOrThrow(confirmation);
            Par2RepairTriggerSink.ReportCorruption(_fileName, segmentId);
            ExceptionDispatchInfo.Capture(corrupt).Throw();
            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException && e is not PersistentUsenetCorruptionException)
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
        _openSegmentHole = false;
    }

    private void ApplyZeroFill(
        int segmentIndex,
        string segmentId,
        long fill,
        Exception cause,
        bool isCorruption)
    {
        _consecutiveZeroFills++;
        _openSegmentHole = true;
        PlaybackHoleTracker.RecordHole(_fileName, segmentId, cause);
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
        var cap = _consecutiveZeroFills >= GapFillLimits.MaxConsecutiveZeroFills;
        var trackerFail = PlaybackHoleTracker.ShouldFailFast(_fileName, out var failFast);
        if (cap || trackerFail)
        {
            ExceptionDispatchInfo.Capture(failFast ?? cause).Throw();
            throw cause;
        }

        _stream = CreateGapFillStream(fill, segmentIndex);
        _openSegmentIndex = segmentIndex;
        _openSegmentFromLiveFetch = false;
        _openSegmentBytes = 0;
        _hasProbedByte = false;
        MarkCandidateRestarted(segmentIndex);
    }

    private int ReturnToCaller(int count)
    {
        if (count > 0 && !_isPositioning)
            _openSegmentCallerBytes += count;
        return count;
    }

    private async Task<Stream?> TryFallbackSegmentsAsync(
        int segmentIndex,
        SegmentRecoveryState state,
        CancellationToken cancellationToken)
    {
        if (_segmentFallbacks is null ||
            segmentIndex < 0 ||
            segmentIndex >= _segmentFallbacks.Length)
            return null;

        var fallbacks = _segmentFallbacks[segmentIndex] ?? [];
        while (state.NextFallbackIndex < fallbacks.Length)
        {
            var fallbackId = fallbacks[state.NextFallbackIndex++];
            Stream? fallbackStream = null;
            try
            {
                var body = await FetchBodyAsync(fallbackId, cancellationToken)
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
                // A playback fail-fast raised by FetchBodyAsync must escape instead of
                // walking every fallback ID and recording an extra hole per attempt.
                if (PlaybackHoleTracker.ShouldFailFast(_fileName, out var failFast) && failFast is not null)
                    ExceptionDispatchInfo.Capture(failFast).Throw();
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

    private static bool IsRecoverableTransportFailure(
        Exception exception,
        CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        exception is not OutOfMemoryException
            and not EndOfStreamException
            and not UsenetArticleNotFoundException
            and not UsenetCorruptArticleException
            and not UsenetUnexpectedResponseException
            and not PersistentUsenetCorruptionException &&
        exception.IsTransientTransportException();

    private async Task<UsenetDecodedBodyResponse> FetchBodyAsync(
        string segmentId,
        CancellationToken cancellationToken)
    {
        ThrowIfPlaybackFailFast();
        using (FetchAttributionContext.Begin(_fileName))
        {
            return await _usenetClient.DecodedBodyAsync(segmentId, cancellationToken)
                .ConfigureAwait(false);
        }
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

    private void ThrowIfPlaybackFailFast()
    {
        if (!PlaybackHoleTracker.ShouldFailFast(_fileName, out var exception))
            return;
        ExceptionDispatchInfo.Capture(
            exception ?? new UsenetArticleNotFoundException(_segmentIds.Span[0])).Throw();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class SegmentRecoveryState(
        int segmentIndex,
        string segmentId,
        int corruptionRetryLimit)
    {
        internal int SegmentIndex { get; } = segmentIndex;
        internal string SegmentId { get; } = segmentId;
        internal int CorruptionRetryLimit { get; } = corruptionRetryLimit;
        internal PersistentCorruptionTracker PersistentCorruption { get; } = new();
        internal int CorruptionAttempts { get; set; }
        internal int TransportAttempts { get; set; }
        internal int NextFallbackIndex { get; set; }
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
