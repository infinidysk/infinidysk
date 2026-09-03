using System.Diagnostics;
using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services.Diagnostics;
using NzbWebDAV.Services.Observability;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Utils;
using Serilog;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

public class NzbFileStream(
    string[] fileSegmentIds,
    long fileSize,
    INntpClient usenetClient,
    int articleBufferSize,
    LongRange[]? segmentByteRanges = null,
    bool usePipelinedBodyRequests = true,
    string? fileName = null,
    string[][]? segmentFallbacks = null,
    InFlightArticleBudget? inFlightArticleBudget = null,
    bool useContainerAwareFill = false,
    int streamingBodyBatchWidth = 4,
    HashSet<string>? knownCorruptSegmentIds = null,
    IReadOnlySet<int>? knownMissingSegmentIndices = null,
    bool segmentByteRangesTrusted = true,
    long? readBudgetOverride = null,
    bool readStartWarmupEnabled = false
) : FastReadOnlyStream
{
    private const long MaximumForwardDrainBytes = 1024 * 1024;
    private const long MinimumPrewarmRangeBytes = 8L * 1024 * 1024;
    private const int MinimumPrewarmConnections = 2;
    // A range in this size class is commonly an initial probe or a scrub preview.
    // Avoid draining its target segment into a pooled buffer before returning bytes.
    private const long MaximumDirectRangeBytes = 1024 * 1024;
    private long _position;
    private long _pendingForwardDrain;
    private bool _disposed;
    private Stream? _innerStream;
    // Teardown of the inner stream a Seek replaced is started non-blocking (Seek is
    // synchronous), but the next ReadAsync must await it before opening a new inner
    // stream — otherwise rapid scrubbing overlaps generations and pins the article
    // budget (#840 scrub wedge).
    private Task? _pendingInnerDispose;
    private Stopwatch? _pendingSeekStopwatch;
    private string? _pendingSeekKind;
    private readonly LongRange[]? _segmentByteRanges = ValidateAndCloneSegmentByteRanges(
        segmentByteRanges,
        fileSegmentIds.Length,
        fileSize,
        fileName,
        segmentByteRangesTrusted);
    private readonly HashSet<int>? _knownMissingSegmentIndices =
        knownMissingSegmentIndices is { Count: > 0 }
            ? new HashSet<int>(knownMissingSegmentIndices.Where(index => (uint)index < (uint)fileSegmentIds.Length))
            : null;

    private long[]? _exactSegmentSizes;

    // Average yEnc-decoded size per segment in this file, used to guess which segment
    // covers a byte offset (seek probes and capacity hints). It is only ever an
    // approximation — the tail segment is shorter, so the average is off by a few bytes
    // for every segment — and must never decide how many bytes the stream emits.
    private long EstimatedSegmentSize =>
        fileSegmentIds.Length > 0 ? Math.Max(1, fileSize / fileSegmentIds.Length) : 0;

    /// <summary>
    /// Exact decoded size of each segment, when the import recorded per-segment byte
    /// ranges. This is what lets a failed segment be replaced by the right number of
    /// bytes instead of an approximation that shifts the rest of the file.
    /// </summary>
    private long[]? ExactSegmentSizes
    {
        get
        {
            if (_segmentByteRanges is null) return null;
            return _exactSegmentSizes ??= _segmentByteRanges
                .Select(range => range.Count)
                .ToArray();
        }
    }

    public override bool CanSeek => true;
    public override long Length => fileSize;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush()
    {
        _innerStream?.Flush();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return 0;
        if (_position >= fileSize) return 0;
        // A prior Seek started the old inner stream's teardown non-blocking; join it
        // here so its article-budget leases release before a new stream leases again.
        if (_pendingInnerDispose is { } pendingDispose)
        {
            _pendingInnerDispose = null;
            try { await pendingDispose.ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Teardown-only; producer failures surface on ReadAsync.
            }
        }
        _innerStream ??= await GetFileStream(_position, cancellationToken).ConfigureAwait(false);
        if (_pendingForwardDrain > 0)
        {
            try
            {
                // Exact: a partial skip would leave the stream short of the position the
                // caller seeked to, and every byte it then read would be misattributed.
                await _innerStream.DiscardExactBytesAsync(
                    _pendingForwardDrain, cancellationToken).ConfigureAwait(false);
                _pendingForwardDrain = 0;
            }
            catch
            {
                await _innerStream.DisposeAsync().ConfigureAwait(false);
                _innerStream = null;
                _pendingForwardDrain = 0;
                throw;
            }
        }

        if (_pendingSeekStopwatch is { } seekStopwatch && _pendingSeekKind is { } seekKind)
        {
            PrometheusMetrics.Current?.RecordSeek(seekKind, seekStopwatch.Elapsed);
            _pendingSeekStopwatch = null;
            _pendingSeekKind = null;
        }

        var read = await _innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long absoluteOffset;
        try
        {
            absoluteOffset = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(fileSize + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Invalid seek origin.")
            };
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Seek position is outside stream bounds.");
        }

        if (absoluteOffset < 0 || absoluteOffset > fileSize)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Seek position is outside stream bounds.");

        if (_position == absoluteOffset)
        {
            PrometheusMetrics.Current?.RecordSeek("noop", TimeSpan.Zero);
            return _position;
        }
        if (_innerStream is not null &&
            absoluteOffset > _position &&
            absoluteOffset - _position <= MaximumForwardDrainBytes)
        {
            BeginSeekMeasurement("warm");
            _pendingForwardDrain += absoluteOffset - _position;
            _position = absoluteOffset;
            if (MultiProviderNntpClient.CurrentReadSessionId is { } drainSession)
                StreamTrace.TrySeek(drainSession, _position);
            return _position;
        }

        _position = absoluteOffset;
        BeginSeekMeasurement(_innerStream is null ? "fresh" : "cold");
        if (_innerStream is { } replaced)
        {
            // Start the inner stream's async teardown without blocking (Seek is sync),
            // but retain the task so the next ReadAsync can join it before leasing again.
            _pendingInnerDispose = replaced.DisposeAsync().AsTask();
            _innerStream = null;
        }
        _pendingForwardDrain = 0;
        if (MultiProviderNntpClient.CurrentReadSessionId is { } seekSession)
            StreamTrace.TrySeek(seekSession, _position);
        return _position;
    }

    private void BeginSeekMeasurement(string kind)
    {
        _pendingSeekStopwatch = Stopwatch.StartNew();
        _pendingSeekKind = kind;
    }

    private async Task<InterpolationSearch.Result> SeekSegment(long byteOffset, CancellationToken ct)
    {
        if (_segmentByteRanges is not null)
        {
            return InterpolationSearch.Find(
                byteOffset,
                new LongRange(0, _segmentByteRanges.Length),
                new LongRange(0, fileSize),
                guess => _segmentByteRanges[guess]
            );
        }

        var avg = EstimatedSegmentSize;
        UsenetArticleNotFoundException? missingProbeArticle = null;
        try
        {
            return await InterpolationSearch.Find(
                byteOffset,
                new LongRange(0, fileSegmentIds.Length),
                new LongRange(0, fileSize),
                async (guess) =>
                {
                    try
                    {
                        var header = await usenetClient.GetYencHeadersAsync(fileSegmentIds[guess], ct).ConfigureAwait(false);
                        var range = new LongRange(header.PartOffset, header.PartOffset + header.PartSize);

                        // A lazy RAR part's logical length ends with its packed file
                        // data, while its final yEnc segment can also contain trailing
                        // archive structure. Keep the generic interpolation search
                        // strict, but trim this known final-probe overflow so valid
                        // tail seeks can still converge through the preceding segment.
                        if (guess == fileSegmentIds.Length - 1 &&
                            range.StartInclusive >= 0 &&
                            range.StartInclusive < fileSize &&
                            range.EndExclusive > fileSize)
                        {
                            range = new LongRange(range.StartInclusive, fileSize);
                        }

                        return range;
                    }
                    catch (UsenetArticleNotFoundException e)
                    {
                        // The probe segment itself is missing — fall back to a
                        // synthetic uniform-size range so interpolation can still
                        // converge. The actual body read of this segment (if it
                        // turns out to be the seek target) gets a same-length gap from
                        // MultiSegmentStream.
                        missingProbeArticle = e;
                        Log.Warning(
                            "Seek probe hit missing article {SegmentId} (segment index {Index}) while reading {FileName}. Using estimated range.",
                            e.SegmentId, guess, string.IsNullOrEmpty(fileName) ? "unknown" : fileName);
                        var start = guess * avg;
                        var end = Math.Min(fileSize, start + avg);
                        return new LongRange(start, end);
                    }
                    catch (OutOfMemoryException oom)
                    {
                        OomDiagnostics.LogHeapStateOnOom(oom, "seek probe");
                        throw;
                    }
                    catch (Exception e) when (articleBufferSize > 0 && !ct.IsCancellationRequested && e is not OutOfMemoryException)
                    {
                        e.LogWarningKnownOrStack(
                            "Seek probe transient failure on segment index {Index}. Using estimated range.", guess);
                        var start = guess * avg;
                        var end = Math.Min(fileSize, start + avg);
                        return new LongRange(start, end);
                    }
                },
                ct
            ).ConfigureAwait(false);
        }
        catch (SeekPositionNotFoundException e) when (missingProbeArticle is not null)
        {
            throw new SeekPositionNotFoundException(e.Message, missingProbeArticle);
        }
    }

    private static LongRange[]? ValidateAndCloneSegmentByteRanges(
        LongRange[]? ranges,
        int segmentCount,
        long expectedFileSize,
        string? fileName,
        bool trusted)
    {
        if (ranges is null)
            return null;

        if (!trusted)
        {
            Log.Debug(
                "Persisted segment byte ranges for {FileName} have no trusted geometry provenance; " +
                "falling back to NNTP header probes for seeking",
                fileName ?? "unknown");
            return null;
        }

        var valid = expectedFileSize >= 0 &&
                    ranges.Length == segmentCount &&
                    ranges.Length > 0 &&
                    ranges[0] is not null &&
                    ranges[^1] is not null &&
                    ranges[0].StartInclusive == 0 &&
                    ranges[^1].EndExclusive == expectedFileSize;

        for (var i = 0; valid && i < ranges.Length; i++)
        {
            var range = ranges[i];
            valid = range is not null &&
                    range.StartInclusive >= 0 &&
                    range.EndExclusive > range.StartInclusive &&
                    range.EndExclusive <= expectedFileSize &&
                    (i == 0 || ranges[i - 1].EndExclusive == range.StartInclusive);
        }

        if (!valid)
        {
            Log.Warning(
                "Discarding invalid segment byte ranges for {FileName} " +
                "(rangeCount={RangeCount}, segmentCount={SegmentCount}, fileSize={FileSize}); " +
                "falling back to NNTP header probes for seeking",
                fileName ?? "unknown", ranges.Length, segmentCount, expectedFileSize);
            return null;
        }

        return ranges
            .Select(range => new LongRange(range.StartInclusive, range.EndExclusive))
            .ToArray();
    }

    private void ThrowIfPlaybackFailFast()
    {
        if (!PlaybackHoleTracker.ShouldFailFast(fileName ?? "", out var exception))
            return;
        ExceptionDispatchInfo.Capture(
            exception ?? new UsenetArticleNotFoundException(fileSegmentIds[0])).Throw();
    }

    private async Task<Stream> GetFileStream(long rangeStart, CancellationToken cancellationToken)
    {
        ThrowIfPlaybackFailFast();
        var readBudget = readBudgetOverride ?? NzbWebDAV.WebDav.Requests.RangeContext.GetReadBudget();

        if (rangeStart == 0)
        {
            return GetMultiSegmentStream(
                firstSegmentIndex: 0,
                failFastOnFirstSegment: true,
                readBudget,
                cancellationToken,
                rangeStart);
        }

        if (CanUseExactIndexedDirectHead())
        {
            StreamStartupTrace.TryRecord(StreamStartupPhase.ExactIndexDirect);
            var exact = await SeekSegment(rangeStart, cancellationToken).ConfigureAwait(false);
            return await GetExactIndexedStreamAsync(
                    exact, rangeStart, readBudget, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!ShouldUseLegacyUnbufferedRangePath(readBudget))
        {
            var buffered = await TryGetSeekStreamFast(rangeStart, cancellationToken, readBudget)
                .ConfigureAwait(false);
            if (buffered is not null)
            {
                StreamStartupTrace.TryRecord(StreamStartupPhase.LegacyBuffered);
                return buffered;
            }
        }

        StreamStartupTrace.TryRecord(StreamStartupPhase.LegacyProbedUnbuffered);
        var probed = await SeekSegment(rangeStart, cancellationToken).ConfigureAwait(false);
        return await GetLegacyProbedStreamAsync(probed, rangeStart, readBudget, cancellationToken)
            .ConfigureAwait(false);
    }

    private bool CanUseExactIndexedDirectHead() => _segmentByteRanges is not null;

    private static bool ShouldUseLegacyUnbufferedRangePath(long? readBudget) =>
        readBudget is > 0 and <= MaximumDirectRangeBytes;

    private Task<Stream> GetExactIndexedStreamAsync(
        InterpolationSearch.Result foundSegment,
        long rangeStart,
        long? readBudget,
        CancellationToken cancellationToken)
    {
        var prefixBytes = checked(rangeStart - foundSegment.FoundByteRange.StartInclusive);
        if (prefixBytes < 0 || prefixBytes >= foundSegment.FoundByteRange.Count)
        {
            throw new InvalidOperationException(
                $"Exact-index mapping produced prefix {prefixBytes} for range start {rangeStart} " +
                $"in segment {foundSegment.FoundIndex}.");
        }

        return GetPositionedMultiSegmentStreamAsync(
            foundSegment.FoundIndex,
            failFastOnFirstSegment: false,
            readBudget,
            prefixBytes,
            rangeStart,
            cancellationToken);
    }

    private Task<Stream> GetLegacyProbedStreamAsync(
        InterpolationSearch.Result foundSegment,
        long rangeStart,
        long? readBudget,
        CancellationToken cancellationToken)
    {
        var prefixBytes = rangeStart - foundSegment.FoundByteRange.StartInclusive;
        return GetPositionedMultiSegmentStreamAsync(
            foundSegment.FoundIndex,
            failFastOnFirstSegment: false,
            readBudget,
            prefixBytes,
            rangeStart,
            cancellationToken);
    }

    private async Task<Stream> GetPositionedMultiSegmentStreamAsync(
        int firstSegmentIndex,
        bool failFastOnFirstSegment,
        long? readBudget,
        long prefixBytes,
        long rangeStart,
        CancellationToken cancellationToken)
    {
        var initialBatchPlan = TryCreateInitialBatchPlan(
            firstSegmentIndex, rangeStart, readBudget, cancellationToken, out var finitePlan);
        var sliced = finitePlan is { } plan
            ? SliceFrom(firstSegmentIndex, plan.SegmentCount)
            : SliceFrom(firstSegmentIndex);
        StartConnectionPrewarm(
            sliced.SegmentIds.Length - 1,
            rangeStart,
            readBudget,
            cancellationToken);
        try
        {
            return await MultiSegmentStream.CreatePositionedFirstSegmentHybridAsync(
                    new MultiSegmentStream.FirstSegmentHybridOptions(
                        sliced.SegmentIds,
                        usenetClient,
                        articleBufferSize,
                        EstimatedSegmentSize,
                        failFastOnFirstSegment,
                        usePipelinedBodyRequests,
                        fileName,
                        readBudget,
                        sliced.Fallbacks,
                        sliced.ExactSizes,
                        inFlightArticleBudget,
                        useContainerAwareFill,
                        sliced.FirstSegmentFileOffset,
                        streamingBodyBatchWidth,
                        knownCorruptSegmentIds,
                        sliced.KnownMissing,
                        cancellationToken)
                    {
                        InitialBatchPlan = initialBatchPlan,
                    },
                    prefixBytes)
                .ConfigureAwait(false);
        }
        catch (EndOfStreamException e)
        {
            throw new SeekPositionNotFoundException(
                $"Byte position {rangeStart} of \"{fileName ?? "unknown"}\" is past the data " +
                $"available in segment {firstSegmentIndex + 1}. {e.Message}",
                e);
        }
    }

    private const int MaxSeekGuessCorrection = 3;

    internal async Task<Stream?> TryGetSeekStreamFast(
        long rangeStart,
        CancellationToken ct,
        long? readBudget = null)
    {
        var avg = EstimatedSegmentSize;
        if (avg <= 0 || fileSegmentIds.Length == 0) return null;

        var index = (int)Math.Clamp(rangeStart / avg, 0, fileSegmentIds.Length - 1);

        for (var step = 0; step <= MaxSeekGuessCorrection; step++)
        {
            var estimate = EstimateSeekTailBytes(rangeStart, index, avg);
            ArticleByteLease? seekLease = null;
            try
            {
                seekLease = await LeaseSeekTailAsync(estimate, ct).ConfigureAwait(false);

                UsenetDecodedBodyResponse response;
                try
                {
                    response = await usenetClient.DecodedBodyAsync(fileSegmentIds[index], ct).ConfigureAwait(false);
                }
                catch (OutOfMemoryException oom)
                {
                    OomDiagnostics.LogHeapStateOnOom(oom, "fast-seek body fetch");
                    throw;
                }
                catch
                {
                    return null;
                }

                var body = response.Stream!;
                UsenetYencHeader? header;
                try
                {
                    header = await body.GetYencHeadersAsync(ct).ConfigureAwait(false);
                }
                catch (OutOfMemoryException oom)
                {
                    OomDiagnostics.LogHeapStateOnOom(oom, "fast-seek header read");
                    throw;
                }
                catch
                {
                    await body.DisposeAsync().ConfigureAwait(false);
                    return null;
                }

                if (header == null)
                {
                    await body.DisposeAsync().ConfigureAwait(false);
                    return null;
                }

                var start = header.PartOffset;
                var end = header.PartOffset + header.PartSize;

                if (rangeStart < start || rangeStart >= end)
                {
                    await body.DisposeAsync().ConfigureAwait(false);
                    var next = rangeStart < start ? index - 1 : index + 1;
                    if (next < 0 || next >= fileSegmentIds.Length) return null;
                    index = next;
                    continue;
                }

                PooledBufferStream? head = null;
                var bodyDisposeAttempted = false;
                try
                {
                    try
                    {
                        await body.DiscardExactBytesAsync(rangeStart - start, ct).ConfigureAwait(false);
                        var tail = end - rangeStart;
                        var capacity = tail is > 0 and <= int.MaxValue ? (int)tail : 0;
#pragma warning disable CA2000 // head is disposed in the outer finally on all non-transferred paths; on success ownership moves to the returned CombinedStream
                        head = new PooledBufferStream(capacity);
#pragma warning restore CA2000
                        await body.CopyToAsync(head, ct).ConfigureAwait(false);
                        head.Position = 0;
                        // Do not relinquish the pooled head until body disposal succeeds.
                        // Otherwise a disposal exception aborts the return with no owner
                        // left to return the rented array.
                        bodyDisposeAttempted = true;
                        await body.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (OutOfMemoryException oom)
                    {
                        OomDiagnostics.LogHeapStateOnOom(oom, "fast-seek body drain");
                        throw;
                    }
                    catch (Exception e) when (!ct.IsCancellationRequested && e is not OutOfMemoryException)
                    {
                        // The guess was right (headers matched) but the body read failed,
                        // e.g. a mid-stream NNTP read timeout. Fall back to the slow seek
                        // path, whose MultiSegmentStream applies the normal retry and
                        // failure policy for the segment.
                        var displayName = string.IsNullOrEmpty(fileName) ? "unknown" : fileName;
                        if (e.TryGetKnownErrorMessage(out var reason))
                        {
                            ThrottledSegmentWarning.Write(
                                displayName,
                                "Fast seek failed mid-segment while reading {FileName}. Falling back to segment-index seek. Reason: {Reason}",
                                displayName,
                                reason);
                            Log.Debug(e, "Fast seek known failure stack while reading {FileName}", displayName);
                        }
                        else
                        {
                            Log.Warning(
                                e,
                                "Fast seek failed mid-segment while reading {FileName}. Falling back to segment-index seek.",
                                displayName);
                        }

                        return null;
                    }

                    var actual = head.Length;
                    if (actual != estimate)
                        seekLease.Adjust(actual - estimate);

                    // OnDispose returns the rented head if CombinedStream is disposed before
                    // its first read (head never becomes current). Idempotent dispose is safe
                    // when CombinedStream also disposes head after consuming it.
#pragma warning disable CA2000 // ownership transfers to CombinedStream / OnDispose
                    Stream owned = ReferenceEquals(seekLease, ArticleByteLease.Empty)
                        ? head
                        : new BudgetedStream(head, seekLease);
#pragma warning restore CA2000
                    seekLease = null;
                    var spliced = new CombinedStream(SpliceHeadThenRest(owned, index + 1, readBudget, ct))
                        .OnDispose(() => owned.Dispose());
                    head = null;
                    return spliced;
                }
                finally
                {
                    try
                    {
                        if (!bodyDisposeAttempted)
                            await body.DisposeAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        if (head is not null)
                            await head.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                seekLease?.Dispose();
            }
        }

        return null;
    }

    private long EstimateSeekTailBytes(long rangeStart, int index, long avg)
    {
        if (_segmentByteRanges is { } ranges && (uint)index < (uint)ranges.Length)
        {
            var exactTail = ranges[index].EndExclusive - rangeStart;
            return exactTail > 0 ? exactTail : ranges[index].Count;
        }

        var segmentStart = (long)index * avg;
        var offset = rangeStart - segmentStart;
        if (offset < 0) offset = 0;
        var tail = avg - offset;
        // Downward guess correction yields offset >= avg, so tail would be 0 and
        // LeaseSeekTailAsync would return Empty — leaving the pooled head unbudgeted.
        // Over-reserve one average segment; Adjust releases the surplus after drain.
        return tail > 0 ? tail : avg;
    }

    private async ValueTask<ArticleByteLease> LeaseSeekTailAsync(long estimate, CancellationToken ct)
    {
        var budget = inFlightArticleBudget ?? InFlightArticleBudget.Current;
        if (budget is null || estimate <= 0)
            return ArticleByteLease.Empty;
        return await budget.LeaseAsync(estimate, ct).ConfigureAwait(false);
    }

    private IEnumerable<Task<Stream>> SpliceHeadThenRest(
        Stream head,
        int restFirstIndex,
        long? readBudget,
        CancellationToken ct)
    {
        yield return Task.FromResult(head);
        yield return Task.FromResult(
            GetMultiSegmentStream(restFirstIndex, failFastOnFirstSegment: false, readBudget, ct));
    }

    private readonly record struct SlicedSegments(
        Memory<string> SegmentIds,
        string[][]? Fallbacks,
        ReadOnlyMemory<long> ExactSizes,
        long? FirstSegmentFileOffset,
        HashSet<int>? KnownMissing);

    private InitialBodyBatchPlan? TryCreateInitialBatchPlan(
        int firstSegmentIndex,
        long rangeStart,
        long? readBudget,
        CancellationToken cancellationToken,
        out FiniteRangeSegmentPlan? finitePlan)
    {
        finitePlan = null;
        var scheduling = cancellationToken.GetContext<StreamingSchedulingContext>();
        if (scheduling is null)
            return null;

        if (!usePipelinedBodyRequests || articleBufferSize <= 0)
        {
            RecordBatchPlan(
                scheduling.Snapshot,
                eligible: false,
                FiniteRangePlanUnavailableReason.UnbufferedOrNonPipelined.ToString());
            return null;
        }

        if (readBudget is not > 0)
        {
            RecordBatchPlan(
                scheduling.Snapshot,
                eligible: false,
                FiniteRangePlanUnavailableReason.NoFiniteReadBudget.ToString());
            return null;
        }

        if (_segmentByteRanges is null)
        {
            RecordBatchPlan(
                scheduling.Snapshot,
                eligible: false,
                FiniteRangePlanUnavailableReason.MissingExactMetadata.ToString());
            return null;
        }

        if (!FiniteRangeSegmentPlan.TryCreate(
                _segmentByteRanges,
                firstSegmentIndex,
                rangeStart,
                readBudget.Value,
                fileSize,
                out var plan,
                out var unavailableReason))
        {
            RecordBatchPlan(
                scheduling.Snapshot,
                eligible: false,
                unavailableReason.ToString());
            return null;
        }

        finitePlan = plan;
        if (!plan.HasBufferedRemainder)
        {
            RecordBatchPlan(
                scheduling.Snapshot,
                eligible: true,
                InitialBodyBatchPlanReason.ExactFiniteRange.ToString(),
                plan);
            return null;
        }

        var reason = scheduling.Snapshot.Reason == StreamingCapacityReason.Ok
            ? InitialBodyBatchPlanReason.ExactFiniteRange
            : InitialBodyBatchPlanReason.DegradedNoHealthyPrimaryCapacity;
        var initialPlan = InitialBodyBatchPlan.Create(
            plan.RemainderSegmentCount,
            plan.ExactPlannedRemainderBytes,
            scheduling.Snapshot.EffectiveStreamConnectionTarget,
            streamingBodyBatchWidth,
            articleBufferSize,
            reason);
        RecordBatchPlan(scheduling.Snapshot, eligible: true, reason.ToString(), plan, initialPlan);
        return initialPlan;
    }

    private static void RecordBatchPlan(
        StreamingCapacitySnapshot snapshot,
        bool eligible,
        string reason,
        FiniteRangeSegmentPlan? finitePlan = null,
        InitialBodyBatchPlan? initialPlan = null)
    {
        if (MultiProviderNntpClient.CurrentReadSessionId is not { } sessionId)
            return;

        StreamTrace.TryBatchPlan(
            sessionId,
            eligible,
            reason,
            finitePlan?.RemainderSegmentCount,
            finitePlan?.ExactPlannedRemainderBytes,
            initialPlan?.InitialBatchWidth,
            initialPlan?.ConfiguredMaximumBatchWidth,
            snapshot.EffectiveStreamConnectionTarget,
            snapshot.ActiveReaderShareCount,
            snapshot.EffectivePrimaryTransferCapacity,
            initialPlan?.WideningNotBeforeDeliveredSegment);
    }

    private SlicedSegments SliceFrom(int firstSegmentIndex, int? segmentCount = null)
    {
        var availableCount = fileSegmentIds.Length - firstSegmentIndex;
        var count = segmentCount ?? availableCount;
        if (firstSegmentIndex < 0 || firstSegmentIndex > fileSegmentIds.Length ||
            count < 0 || count > availableCount)
            throw new ArgumentOutOfRangeException(nameof(firstSegmentIndex));

        var segmentIds = fileSegmentIds.AsMemory(firstSegmentIndex, count);
        string[][]? fallbacks = null;
        if (segmentFallbacks is { Length: > 0 } && firstSegmentIndex < segmentFallbacks.Length)
            fallbacks = segmentFallbacks.AsMemory(firstSegmentIndex,
                Math.Min(count, segmentFallbacks.Length - firstSegmentIndex)).ToArray();

        var exactSizes = ExactSegmentSizes is { } sizes
            ? sizes.AsMemory(firstSegmentIndex, count)
            : default;
        var firstSegmentFileOffset = _segmentByteRanges?[firstSegmentIndex].StartInclusive;
        var knownMissing = _knownMissingSegmentIndices?
            .Where(index => index >= firstSegmentIndex && index < firstSegmentIndex + count)
            .Select(index => index - firstSegmentIndex)
            .ToHashSet();
        var trackerIds = PlaybackHoleTracker.SnapshotMissingSegmentIds(fileName ?? "");
        if (trackerIds is { Count: > 0 })
        {
            knownMissing ??= [];
            for (var i = firstSegmentIndex; i < firstSegmentIndex + count; i++)
            {
                if (trackerIds.Contains(fileSegmentIds[i]))
                    knownMissing.Add(i - firstSegmentIndex);
            }
        }

        return new SlicedSegments(
            segmentIds, fallbacks, exactSizes, firstSegmentFileOffset, knownMissing);
    }

    private Stream GetMultiSegmentStream(
        int firstSegmentIndex,
        bool failFastOnFirstSegment,
        long? readBudget,
        CancellationToken cancellationToken,
        long rangeStart = 0)
    {
        var initialBatchPlan = TryCreateInitialBatchPlan(
            firstSegmentIndex, rangeStart, readBudget, cancellationToken, out var finitePlan);
        var sliced = finitePlan is { } plan
            ? SliceFrom(firstSegmentIndex, plan.SegmentCount)
            : SliceFrom(firstSegmentIndex);
        StartConnectionPrewarm(
            sliced.SegmentIds.Length - 1,
            rangeStart,
            readBudget,
            cancellationToken);
        return MultiSegmentStream.CreateFirstSegmentHybridWithInitialBatchPlan(
            sliced.SegmentIds,
            usenetClient,
            articleBufferSize,
            EstimatedSegmentSize,
            failFastOnFirstSegment,
            usePipelinedBodyRequests,
            cancellationToken,
            fileName,
            readBudget,
            sliced.Fallbacks,
            sliced.ExactSizes,
            inFlightArticleBudget,
            useContainerAwareFill,
            sliced.FirstSegmentFileOffset,
            streamingBodyBatchWidth,
            knownCorruptSegmentIds,
            sliced.KnownMissing,
            initialBatchPlan);
    }

    private void StartConnectionPrewarm(
        int remainingSegments,
        long rangeStart,
        long? readBudget,
        CancellationToken cancellationToken)
    {
        if (!readStartWarmupEnabled || articleBufferSize <= 0 || remainingSegments <= 0)
            return;
        var remainingFileBytes = Math.Max(0, fileSize - rangeStart);
        var plannedReadBytes = readBudget is { } budget
            ? Math.Min(Math.Max(0, budget), remainingFileBytes)
            : remainingFileBytes;
        if (plannedReadBytes < MinimumPrewarmRangeBytes)
            return;

        var width = usePipelinedBodyRequests ? Math.Max(1, streamingBodyBatchWidth) : 1;
        var plannedBatches = (remainingSegments + width - 1) / width;
        var targetConnections = Math.Min(plannedBatches, articleBufferSize);
        if (targetConnections < MinimumPrewarmConnections)
            return;

        _ = ObservePrewarmAsync(
            usenetClient.PrewarmConnectionsAsync(targetConnections, cancellationToken));
    }

    private static async Task ObservePrewarmAsync(Task prewarm)
    {
        try
        {
            await prewarm.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Read cancellation also cancels this best-effort hint.
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Debug(e, "Connection pre-warm hint failed; the read continues without it.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _innerStream?.Dispose();
                // The prior Seek's teardown is async and cannot be awaited here; observe
                // any fault so it is not left unobserved, matching the fire-and-forget
                // dispose it replaced.
                var pending = _pendingInnerDispose;
                if (pending is not null)
                {
                    _pendingInnerDispose = null;
                    pending.ContinueWith(
                        static t => { _ = t.Exception; },
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default);
                }
            }
            _disposed = true;
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_pendingInnerDispose is { } pending)
        {
            _pendingInnerDispose = null;
            try { await pending.ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Teardown-only.
            }
        }
        if (_innerStream != null) await _innerStream.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
