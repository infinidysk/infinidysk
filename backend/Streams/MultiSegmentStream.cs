using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services.Diagnostics;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Services.StreamTrace;
using Serilog;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

public class MultiSegmentStream : FastReadOnlyNonSeekableStream, ISegmentIssueProgress, IDeliveredBytesValidation
{
    private const int BodyPipelineBatchSize = 4;
    // Pipelining hides per-command RTT; at multi-MB articles transfer time dominates and
    // batched articles only wait behind each other, so these batches carry one article.
    internal const long PipelinedArticleSizeLimit = 2L * 1024 * 1024;
    private const int MinInitialPrefetchSegments = 8;
    private const int MaxBodyRetries = 2;
    private const int MaxCorruptionRetries = 3;
    internal const string InconclusiveGapFillTemplate =
        "Article {SegmentId} could not be confirmed missing while reading {FileName}. " +
        "Filling the {Bytes}-byte gap for this read only; the segment is not recorded as missing because not every enabled provider answered.";

    private readonly Memory<string> _segmentIds;
    private readonly string[][]? _segmentFallbacks;
    private readonly INntpClient _usenetClient;
    private readonly long _estimatedSegmentSize;
    private readonly SegmentSizes _segmentSizes;
    private readonly bool _failFastOnFirstSegment;
    private readonly bool _useContainerAwareFill;
    private readonly bool _recordedSizesInferred;
    private readonly long? _firstSegmentFileOffset;
    private readonly LongRange? _expectedFirstSegmentRange;
    private readonly bool _expectedFirstSegmentRangeWasClippedAtFileEnd;
    private readonly string _fileName;
    private readonly Channel<Task<SegmentDownloadResult>> _streamTasks;
    private readonly int _bodyPipelineBatchSize;
    private readonly AdaptiveBodyBatchSizer? _batchSizer;
    private readonly ContextualCancellationTokenSource _cts;
    private readonly long? _readBudget;
    private readonly long _prefetchByteCeiling;
    private readonly long _initialPrefetchByteCeiling;
    // Planned bytes of segments handed to the reader; grows the ceiling like TCP slow start.
    private long _consumedPrefetchBytes;
    private readonly int _taskWindowSize;
    private readonly int _stripeCount;
    // Producer-only: stripes the next group may use, between 1 and _stripeCount.
    private int _stripeTarget;
    private int _activeBatches;
    private readonly InFlightArticleBudget? _budget;
    private long _inFlightPrefetchBytes;
    // Producer-only enqueue progress, read by ShouldStopPrefetch.
    private int _segmentsEnqueued;
    private long _enqueuedBytes;
    private TaskCompletionSource _prefetchSpace =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Stream? _stream;
    private int _consecutiveZeroFills;
    private bool _incrementalReadinessPending;
    private int _deliveredSegments;
    private bool _disposed;
    private readonly Task _downloadTask;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    // Segment tasks whose channel write was cancelled are disposed out-of-band by
    // the producer; DisposeCoreAsync must join them so DisposeAsync does not return
    // while their BudgetedStream leases are still held (#840 scrub wedge).
    private readonly ConcurrentQueue<Task> _orphanedDisposals = new();
    private readonly ConcurrentQueue<Task> _batchCompletionObservers = new();
    private readonly HashSet<string>? _knownCorruptSegmentIds;
    private readonly IReadOnlySet<int>? _knownMissingSegmentIndices;

    // Head-of-line hedging (#893): one duplicate fetch when the next segment straggles.
    private static readonly TimeSpan HedgeFloor = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan HedgePollInterval = TimeSpan.FromMilliseconds(50);
    private const int HedgeLatencyMultiplier = 2;
    private const int ResponseLatencySamples = 32;
    private readonly long[] _responseLatencyTicks = new long[ResponseLatencySamples];
    private int _responseLatencyCount;
    // Ring of issue timestamps; sized past the most segments that can be issued ahead of the reader.
    private readonly long[] _segmentIssuedAt;
    private int _highestRespondedIndex = -1;
    private int _nextHeadIndex;
    private ConcurrentDictionary<int, byte>? _supersededSegments;

    private int GetCorruptionRetryLimit(string segmentId) =>
        _knownCorruptSegmentIds is not null && _knownCorruptSegmentIds.Contains(segmentId)
            ? 0
            : MaxCorruptionRetries;

    private void ThrowIfPlaybackFailFast()
    {
        if (!PlaybackHoleTracker.ShouldFailFast(_fileName, out var exception))
            return;
        ExceptionDispatchInfo.Capture(
            exception ?? new UsenetArticleNotFoundException(_segmentIds.Span[0])).Throw();
    }

    private SegmentDownloadResult ToDownloadResult(
        DrainedSegment drained,
        long estimate,
        string segmentId) =>
        SegmentDownloadResult.Success(drained.Stream, estimate, drained.ShortPadded, segmentId, drained.Incremental);

    /// <summary>
    /// Optional per-instance test hook invoked with the segment-boundary readiness sample,
    /// after it is taken and before the segment task is awaited. Production code never sets
    /// this; instance scope keeps parallel stream tests from observing each other's streams.
    /// </summary>
    internal Action<bool>? TestOnSegmentReadiness;

    /// <summary>Current adaptive BODY batch width (or the fixed pipeline size when not adaptive).</summary>
    internal int PrefetchBatchWidth => _batchSizer?.Current ?? _bodyPipelineBatchSize;
    // 0 when BODY requests are issued individually.
    internal int MaxPrefetchBatchWidth => _batchSizer?.Maximum ?? 0;
    internal int TaskWindowSize => _taskWindowSize;
    internal long InitialPrefetchByteCeiling => _initialPrefetchByteCeiling;

    /// <summary>
    /// Test hook: completes when the producer loop has exited (e.g. after observing
    /// the consecutive-zero-fill cancellation), so tests can assert on the final
    /// request count without racing the prefetch top-up.
    /// </summary>
    internal Task DownloadTaskForTests => _downloadTask;

    // The producer exits only after leasing every segment it will enqueue.
    bool ISegmentIssueProgress.AllSegmentsIssued => _downloadTask.IsCompleted;

    // Earlier segments were read to their end, which waits for their validation.
    ValueTask IDeliveredBytesValidation.ValidateDeliveredAsync(CancellationToken cancellationToken) =>
        _stream.ValidateDeliveredAsync(cancellationToken);

    public static Stream Create(
        Memory<string> segmentIds,
        INntpClient usenetClient,
        int articleBufferSize,
        bool usePipelinedBodyRequests,
        CancellationToken cancellationToken,
        string? fileName = null,
        long? readBudget = null,
        string[][]? segmentFallbacks = null,
        InFlightArticleBudget? inFlightArticleBudget = null,
        bool useContainerAwareFill = false,
        long? firstSegmentFileOffset = null,
        int bodyPipelineBatchWidth = BodyPipelineBatchSize,
        HashSet<string>? knownCorruptSegmentIds = null,
        IReadOnlySet<int>? knownMissingSegmentIndices = null)
    {
        return Create(
            segmentIds,
            usenetClient,
            articleBufferSize,
            estimatedSegmentSize: 0,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests,
            cancellationToken,
            fileName,
            readBudget,
            segmentFallbacks,
            inFlightArticleBudget: inFlightArticleBudget,
            useContainerAwareFill: useContainerAwareFill,
            firstSegmentFileOffset: firstSegmentFileOffset,
            bodyPipelineBatchWidth: bodyPipelineBatchWidth,
            knownCorruptSegmentIds: knownCorruptSegmentIds,
            knownMissingSegmentIndices: knownMissingSegmentIndices);
    }

    /// <param name="estimatedSegmentSize">
    /// Approximate decoded size per segment, used only for buffer capacity hints and
    /// prefetch budgeting. It must never determine how many bytes this stream emits —
    /// an estimate that is off by even one byte shifts every following byte in the file.
    /// </param>
    /// <param name="exactSegmentSizes">
    /// Exact decoded size of each segment in <paramref name="segmentIds"/>, in the same
    /// order. Supplied when the import recorded per-segment byte ranges, and required
    /// before a failed segment may be replaced with same-length gap bytes.
    /// </param>
    /// <param name="recordedSizesInferred">
    /// The exact sizes may be inferred and a caller can re-derive them, so a fallback that
    /// contradicts them is evidence to recover geometry rather than a bad donor.
    /// </param>
    internal static Stream CreateWithInitialBatchPlan
    (
        Memory<string> segmentIds,
        INntpClient usenetClient,
        int articleBufferSize,
        long estimatedSegmentSize,
        bool failFastOnFirstSegment,
        bool usePipelinedBodyRequests,
        CancellationToken cancellationToken,
        string? fileName = null,
        long? readBudget = null,
        string[][]? segmentFallbacks = null,
        ReadOnlyMemory<long> exactSegmentSizes = default,
        InFlightArticleBudget? inFlightArticleBudget = null,
        bool useContainerAwareFill = false,
        long? firstSegmentFileOffset = null,
        int bodyPipelineBatchWidth = BodyPipelineBatchSize,
        HashSet<string>? knownCorruptSegmentIds = null,
        IReadOnlySet<int>? knownMissingSegmentIndices = null,
        InitialBodyBatchPlan? initialBatchPlan = null,
        LongRange? expectedFirstSegmentRange = null,
        bool expectedFirstSegmentRangeWasClippedAtFileEnd = false,
        bool recordedSizesInferred = false
    )
    {
        return articleBufferSize == 0
            ? new UnbufferedMultiSegmentStream(
                segmentIds, usenetClient, estimatedSegmentSize, fileName, segmentFallbacks,
                exactSegmentSizes, useContainerAwareFill, firstSegmentFileOffset,
                failFastOnFirstSegment, knownCorruptSegmentIds, knownMissingSegmentIndices,
                expectedFirstSegmentRange,
                expectedFirstSegmentRangeWasClippedAtFileEnd,
                recordedSizesInferred)
            : new MultiSegmentStream(
                segmentIds,
                usenetClient,
                articleBufferSize,
                estimatedSegmentSize,
                failFastOnFirstSegment,
                usePipelinedBodyRequests,
                fileName,
                readBudget,
                segmentFallbacks,
                exactSegmentSizes,
                inFlightArticleBudget,
                useContainerAwareFill,
                firstSegmentFileOffset,
                bodyPipelineBatchWidth,
                knownCorruptSegmentIds,
                knownMissingSegmentIndices,
                initialBatchPlan,
                expectedFirstSegmentRange,
                expectedFirstSegmentRangeWasClippedAtFileEnd,
                recordedSizesInferred,
                cancellationToken);
    }

    public static Stream Create
    (
        Memory<string> segmentIds,
        INntpClient usenetClient,
        int articleBufferSize,
        long estimatedSegmentSize,
        bool failFastOnFirstSegment,
        bool usePipelinedBodyRequests,
        CancellationToken cancellationToken,
        string? fileName = null,
        long? readBudget = null,
        string[][]? segmentFallbacks = null,
        ReadOnlyMemory<long> exactSegmentSizes = default,
        InFlightArticleBudget? inFlightArticleBudget = null,
        bool useContainerAwareFill = false,
        long? firstSegmentFileOffset = null,
        int bodyPipelineBatchWidth = BodyPipelineBatchSize,
        HashSet<string>? knownCorruptSegmentIds = null,
        IReadOnlySet<int>? knownMissingSegmentIndices = null,
        LongRange? expectedFirstSegmentRange = null,
        bool expectedFirstSegmentRangeWasClippedAtFileEnd = false,
        bool recordedSizesInferred = false
    )
    {
        return CreateWithInitialBatchPlan(
            segmentIds,
            usenetClient,
            articleBufferSize,
            estimatedSegmentSize,
            failFastOnFirstSegment,
            usePipelinedBodyRequests,
            cancellationToken,
            fileName,
            readBudget,
            segmentFallbacks,
            exactSegmentSizes,
            inFlightArticleBudget,
            useContainerAwareFill,
            firstSegmentFileOffset,
            bodyPipelineBatchWidth,
            knownCorruptSegmentIds,
            knownMissingSegmentIndices,
            initialBatchPlan: null,
            expectedFirstSegmentRange,
            expectedFirstSegmentRangeWasClippedAtFileEnd,
            recordedSizesInferred);
    }

    internal sealed record FirstSegmentHybridOptions(
        Memory<string> SegmentIds,
        INntpClient UsenetClient,
        int ArticleBufferSize,
        long EstimatedSegmentSize,
        bool FailFastOnFirstSegment,
        bool UsePipelinedBodyRequests,
        string? FileName,
        long? ReadBudget,
        string[][]? SegmentFallbacks,
        ReadOnlyMemory<long> ExactSegmentSizes,
        InFlightArticleBudget? InFlightArticleBudget,
        bool UseContainerAwareFill,
        long? FirstSegmentFileOffset,
        int BodyPipelineBatchWidth,
        HashSet<string>? KnownCorruptSegmentIds,
        IReadOnlySet<int>? KnownMissingSegmentIndices,
        CancellationToken CancellationToken)
    {
        internal InitialBodyBatchPlan? InitialBatchPlan { get; init; }
        internal LongRange? ExpectedFirstSegmentRange { get; init; }
        internal bool ExpectedFirstSegmentRangeWasClippedAtFileEnd { get; init; }
        internal bool RecordedSizesInferred { get; init; }
    }

    /// <summary>
    /// Starts the first segment directly from its decoded BODY, then starts the normal
    /// buffered pipeline after the first positive requested read when a remainder is
    /// known to be required. This lets a player receive its first bytes without waiting
    /// for a whole decoded segment to drain or for later articles to be admitted.
    /// </summary>
    internal static Stream CreateFirstSegmentHybridWithInitialBatchPlan(
        Memory<string> segmentIds,
        INntpClient usenetClient,
        int articleBufferSize,
        long estimatedSegmentSize,
        bool failFastOnFirstSegment,
        bool usePipelinedBodyRequests,
        CancellationToken cancellationToken,
        string? fileName = null,
        long? readBudget = null,
        string[][]? segmentFallbacks = null,
        ReadOnlyMemory<long> exactSegmentSizes = default,
        InFlightArticleBudget? inFlightArticleBudget = null,
        bool useContainerAwareFill = false,
        long? firstSegmentFileOffset = null,
        int bodyPipelineBatchWidth = BodyPipelineBatchSize,
        HashSet<string>? knownCorruptSegmentIds = null,
        IReadOnlySet<int>? knownMissingSegmentIndices = null,
        InitialBodyBatchPlan? initialBatchPlan = null,
        bool recordedSizesInferred = false)
    {
        return CreateFirstSegmentHybridCore(
            new FirstSegmentHybridOptions(
                segmentIds,
                usenetClient,
                articleBufferSize,
                estimatedSegmentSize,
                failFastOnFirstSegment,
                usePipelinedBodyRequests,
                fileName,
                readBudget,
                segmentFallbacks,
                exactSegmentSizes,
                inFlightArticleBudget,
                useContainerAwareFill,
                firstSegmentFileOffset,
                bodyPipelineBatchWidth,
                knownCorruptSegmentIds,
                knownMissingSegmentIndices,
                cancellationToken)
            {
                InitialBatchPlan = initialBatchPlan,
                RecordedSizesInferred = recordedSizesInferred,
            },
            firstSegmentPrefixBytes: 0);
    }

    public static Stream CreateFirstSegmentHybrid(
        Memory<string> segmentIds,
        INntpClient usenetClient,
        int articleBufferSize,
        long estimatedSegmentSize,
        bool failFastOnFirstSegment,
        bool usePipelinedBodyRequests,
        CancellationToken cancellationToken,
        string? fileName = null,
        long? readBudget = null,
        string[][]? segmentFallbacks = null,
        ReadOnlyMemory<long> exactSegmentSizes = default,
        InFlightArticleBudget? inFlightArticleBudget = null,
        bool useContainerAwareFill = false,
        long? firstSegmentFileOffset = null,
        int bodyPipelineBatchWidth = BodyPipelineBatchSize,
        HashSet<string>? knownCorruptSegmentIds = null,
        IReadOnlySet<int>? knownMissingSegmentIndices = null)
    {
        return CreateFirstSegmentHybridWithInitialBatchPlan(
            segmentIds,
            usenetClient,
            articleBufferSize,
            estimatedSegmentSize,
            failFastOnFirstSegment,
            usePipelinedBodyRequests,
            cancellationToken,
            fileName,
            readBudget,
            segmentFallbacks,
            exactSegmentSizes,
            inFlightArticleBudget,
            useContainerAwareFill,
            firstSegmentFileOffset,
            bodyPipelineBatchWidth,
            knownCorruptSegmentIds,
            knownMissingSegmentIndices,
            initialBatchPlan: null);
    }

    /// <summary>
    /// Same hybrid as <see cref="CreateFirstSegmentHybrid"/>, but discards
    /// <paramref name="firstSegmentPrefixBytes"/> from the raw unbuffered head before
    /// wrapping it. Prefix discard must not go through the handoff owner — those reads
    /// are not requested response bytes and must not start the remainder.
    /// </summary>
    internal static async Task<Stream> CreatePositionedFirstSegmentHybridAsync(
        FirstSegmentHybridOptions options,
        long firstSegmentPrefixBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstSegmentPrefixBytes);

        // Fully unbuffered files skip the hybrid. A one-segment remainder still needs
        // the unbuffered head so an exact-index seek does not drain that article first.
        if (options.ArticleBufferSize == 0 || options.SegmentIds.Length == 0)
        {
#pragma warning disable CA2000 // ownership transfers to the caller after prefix discard
            var stream = Create(
                options.SegmentIds,
                options.UsenetClient,
                options.ArticleBufferSize,
                options.EstimatedSegmentSize,
                options.FailFastOnFirstSegment,
                options.UsePipelinedBodyRequests,
                options.CancellationToken,
                options.FileName,
                options.ReadBudget,
                options.SegmentFallbacks,
                options.ExactSegmentSizes,
                options.InFlightArticleBudget,
                options.UseContainerAwareFill,
                options.FirstSegmentFileOffset,
                options.BodyPipelineBatchWidth,
                options.KnownCorruptSegmentIds,
                options.KnownMissingSegmentIndices,
                options.ExpectedFirstSegmentRange,
                options.ExpectedFirstSegmentRangeWasClippedAtFileEnd,
                options.RecordedSizesInferred);
#pragma warning restore CA2000
            return await DiscardPrefixOrDisposeAsync(
                    stream, firstSegmentPrefixBytes, options.CancellationToken)
                .ConfigureAwait(false);
        }

        var plan = BuildFirstSegmentHybridPlan(options, firstSegmentPrefixBytes);

        var positionedHead = await DiscardPrefixOrDisposeAsync(
                plan.Head, firstSegmentPrefixBytes, options.CancellationToken)
            .ConfigureAwait(false);

        if (plan.CreateRemainder is null)
        {
            StreamStartupTrace.TryRecord(
                StreamStartupPhase.HandoffNotNeeded,
                plan.HeadAvailableBytes);
            return positionedHead;
        }

        StreamStartupTrace.TryRecord(
            plan.StartPolicy == RemainderStartPolicy.AfterFirstPositiveRead
                ? StreamStartupPhase.HandoffEager
                : StreamStartupPhase.HandoffLegacyLazy,
            plan.HeadAvailableBytes);
        return new FirstSegmentHandoffStream(
            positionedHead,
            plan.CreateRemainder,
            plan.StartPolicy,
            options.CancellationToken);
    }

    private static async Task<Stream> DiscardPrefixOrDisposeAsync(
        Stream stream,
        long prefixBytes,
        CancellationToken cancellationToken)
    {
        if (prefixBytes == 0)
            return stream;

        try
        {
            var discardStarted = Stopwatch.GetTimestamp();
            if (stream is UnbufferedMultiSegmentStream unbuffered)
            {
                await unbuffered.DiscardPrefixBytesAsync(prefixBytes, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await stream.DiscardExactBytesAsync(prefixBytes, cancellationToken)
                    .ConfigureAwait(false);
            }
            StreamStartupTrace.TryRecord(
                StreamStartupPhase.PrefixDiscard,
                prefixBytes,
                Stopwatch.GetElapsedTime(discardStarted));
            return stream;
        }
        catch (Exception primaryFailure)
        {
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupFailure) when (cleanupFailure is not OutOfMemoryException)
            {
                Log.Debug(
                    cleanupFailure,
                    "Failed to dispose first-segment stream after prefix positioning failed.");
            }

            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            throw;
        }
    }

    private static Stream CreateFirstSegmentHybridCore(
        FirstSegmentHybridOptions options,
        long firstSegmentPrefixBytes)
    {
        if (options.ArticleBufferSize == 0 || options.SegmentIds.Length == 0)
        {
            StreamStartupTrace.TryRecord(StreamStartupPhase.HandoffNotNeeded);
            return Create(
                options.SegmentIds,
                options.UsenetClient,
                options.ArticleBufferSize,
                options.EstimatedSegmentSize,
                options.FailFastOnFirstSegment,
                options.UsePipelinedBodyRequests,
                options.CancellationToken,
                options.FileName,
                options.ReadBudget,
                options.SegmentFallbacks,
                options.ExactSegmentSizes,
                options.InFlightArticleBudget,
                options.UseContainerAwareFill,
                options.FirstSegmentFileOffset,
                options.BodyPipelineBatchWidth,
                options.KnownCorruptSegmentIds,
                options.KnownMissingSegmentIndices,
                recordedSizesInferred: options.RecordedSizesInferred);
        }

        var plan = BuildFirstSegmentHybridPlan(options, firstSegmentPrefixBytes);

        if (plan.CreateRemainder is null)
        {
            StreamStartupTrace.TryRecord(
                StreamStartupPhase.HandoffNotNeeded,
                plan.HeadAvailableBytes);
            return plan.Head;
        }

        StreamStartupTrace.TryRecord(
            plan.StartPolicy == RemainderStartPolicy.AfterFirstPositiveRead
                ? StreamStartupPhase.HandoffEager
                : StreamStartupPhase.HandoffLegacyLazy,
            plan.HeadAvailableBytes);
        return new FirstSegmentHandoffStream(
            plan.Head,
            plan.CreateRemainder,
            plan.StartPolicy,
            options.CancellationToken);
    }

    private sealed record FirstSegmentHybridPlan(
        Stream Head,
        Func<CancellationToken, Stream>? CreateRemainder,
        RemainderStartPolicy StartPolicy,
        long? HeadAvailableBytes);

    private static FirstSegmentHybridPlan BuildFirstSegmentHybridPlan(
        FirstSegmentHybridOptions options,
        long firstSegmentPrefixBytes)
    {
        if (options.InitialBatchPlan is { } initialPlan &&
            initialPlan.PlannedSegmentCount != options.SegmentIds.Length - 1)
        {
            throw new ArgumentException(
                "The finite-range batch plan must match the buffered remainder segment count.",
                nameof(options));
        }

        var effectiveReadBudget =
            options.ReadBudget ?? NzbWebDAV.WebDav.Requests.RangeContext.GetReadBudget();
        var firstExactSizes = options.ExactSegmentSizes.Length == options.SegmentIds.Length
            ? options.ExactSegmentSizes[..1]
            : default;
        var planningFirstExactSizes = firstExactSizes.Length == 1
            ? firstExactSizes
            : options.ExpectedFirstSegmentRange is { } expected
                ? new[] { expected.Count }.AsMemory()
                : default;
        var remainingExactSizes = options.ExactSegmentSizes.Length == options.SegmentIds.Length
            ? options.ExactSegmentSizes[1..]
            : default;
        var firstFallbacks = options.SegmentFallbacks is { Length: > 0 }
            ? options.SegmentFallbacks[..1]
            : null;
        var remainingFallbacks = options.SegmentFallbacks is { Length: > 1 }
            ? options.SegmentFallbacks[1..]
            : null;
        var firstKnownMissing = options.KnownMissingSegmentIndices?
            .Where(index => index == 0)
            .ToHashSet();
        var remainingKnownMissing = options.KnownMissingSegmentIndices?
            .Where(index => index > 0)
            .Select(index => index - 1)
            .ToHashSet();
        var remainingOffset = options.FirstSegmentFileOffset;
        if (remainingOffset is not null && planningFirstExactSizes.Length == 1)
        {
            try { remainingOffset = checked(remainingOffset.Value + planningFirstExactSizes.Span[0]); }
            catch (OverflowException) { remainingOffset = null; }
        }

        var remainderPlan = PlanHybridRemainder(
            options.SegmentIds.Length,
            planningFirstExactSizes,
            firstSegmentPrefixBytes,
            effectiveReadBudget);

#pragma warning disable CA2000 // ownership transfers to the caller / FirstSegmentHandoffStream
        Stream head = new UnbufferedMultiSegmentStream(
            options.SegmentIds[..1],
            options.UsenetClient,
            options.EstimatedSegmentSize,
            options.FileName,
            firstFallbacks,
            planningFirstExactSizes,
            options.UseContainerAwareFill,
            options.FirstSegmentFileOffset,
            options.FailFastOnFirstSegment,
            options.KnownCorruptSegmentIds,
            firstKnownMissing,
            options.ExpectedFirstSegmentRange,
            options.ExpectedFirstSegmentRangeWasClippedAtFileEnd,
            options.RecordedSizesInferred);
#pragma warning restore CA2000

        if (!remainderPlan.NeedsRemainder)
        {
            return new FirstSegmentHybridPlan(
                head,
                null,
                RemainderStartPolicy.None,
                remainderPlan.HeadAvailableBytes);
        }

        // Capture every remainder input in this closure. Do not re-read AsyncLocal
        // RangeContext after the handoff scheduling boundary.
        Func<CancellationToken, Stream> createRemainder = lifetimeToken => CreateWithInitialBatchPlan(
            options.SegmentIds[1..],
            options.UsenetClient,
            options.ArticleBufferSize,
            options.EstimatedSegmentSize,
            failFastOnFirstSegment: false,
            options.UsePipelinedBodyRequests,
            lifetimeToken,
            options.FileName,
            remainderPlan.RemainderBudget,
            remainingFallbacks,
            remainingExactSizes,
            options.InFlightArticleBudget,
            options.UseContainerAwareFill,
            remainingOffset,
            options.BodyPipelineBatchWidth,
            options.KnownCorruptSegmentIds,
            remainingKnownMissing,
            options.InitialBatchPlan,
            recordedSizesInferred: options.RecordedSizesInferred);

        return new FirstSegmentHybridPlan(
            head,
            createRemainder,
            remainderPlan.StartPolicy,
            remainderPlan.HeadAvailableBytes);
    }

    internal readonly record struct HybridRemainderPlan(
        long? HeadAvailableBytes,
        long? RemainderBudget,
        bool NeedsRemainder,
        RemainderStartPolicy StartPolicy);

    internal static HybridRemainderPlan PlanHybridRemainder(
        int segmentCount,
        ReadOnlyMemory<long> firstExactSizes,
        long firstSegmentPrefixBytes,
        long? readBudget)
    {
        var hasExactHead = firstExactSizes.Length == 1;
        long? headAvailable = null;
        if (hasExactHead)
        {
            var segmentSize = firstExactSizes.Span[0];
            if (firstSegmentPrefixBytes < 0 || firstSegmentPrefixBytes >= segmentSize)
            {
                throw new InvalidOperationException(
                    "Exact-index prefix is outside the mapped target segment.");
            }

            headAvailable = checked(segmentSize - firstSegmentPrefixBytes);
        }

        if (segmentCount <= 1)
        {
            return new HybridRemainderPlan(
                headAvailable,
                null,
                false,
                RemainderStartPolicy.None);
        }

        if (hasExactHead)
        {
            if (readBudget is { } budget)
            {
                var headContribution = Math.Min(budget, headAvailable!.Value);
                var remainderBudget = checked(budget - headContribution);
                var needsRemainder = remainderBudget > 0;
                return new HybridRemainderPlan(
                    headAvailable,
                    remainderBudget,
                    needsRemainder,
                    needsRemainder
                        ? RemainderStartPolicy.AfterFirstPositiveRead
                        : RemainderStartPolicy.None);
            }

            return new HybridRemainderPlan(
                headAvailable,
                null,
                true,
                RemainderStartPolicy.AfterFirstPositiveRead);
        }

        // Unknown first-segment size: a full GET always needs the remainder, so eagerness
        // is safe. A finite budget cannot prove the head will not satisfy it, so keep
        // the legacy lazy-at-EOF start and do not subtract an unknown head.
        if (readBudget is null)
        {
            return new HybridRemainderPlan(
                null,
                null,
                true,
                RemainderStartPolicy.AfterFirstPositiveRead);
        }

        return new HybridRemainderPlan(
            null,
            readBudget,
            true,
            RemainderStartPolicy.AtHeadEof);
    }

    private MultiSegmentStream
    (
        Memory<string> segmentIds,
        INntpClient usenetClient,
        int articleBufferSize,
        long estimatedSegmentSize,
        bool failFastOnFirstSegment,
        bool usePipelinedBodyRequests,
        string? fileName,
        long? readBudget,
        string[][]? segmentFallbacks,
        ReadOnlyMemory<long> exactSegmentSizes,
        InFlightArticleBudget? inFlightArticleBudget,
        bool useContainerAwareFill,
        long? firstSegmentFileOffset,
        int bodyPipelineBatchWidth,
        HashSet<string>? knownCorruptSegmentIds,
        IReadOnlySet<int>? knownMissingSegmentIndices,
        InitialBodyBatchPlan? initialBatchPlan,
        LongRange? expectedFirstSegmentRange,
        bool expectedFirstSegmentRangeWasClippedAtFileEnd,
        bool recordedSizesInferred,
        CancellationToken cancellationToken
    )
    {
        if (expectedFirstSegmentRangeWasClippedAtFileEnd && expectedFirstSegmentRange is null)
        {
            throw new ArgumentException(
                "A clipped first-segment range requires an expected first-segment range.",
                nameof(expectedFirstSegmentRangeWasClippedAtFileEnd));
        }
        if (expectedFirstSegmentRange is not null && segmentIds.Length == 0)
        {
            throw new ArgumentException(
                "First-segment geometry cannot be validated without a first segment.",
                nameof(expectedFirstSegmentRange));
        }

        _segmentIds = segmentIds;
        _segmentFallbacks = segmentFallbacks;
        _usenetClient = usenetClient;
        _estimatedSegmentSize = estimatedSegmentSize;
        _segmentSizes = new SegmentSizes(exactSegmentSizes, segmentIds.Length);
        _failFastOnFirstSegment = failFastOnFirstSegment;
        _useContainerAwareFill = useContainerAwareFill;
        _recordedSizesInferred = recordedSizesInferred;
        _firstSegmentFileOffset = firstSegmentFileOffset;
        _expectedFirstSegmentRange = expectedFirstSegmentRange;
        _expectedFirstSegmentRangeWasClippedAtFileEnd =
            expectedFirstSegmentRangeWasClippedAtFileEnd;
        _knownCorruptSegmentIds = knownCorruptSegmentIds;
        _knownMissingSegmentIndices = knownMissingSegmentIndices;
        _fileName = string.IsNullOrEmpty(fileName) ? "unknown" : fileName;
        _readBudget = readBudget ?? NzbWebDAV.WebDav.Requests.RangeContext.GetReadBudget();
        _budget = inFlightArticleBudget ?? InFlightArticleBudget.Current;
        _bodyPipelineBatchSize = Math.Min(Math.Max(1, bodyPipelineBatchWidth), articleBufferSize);
        _taskWindowSize = CalculateTaskWindowSize(
            articleBufferSize, usePipelinedBodyRequests, _bodyPipelineBatchSize);
        _prefetchByteCeiling = _taskWindowSize > 0 && estimatedSegmentSize > 0
            ? SaturatingMultiply(_taskWindowSize, estimatedSegmentSize)
            : 0;
        if (_prefetchByteCeiling > 0 && _readBudget is > 0 && !_segmentSizes.TryGetExactSize(0, out _))
        {
            var rangeWindow = Math.Min(_readBudget.Value, long.MaxValue - estimatedSegmentSize)
                + estimatedSegmentSize;
            var batchWindow = SaturatingMultiply(
                Math.Max(1, _bodyPipelineBatchSize), estimatedSegmentSize);
            _prefetchByteCeiling = Math.Min(_prefetchByteCeiling, Math.Max(batchWindow, rangeWindow));
        }
        // One-article batches keep the primary re-probe; the configured prefetch window is kept for throughput.
        var maxBatchWidth = GetPlannedSegmentBytes(0) >= PipelinedArticleSizeLimit
            ? Math.Min(_bodyPipelineBatchSize, 1)
            : _bodyPipelineBatchSize;
        _batchSizer = usePipelinedBodyRequests
            ? new AdaptiveBodyBatchSizer(
                maxBatchWidth,
                Math.Min(initialBatchPlan?.InitialBatchWidth ?? maxBatchWidth, maxBatchWidth),
                initialBatchPlan?.WideningNotBeforeDeliveredSegment ?? 0)
            : null;
        if (_batchSizer is not null && _bodyPipelineBatchSize != BodyPipelineBatchSize)
        {
            Log.Debug(
                "Streaming BODY batch width for {FileName} configured at {BatchWidth} (stock {StockWidth}).",
                _fileName,
                _bodyPipelineBatchSize,
                BodyPipelineBatchSize);
        }
        _streamTasks = Channel.CreateBounded<Task<SegmentDownloadResult>>(_taskWindowSize);
        _segmentIssuedAt = new long[_taskWindowSize * 2 + Math.Max(1, _bodyPipelineBatchSize) + 1];
        _stripeCount = usePipelinedBodyRequests
            ? ResolveStripeCount(initialBatchPlan, articleBufferSize, cancellationToken)
            : 1;
        _stripeTarget = _stripeCount;
        // A full striped group keeps every connection pipelined; half the window keeps a ramp when it would fill it.
        var initialSegmentCount = Math.Min(
            _segmentIds.Length,
            Math.Max(
                MinInitialPrefetchSegments,
                Math.Min((long)_stripeCount * _bodyPipelineBatchSize, _taskWindowSize / 2)));
        var initialPlannedBytes = 0L;
        for (var segmentIndex = 0; segmentIndex < initialSegmentCount; segmentIndex++)
        {
            var plannedBytes = GetPlannedSegmentBytes(segmentIndex);
            initialPlannedBytes = plannedBytes > long.MaxValue - initialPlannedBytes
                ? long.MaxValue
                : initialPlannedBytes + plannedBytes;
        }
        _initialPrefetchByteCeiling = Math.Min(_prefetchByteCeiling, initialPlannedBytes);
        if (_stripeCount > 1)
        {
            Log.Debug(
                "Interleaving BODY batches for {FileName} across up to {StripeCount} connections.",
                _fileName,
                _stripeCount);
        }
        _cts = ContextualCancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _downloadTask = DownloadSegments(usePipelinedBodyRequests, _cts.Token);
    }

    // A finite-range plan target is itself a hint; the stream-open hint still bounds it.
    internal static int ResolveStripeCount(
        InitialBodyBatchPlan? initialBatchPlan,
        int articleBufferSize,
        CancellationToken cancellationToken)
    {
        var hint = cancellationToken.GetContext<StreamingStripeContext>()?.StripeCount;
        var target = initialBatchPlan?.EffectiveConnectionTarget is { } planned
            ? Math.Min(planned, hint ?? planned)
            : hint ?? 1;
        return Math.Clamp(target, 1, Math.Max(1, articleBufferSize));
    }

    /// <summary>
    /// Computes the number of ordered segment tasks that may wait ahead of the consumer.
    /// Pipelined BODY requests retain one connection per batch, not per segment, so a
    /// segment-only window of <paramref name="articleBufferSize"/> could use only a quarter
    /// of the per-stream connection budget at the normal four-article batch width. Expand
    /// the task window by that width; the connection semaphore remains the concurrency
    /// authority and <see cref="InFlightArticleBudget"/> remains the decoded-byte authority.
    /// </summary>
    internal static int CalculateTaskWindowSize(
        int articleBufferSize,
        bool usePipelinedBodyRequests,
        int bodyPipelineBatchWidth = BodyPipelineBatchSize)
    {
        if (articleBufferSize <= 0) return 0;
        if (!usePipelinedBodyRequests) return articleBufferSize;

        var initialBatchWidth = Math.Min(bodyPipelineBatchWidth, articleBufferSize);
        return articleBufferSize > int.MaxValue / initialBatchWidth
            ? int.MaxValue
            : articleBufferSize * initialBatchWidth;
    }

    internal static long SaturatingMultiply(long multiplier, long multiplicand)
    {
        if (multiplier <= 0 || multiplicand <= 0)
            return 0;

        return multiplier > long.MaxValue / multiplicand
            ? long.MaxValue
            : multiplier * multiplicand;
    }

    private async Task DownloadSegments(
        bool usePipelinedBodyRequests,
        CancellationToken cancellationToken)
    {
        try
        {
            if (usePipelinedBodyRequests)
                await DownloadPipelinedSegments(cancellationToken).ConfigureAwait(false);
            else
                await DownloadIndividualSegments(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _streamTasks.Writer.TryComplete();
        }
        catch (OutOfMemoryException oom)
        {
            OomDiagnostics.LogHeapStateOnOom(oom, "segment download pipeline");
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _streamTasks.Writer.TryComplete(exception);
        }
        finally
        {
            _streamTasks.Writer.TryComplete();
        }

        return;
    }

    private async Task DownloadPipelinedSegments(CancellationToken cancellationToken)
    {
        for (var batchStart = 0; batchStart < _segmentIds.Length;)
        {
            if (ShouldStopPrefetch(_segmentsEnqueued, _enqueuedBytes))
                break;

            // Fail-fast must win before the batch goes on the wire: once BODY commands
            // are issued, every response needs an owner that drains or disposes it.
            ThrowIfPlaybackFailFast();

            await WaitForPrefetchCeilingAsync(cancellationToken).ConfigureAwait(false);

            // Adaptive width: narrower batches → more outstanding connections at the
            // same article-buffer memory cost when the consumer is starving.
            var batchWidth = _batchSizer?.Current ?? _bodyPipelineBatchSize;
            await _streamTasks.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false);
            var issued = _stripeCount > 1 && batchWidth > 1
                ? await TryIssueStripedGroupAsync(batchStart, batchWidth, cancellationToken).ConfigureAwait(false)
                : 0;
            if (issued == 0)
            {
                issued = await IssueContiguousBatchAsync(
                        batchStart, Math.Min(batchWidth, _segmentIds.Length - batchStart), cancellationToken)
                    .ConfigureAwait(false);
            }

            batchStart += issued;
        }
    }

    private async Task<int> IssueContiguousBatchAsync(
        int batchStart,
        int batchCount,
        CancellationToken cancellationToken)
    {
        var group = new PipelinedGroup(batchCount);
        try
        {
            // Reserve decoded-memory capacity before the request takes a streaming
            // permit. A saturated budget must never occupy all download slots with
            // requests that cannot yet be drained.
            for (var slot = 0; slot < batchCount; slot++)
            {
                group.Leases[slot] = await LeaseSegmentBytesAsync(
                    GetPlannedSegmentBytes(batchStart + slot), cancellationToken).ConfigureAwait(false);
            }

            var (running, admitted) = await IssueBatchAsync(
                    batchStart, Enumerable.Range(0, batchCount).ToArray(), group, cancellationToken)
                .ConfigureAwait(false);
            await PublishReadyAsync(batchStart, group, cancellationToken).ConfigureAwait(false);
            await admitted.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (running is { IsCompleted: false })
                TrackRunningBatch(running);
            return batchCount;
        }
        catch
        {
            AbandonUnpublished(group);
            throw;
        }
    }

    /// <summary>
    /// Issues S batches over the next S×W segments, batch j taking j, j+S, j+2S, ...
    /// Consecutive segments then arrive in parallel on different connections instead of
    /// one after another on a single pipelined connection. Returns 0 when the group is
    /// not worth striping or its memory cannot be reserved without waiting.
    /// </summary>
    private async Task<int> TryIssueStripedGroupAsync(
        int groupStart,
        int width,
        CancellationToken cancellationToken)
    {
        if (_stripeTarget < 2) return 0;
        var freeSlots = _taskWindowSize - _streamTasks.Reader.Count;
        var limit = (int)Math.Min(
            Math.Min(_segmentIds.Length - groupStart, freeSlots),
            (long)_stripeTarget * width);
        if (limit < 2) return 0;

        var ceilingRoom = _prefetchByteCeiling > 0
            ? CurrentPrefetchByteCeiling - Interlocked.Read(ref _inFlightPrefetchBytes)
            : long.MaxValue;
        var sizes = new long[limit];
        var count = 0;
        var bytes = 0L;
        while (count < limit && !ShouldStopPrefetch(_segmentsEnqueued + count, _enqueuedBytes + bytes))
        {
            var planned = GetPlannedSegmentBytes(groupStart + count);
            if (count > 0 && planned > ceilingRoom - bytes) break;
            sizes[count++] = planned;
            bytes += planned;
        }

        // A short group (tail, or a window that is nearly full) still spreads across every
        // stripe, so a few wide batches never serialize the last segments on few connections.
        var stripes = Math.Min(_stripeTarget, count);
        if (stripes < 2) return 0;

        // Waiting while holding part of a group can deadlock against another stream doing
        // the same, so the whole group is reserved at once or the contiguous path runs.
        ArticleByteLease?[]? leases = _budget is null
            ? Enumerable.Repeat(ArticleByteLease.Empty, count).ToArray()
            : _budget.TryLeaseAll(sizes.AsSpan(0, count));
        if (leases is null) return 0;

        var group = new PipelinedGroup(leases);
        var issuedBatches = new List<Task>(stripes);
        var remaining = Enumerable.Range(0, count).ToList();
        try
        {
            for (var issued = 0; remaining.Count > 0; issued++)
            {
                if (issued > 0)
                    ThrowIfPlaybackFailFast();
                // After a capacity loss the rest of the group takes the nearest segments
                // instead of queueing them behind stripes that can no longer run in parallel.
                var stride = Math.Clamp(Math.Min(stripes - issued, _stripeTarget), 1, remaining.Count);
                var slots = TakeStride(remaining, stride, width);
                var runningBefore = issuedBatches.Count(batch => !batch.IsCompleted);
                var issue = IssueBatchAsync(groupStart, slots, group, cancellationToken);
                var waited = !issue.IsCompleted;
                var (lastResponse, admitted) = await issue.ConfigureAwait(false);
                if (!admitted.IsCompleted)
                {
                    // Local hits returned ahead of remote admission are readable now; the
                    // capacity decision still waits until the misses hold a connection.
                    await PublishReadyAsync(groupStart, group, cancellationToken).ConfigureAwait(false);
                    var probe = await IssueLocalFrontierAsync(groupStart, group, remaining, cancellationToken)
                        .ConfigureAwait(false);
                    await Task.WhenAll(admitted, probe.Admitted).WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (probe.Running is { IsCompleted: false })
                        TrackRunningBatch(probe.Running);
                    waited = true;
                }

                if (lastResponse is { IsCompleted: false })
                    TrackRunningBatch(lastResponse);
                // Admitted only once one of this group's own batches finished: the stream
                // holds fewer connections than stripes, so shrink to what it actually holds.
                if (waited && issuedBatches.Count(batch => !batch.IsCompleted) < runningBefore)
                    _stripeTarget = Math.Clamp(Volatile.Read(ref _activeBatches), 1, _stripeCount);
                if (lastResponse is not null)
                    issuedBatches.Add(lastResponse);
                await PublishReadyAsync(groupStart, group, cancellationToken).ConfigureAwait(false);
            }

            return count;
        }
        catch
        {
            AbandonUnpublished(group);
            throw;
        }
    }

    /// <summary>
    /// While a stripe waits for a connection, issues the next unissued file-order slot alone
    /// until one is not served locally, so cached segments ahead of the remote frontier never
    /// wait on another batch's admission. A remote probe is the nearest segment, which is what
    /// narrowing would pick anyway; the caller awaits its admission before tracking it.
    /// </summary>
    private async Task<(Task? Running, Task Admitted)> IssueLocalFrontierAsync(
        int groupStart,
        PipelinedGroup group,
        List<int> remaining,
        CancellationToken cancellationToken)
    {
        while (group.Published < group.Tasks.Length && group.Tasks[group.Published] is null)
        {
            var slot = group.Published;
            remaining.Remove(slot);
            var (running, admitted) = await IssueBatchAsync(groupStart, [slot], group, cancellationToken)
                .ConfigureAwait(false);
            await PublishReadyAsync(groupStart, group, cancellationToken).ConfigureAwait(false);
            if (running is null && admitted.IsCompleted) continue;
            return (running, admitted);
        }

        return (null, Task.CompletedTask);
    }

    /// <summary>Removes and returns remaining[0], remaining[stride], ... up to width slots.</summary>
    internal static int[] TakeStride(List<int> remaining, int stride, int width)
    {
        var take = Math.Min(width, (remaining.Count + stride - 1) / stride);
        var slots = new int[take];
        for (var index = 0; index < take; index++)
            slots[index] = remaining[index * stride];
        for (var index = take - 1; index >= 0; index--)
            remaining.RemoveAt(index * stride);
        return slots;
    }

    /// <summary>
    /// Issues one batch. Running completes as the batch nears release (null if already done);
    /// Admitted completes once its remote requests hold a connection.
    /// </summary>
    private async Task<(Task? Running, Task Admitted)> IssueBatchAsync(
        int groupStart,
        int[] slots,
        PipelinedGroup group,
        CancellationToken cancellationToken)
    {
        Task? running = null;
        var admitted = Task.CompletedTask;
        // Known degraded holes never enter a provider batch. They still ask local
        // patch/cache layers first, and their tasks stay in file order with live
        // results so the consumer's segment-boundary contract is unchanged.
        var liveIds = new List<SegmentId>(slots.Length);
        foreach (var slot in slots.Where(slot => _knownMissingSegmentIndices?.Contains(groupStart + slot) != true))
            liveIds.Add(_segmentIds.Span[groupStart + slot]);

        Task<UsenetDecodedBodyResponse>[] liveResponses = [];
        if (liveIds.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fetched = await FetchAttributedBatchResponsesAsync(liveIds.ToArray(), cancellationToken)
                .ConfigureAwait(false);
            liveResponses = fetched.Responses;
            admitted = fetched.Admitted;
            EnqueueBatchCompletionObserver(fetched.Completion);
            // Responses complete in order and the connection is released only after the last
            // body drains, so the last response is a race-free "about to free" marker.
            if (liveResponses.Length > 0 && !liveResponses[^1].IsCompleted)
                running = liveResponses[^1];
        }

        var liveResponseIndex = 0;
        foreach (var slot in slots)
        {
            var lease = group.Leases[slot]!;
            group.Leases[slot] = null;
            var segmentIndex = groupStart + slot;
            var segmentId = _segmentIds.Span[segmentIndex];
            NoteSegmentIssued(segmentIndex);
            group.Tasks[slot] = _knownMissingSegmentIndices?.Contains(segmentIndex) == true
                ? DownloadKnownMissingSegment(
                    segmentId, segmentIndex, lease, isFirstSegment: segmentIndex == 0, cancellationToken)
                : DownloadBatchSegment(
                    liveResponses[liveResponseIndex++],
                    segmentId,
                    segmentIndex,
                    isFirstSegment: segmentIndex == 0,
                    lease,
                    cancellationToken);
        }

        return (running, admitted);
    }

    // Producer-only. Holding more batches at once than the target proves the capacity is back.
    private void TrackRunningBatch(Task lastResponse)
    {
        var running = Interlocked.Increment(ref _activeBatches);
        if (running > _stripeTarget)
            _stripeTarget = Math.Min(_stripeCount, running);
        lastResponse.ContinueWith(
            static (_, state) => ((MultiSegmentStream)state!).ReleaseRunningBatch(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ReleaseRunningBatch() => Interlocked.Decrement(ref _activeBatches);

    /// <summary>Publishes the longest file-order prefix of created tasks.</summary>
    private async Task PublishReadyAsync(
        int groupStart,
        PipelinedGroup group,
        CancellationToken cancellationToken)
    {
        while (group.Published < group.Tasks.Length && group.Tasks[group.Published] is { } task)
        {
            var planned = GetPlannedSegmentBytes(groupStart + group.Published);
            // Counted before the write so an immediate consumer release cannot go negative.
            Interlocked.Add(ref _inFlightPrefetchBytes, planned);
            try
            {
                await _streamTasks.Writer.WriteAsync(task, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                ReleaseInFlightPrefetchBytes(planned);
                throw;
            }

            group.Published++;
            _segmentsEnqueued++;
            _enqueuedBytes += planned;
        }
    }

    private void AbandonUnpublished(PipelinedGroup group)
    {
        for (var slot = group.Published; slot < group.Tasks.Length; slot++)
        {
            if (group.Tasks[slot] is { } task)
                _orphanedDisposals.Enqueue(DisposeStreamAsync(task));
        }

        foreach (var lease in group.Leases)
            lease?.Dispose();
    }

    private sealed class PipelinedGroup(ArticleByteLease?[] leases)
    {
        public PipelinedGroup(int count) : this(new ArticleByteLease?[count])
        {
        }

        public ArticleByteLease?[] Leases { get; } = leases;
        public Task<SegmentDownloadResult>?[] Tasks { get; } = new Task<SegmentDownloadResult>?[leases.Length];
        public int Published { get; set; }
    }

    private async Task DownloadIndividualSegments(CancellationToken cancellationToken)
    {
        var enqueuedBytes = 0L;
        for (var index = 0; index < _segmentIds.Length; index++)
        {
            if (ShouldStopPrefetch(index, enqueuedBytes))
                break;

            await WaitForPrefetchCeilingAsync(cancellationToken).ConfigureAwait(false);

            var segmentId = _segmentIds.Span[index];
            await _streamTasks.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false);
            var lease = await LeaseSegmentBytesAsync(
                GetPlannedSegmentBytes(index), cancellationToken).ConfigureAwait(false);
            NoteSegmentIssued(index);
            var streamTask = DownloadSegment(
                segmentId, index, lease, isFirstSegment: index == 0, cancellationToken);
            var planned = GetPlannedSegmentBytes(index);
            try
            {
                await _streamTasks.Writer.WriteAsync(streamTask, cancellationToken).ConfigureAwait(false);
                enqueuedBytes += planned;
                Interlocked.Add(ref _inFlightPrefetchBytes, planned);
            }
            catch
            {
                _orphanedDisposals.Enqueue(DisposeStreamAsync(streamTask));
                throw;
            }
        }
    }

    /// <summary>
    /// Stop enqueueing once the bytes already in flight cover the read budget plus one
    /// segment of slack, which absorbs the prefix a seek discards from the first segment.
    /// Requires recorded per-segment sizes; without them the estimate can undershoot actual
    /// segment lengths and truncate a range response, so prefetch is capped only by
    /// <see cref="WaitForPrefetchCeilingAsync"/>.
    /// </summary>
    private bool ShouldStopPrefetch(int segmentsEnqueued, long enqueuedBytes)
    {
        // Range read budget: permanent stop once enough of the file is planned.
        if (_readBudget is not null)
        {
            if (_segmentSizes.TryGetExactSize(0, out var slack))
                return enqueuedBytes >= _readBudget.Value + slack;
            return false;
        }

        // Full-file / non-range: never permanently stop (consumer needs the whole file).
        // Per-stream byte ceiling is enforced by WaitForPrefetchCeilingAsync instead.
        return false;
    }

    private sealed record FetchedBodyBatch(
        Task<UsenetDecodedBodyResponse>[] Responses,
        Task Completion,
        Task Admitted);

    private async Task<FetchedBodyBatch> FetchAttributedBatchResponsesAsync(
        SegmentId[] liveIds,
        CancellationToken cancellationToken)
    {
        using var fetchAttribution = FetchAttributionContext.Begin(_fileName);
        var batch = await _usenetClient.DecodedBodiesAsync(
            liveIds, onConnectionReadyAgain: null, cancellationToken).ConfigureAwait(false);
        if (batch.Responses.Count != liveIds.Length)
        {
            // The client broke the batch contract after the commands went on the wire.
            // Drain whatever arrived so the shared batch connection can complete.
            foreach (var responseTask in batch.Responses)
            {
                try
                {
                    var response = await responseTask.ConfigureAwait(false);
                    if (response.Stream is not null)
                        await response.Stream.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    Log.Debug(e, "Failed to drain BODY response after batch size mismatch.");
                }
            }

            try
            {
                await batch.Completion.ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.Debug(e, "Failed to observe BODY batch completion after size mismatch.");
            }

            throw new InvalidOperationException(
                $"Pipelined BODY returned {batch.Responses.Count} responses for {liveIds.Length} requests.");
        }

        return new FetchedBodyBatch(batch.Responses.ToArray(), batch.Completion, batch.Admitted);
    }

    private void EnqueueBatchCompletionObserver(Task completion)
    {
        while (_batchCompletionObservers.TryPeek(out var head) &&
               head.Status == TaskStatus.RanToCompletion)
        {
            _batchCompletionObservers.TryDequeue(out _);
        }

        _batchCompletionObservers.Enqueue(ObserveBatchCompletionAsync(completion));
    }

    private static async Task ObserveBatchCompletionAsync(Task completion)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.Debug(exception, "Pipelined BODY batch completion failed.");
        }
    }

    /// <summary>
    /// When <see cref="_readBudget"/> is null, pause the producer once in-flight planned
    /// bytes reach task-window-size × estimated segment size so full-file GETs cannot retain
    /// unbounded decoded bytes ahead of the consumer. For pipelined BODY requests the task
    /// window accounts for every segment needed to keep the connection budget occupied.
    /// </summary>
    private async Task WaitForPrefetchCeilingAsync(CancellationToken cancellationToken)
    {
        if (_prefetchByteCeiling <= 0) return;

        while (Interlocked.Read(ref _inFlightPrefetchBytes) >= CurrentPrefetchByteCeiling)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wait = Volatile.Read(ref _prefetchSpace);
            if (Interlocked.Read(ref _inFlightPrefetchBytes) < CurrentPrefetchByteCeiling)
                return;
            await wait.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Like AltMount, only the opening burst is capped: once the reader takes its first segment
    // the full window opens, so low-bitrate playback is not starved by a consumption-paced ramp.
    internal long CurrentPrefetchByteCeiling => Interlocked.Read(ref _consumedPrefetchBytes) > 0
        ? _prefetchByteCeiling
        : _initialPrefetchByteCeiling;

    private void ReleaseInFlightPrefetchBytes(long plannedBytes)
    {
        if (plannedBytes <= 0) return;
        Interlocked.Add(ref _inFlightPrefetchBytes, -plannedBytes);
        var prior = Interlocked.Exchange(
            ref _prefetchSpace,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        prior.TrySetResult();
    }

    private long GetPlannedSegmentBytes(int segmentIndex) =>
        _segmentSizes.TryGetExactSize(segmentIndex, out var exact)
            ? exact
            : Math.Max(0, _estimatedSegmentSize);

    private async Task<SegmentDownloadResult> DownloadSegment(
        string segmentId,
        int segmentIndex,
        ArticleByteLease initialLease,
        bool isFirstSegment,
        CancellationToken cancellationToken,
        bool incremental = true,
        SegmentRetryResume? resume = null
    )
    {
        var estimate = GetPlannedSegmentBytes(segmentIndex);
        // Known-corrupt articles zero-fill after one fetch; never stream their prefix.
        incremental &= GetCorruptionRetryLimit(segmentId) > 0;
        var lease = initialLease;
        try
        {
            ThrowIfPlaybackFailFast();
            if (_knownMissingSegmentIndices?.Contains(segmentIndex) == true)
            {
                var result = await DownloadKnownMissingSegment(
                    segmentId, segmentIndex, lease, isFirstSegment, cancellationToken).ConfigureAwait(false);
                lease = null;
                return result;
            }

            var persistent = resume?.Persistent ?? new PersistentCorruptionTracker();
            var priorFailure = resume?.Failure;
            for (var attempt = resume?.Attempt ?? 0; ; attempt++)
            {
                try
                {
                    // An incremental body that failed mid-drain resumes at its own attempt so
                    // retry limits and corruption evidence carry across the handoff.
                    if (priorFailure is not null)
                    {
                        var failure = priorFailure;
                        priorFailure = null;
                        ExceptionDispatchInfo.Capture(failure).Throw();
                    }

                    UsenetDecodedBodyResponse bodyResponse;
                    using (FetchAttributionContext.Begin(_fileName))
                    {
                        bodyResponse = await _usenetClient
                            .DecodedBodyAsync(segmentId, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (resume is null && attempt == 0)
                        NoteSegmentResponded(segmentIndex);

                    await ThrowOnSegmentIdMismatchAsync(segmentId, bodyResponse).ConfigureAwait(false);
#pragma warning disable CA2000 // stream ownership transfers to the returned SegmentDownloadResult
                    var drained = await ValidateAndDrainSegmentAsync(
#pragma warning restore CA2000
                            bodyResponse.Stream!, segmentIndex, cancellationToken, lease, estimate,
                            incremental
                                ? new IncrementalSegmentHandler(
                                    this, segmentId, segmentIndex, isFirstSegment, attempt, persistent)
                                : null)
                        .ConfigureAwait(false);
                    lease = null;
                    return ToDownloadResult(drained, estimate, segmentId);
                }
                catch (UsenetArticleNotFoundException e)
                {
                    var fallback = await TryFallbackSegmentsAsync(
                            segmentIndex, lease, e, cancellationToken)
                        .ConfigureAwait(false);
                    if (fallback is not null)
                    {
                        lease = null;
                        return ToDownloadResult(fallback.Value, estimate, segmentId);
                    }

                    if (_failFastOnFirstSegment && isFirstSegment)
                    {
                        // ExceptionMiddleware logs an inconclusive miss once as a retryable 503.
                        if (e.InconclusiveReason is null)
                            e.LogWarningKnownOrStack(
                                "First article {SegmentId} missing on all providers at playback start while reading {FileName}. " +
                                "Failing the stream so the player surfaces an error.",
                                segmentId, _fileName);
                        throw;
                    }

                    return ZeroFillSegment(
                        "Article {SegmentId} missing on all providers while reading {FileName}. Filling the {Bytes}-byte gap to preserve later file offsets.",
                        e.SegmentId,
                        segmentIndex,
                        e);
                }
                catch (UsenetCorruptArticleException e) when (!cancellationToken.IsCancellationRequested)
                {
                    persistent.NoteOrThrow(e);
                    if (attempt >= GetCorruptionRetryLimit(segmentId))
                    {
                        var fallback = await TryFallbackSegmentsAsync(
                            segmentIndex, lease, primaryMiss: null, cancellationToken)
                            .ConfigureAwait(false);
                        if (fallback is not null)
                        {
                            lease = null;
                            return ToDownloadResult(fallback.Value, estimate, segmentId);
                        }

                        if (_failFastOnFirstSegment && isFirstSegment)
                        {
                            Par2RepairTriggerSink.ReportCorruption(_fileName, segmentId);
                            e.LogWarningKnownOrStack(
                                "First article {SegmentId} persistently corrupt at playback start while reading {FileName}. " +
                                "Failing the stream so the player surfaces an error.",
                                segmentId, _fileName);
                            throw;
                        }

                        return ZeroFillSegment(
                            "Article {SegmentId} persistently corrupt while reading {FileName}. Filling the {Bytes}-byte gap to preserve later file offsets.",
                            segmentId,
                            segmentIndex,
                            e);
                    }

                    Log.Debug(
                        e,
                        "Corrupt segment {SegmentId} from provider {Provider}; retrying to allow provider failover (attempt {Attempt}).",
                        segmentId,
                        e.ProviderKey,
                        attempt + 1);
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OutOfMemoryException oom)
                {
                    OomDiagnostics.LogHeapStateOnOom(oom, "segment body retry");
                    throw;
                }
                catch (SeekPositionNotFoundException)
                {
                    throw;
                }
                catch (Exception e) when (
                    !cancellationToken.IsCancellationRequested
                    && e is not OutOfMemoryException
                    && e is not PersistentUsenetCorruptionException)
                {
                    if (attempt < MaxBodyRetries)
                    {
                        Log.Debug(e, "Transient failure fetching segment {SegmentId} (attempt {Attempt}). Retrying.",
                            segmentId, attempt + 1);
                        if (MultiProviderNntpClient.CurrentReadSessionId is { } retrySession)
                            StreamTrace.TryRetry(retrySession, segmentId, attempt + 1, e.Message);
                        await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (_failFastOnFirstSegment && isFirstSegment)
                    {
                        e.LogWarningKnownOrStack(
                            "Segment {SegmentId} unavailable at playback start after {Attempts} attempts while reading {FileName}. " +
                            "Failing the stream so the player surfaces an error.",
                            segmentId, attempt + 1, _fileName);
                        throw;
                    }

                    throw CreateTransientSegmentFailure(segmentId, segmentIndex, e);
                }
            }
        }
        finally
        {
            lease?.Dispose();
        }
    }

    /// <summary>
    /// Routes an incremental body through this stream's retry, gap-fill, and readiness
    /// accounting. Recovery uses the buffered path, so the usual retry, fallback, and
    /// zero-fill handling decides the replacement.
    /// </summary>
    private sealed class IncrementalSegmentHandler(
        MultiSegmentStream owner,
        string segmentId,
        int segmentIndex,
        bool isFirstSegment,
        int attempt,
        PersistentCorruptionTracker persistent) : IIncrementalSegmentHandler
    {
        private SegmentDownloadResult? _replacement;

        public async Task<SegmentReplacement> RecoverAsync(Exception failure, CancellationToken cancellationToken)
        {
            // The original lease still covers the segment, so the replacement is not leased again.
            var result = await owner.DownloadSegment(
                    segmentId, segmentIndex, ArticleByteLease.Empty, isFirstSegment, cancellationToken,
                    incremental: false, new SegmentRetryResume(failure, attempt, persistent))
                .ConfigureAwait(false);
            _replacement = result;
            return new SegmentReplacement(result.Stream, result.IsZeroFill || result.IsShortPad, result.Failure);
        }

        public Exception CreatePostDeliveryFailure(Exception failure, int deliveredBytes) =>
            owner.CreatePostDeliveryFailure(segmentId, segmentIndex, failure, deliveredBytes);

        public void OnConsumed(bool degraded)
        {
            owner.ResolveIncrementalReadiness(ready: true);
            owner.AccountSegment(_replacement is { IsZeroFill: true } gapFill
                ? gapFill
                : SegmentDownloadResult.Success(Stream.Null, isShortPad: degraded, segmentId: segmentId));
        }

        public void OnReaderWaited(TimeSpan elapsed)
        {
            owner.ResolveIncrementalReadiness(ready: false);
            StreamTrace.TryStall(
                MultiProviderNntpClient.CurrentStreamTraceRange, StreamStallKind.ConsumerWait, elapsed);
        }
    }

    private sealed record SegmentRetryResume(
        Exception Failure, int Attempt, PersistentCorruptionTracker Persistent);

    private TransientSegmentExhaustionException CreatePostDeliveryFailure(
        string segmentId, int segmentIndex, Exception failure, int deliveredBytes) =>
        new(
            $"Segment {segmentIndex + 1} of {_segmentIds.Length} ({segmentId}) of \"{_fileName}\" " +
            $"failed after {deliveredBytes} bytes were delivered, and its replacement differs from them. " +
            "The client should retry this range request.",
            failure);

    private async Task<SegmentDownloadResult> DownloadKnownMissingSegment(
        string segmentId,
        int segmentIndex,
        ArticleByteLease initialLease,
        bool isFirstSegment,
        CancellationToken cancellationToken)
    {
        var estimate = GetPlannedSegmentBytes(segmentIndex);
        var lease = initialLease;
        try
        {
            ThrowIfPlaybackFailFast();
            var local = await TryGetLocalSegmentAsync(segmentId, segmentIndex, lease, cancellationToken)
                .ConfigureAwait(false);
            if (local is null)
                local = await TryGetLocalFallbackSegmentsAsync(segmentIndex, lease, cancellationToken)
                    .ConfigureAwait(false);
            if (local is not null)
            {
                lease = null;
                return ToDownloadResult(local.Value, estimate, segmentId);
            }

            var missing = new UsenetArticleNotFoundException(segmentId);
            if (_failFastOnFirstSegment && isFirstSegment)
            {
                missing.LogWarningKnownOrStack(
                    "First article {SegmentId} is health-confirmed missing at playback start while reading {FileName}. " +
                    "Failing the stream so the player surfaces an error.",
                    segmentId, _fileName);
                throw missing;
            }
            return ZeroFillSegment(
                "Article {SegmentId} is a health-confirmed missing segment of {FileName}. Filling the {Bytes}-byte gap without a provider request.",
                segmentId,
                segmentIndex,
                missing);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private async Task<DrainedSegment?> TryGetLocalSegmentAsync(
        string segmentId,
        int segmentIndex,
        ArticleByteLease? lease,
        CancellationToken cancellationToken)
    {
        var body = await _usenetClient.TryGetLocalDecodedBodyAsync(segmentId, cancellationToken)
            .ConfigureAwait(false);
        if (body?.Stream is not { } stream) return null;

        try
        {
            await ThrowOnSegmentIdMismatchAsync(segmentId, body).ConfigureAwait(false);
            if (!await SegmentResponseValidator.IsFallbackPartSizeCompatibleAsync(
                    stream, _segmentSizes, segmentIndex, cancellationToken, IsClippedAtFileEnd(segmentIndex))
                    .ConfigureAwait(false))
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            return await ValidateAndDrainSegmentAsync(
                    stream, segmentIndex, cancellationToken, lease, GetPlannedSegmentBytes(segmentIndex))
                .ConfigureAwait(false);
        }
        catch (SeekPositionNotFoundException)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<DrainedSegment?> TryGetLocalFallbackSegmentsAsync(
        int segmentIndex,
        ArticleByteLease? lease,
        CancellationToken cancellationToken)
    {
        foreach (var fallbackId in GetFallbacks(segmentIndex))
        {
            var local = await TryGetLocalSegmentAsync(fallbackId, segmentIndex, lease, cancellationToken)
                .ConfigureAwait(false);
            if (local is not null) return local;
        }

        return null;
    }

    private async Task<SegmentDownloadResult> DownloadBatchSegment(
        Task<UsenetDecodedBodyResponse> responseTask,
        string segmentId,
        int segmentIndex,
        bool isFirstSegment,
        ArticleByteLease initialLease,
        CancellationToken cancellationToken)
    {
        var estimate = GetPlannedSegmentBytes(segmentIndex);
        var lease = initialLease;
        Exception? playbackFailFast = null;
        try
        {
            // The producer issued this batch before the task ran, so the response must
            // always be owned here. A fail-fast check before the await would strand an
            // already-on-the-wire body, and UsenetSharp's pump cannot release the shared
            // batch connection until every handed-out stream is consumed or disposed.
            var response = await responseTask.ConfigureAwait(false);
            NoteSegmentResponded(segmentIndex);
            if (PlaybackHoleTracker.ShouldFailFast(_fileName, out var failFast))
            {
                if (response.Stream is not null)
                {
                    try
                    {
                        await response.Stream.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception disposeError) when (disposeError is not OutOfMemoryException)
                    {
                        Log.Debug(
                            disposeError,
                            "Failed to dispose pipelined BODY stream after playback fail-fast for {FileName}.",
                            _fileName);
                    }
                }

                playbackFailFast = failFast ?? new UsenetArticleNotFoundException(segmentId);
                ExceptionDispatchInfo.Capture(playbackFailFast).Throw();
            }

            await ThrowOnSegmentIdMismatchAsync(segmentId, response).ConfigureAwait(false);
#pragma warning disable CA2000 // stream ownership transfers to the returned SegmentDownloadResult
            var drained = await ValidateAndDrainSegmentAsync(
#pragma warning restore CA2000
                    response.Stream!, segmentIndex, cancellationToken, lease, estimate,
                    GetCorruptionRetryLimit(segmentId) > 0
                        ? new IncrementalSegmentHandler(
                            this, segmentId, segmentIndex, isFirstSegment, 0, new PersistentCorruptionTracker())
                        : null)
                .ConfigureAwait(false);
            lease = null; // owned by BudgetedStream / buffer
            return ToDownloadResult(drained, estimate, segmentId);
        }
        catch (Exception) when (playbackFailFast is not null)
        {
            // A tracker-provided fail-fast bypasses the miss/corruption recovery below:
            // those handlers can issue fallback or rescue requests on a path already
            // declared dead, and a successful fallback would defeat the fail-fast.
            throw;
        }
        catch (UsenetArticleNotFoundException e)
        {
            var fallback = await TryFallbackSegmentsAsync(segmentIndex, lease, e, cancellationToken)
                .ConfigureAwait(false);
            if (fallback is not null)
            {
                lease = null;
                return ToDownloadResult(fallback.Value, estimate, segmentId);
            }

            if (_failFastOnFirstSegment && isFirstSegment) throw;
            return ZeroFillSegment(
                "Article {SegmentId} missing on all providers while reading {FileName}. Filling the {Bytes}-byte gap to preserve later file offsets.",
                e.SegmentId,
                segmentIndex,
                e);
        }
        catch (UsenetCorruptArticleException e) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var retried = await RetryCorruptSegmentAsync(
                        segmentId, segmentIndex, e, lease, cancellationToken)
                    .ConfigureAwait(false);
                lease = null;
                return ToDownloadResult(retried, estimate, segmentId);
            }
            catch (UsenetCorruptArticleException persistent)
            {
                if (_failFastOnFirstSegment && isFirstSegment)
                {
                    Par2RepairTriggerSink.ReportCorruption(_fileName, segmentId);
                    throw;
                }
                return ZeroFillSegment(
                    "Article {SegmentId} persistently corrupt while reading {FileName}. Filling the {Bytes}-byte gap to preserve later file offsets.",
                    segmentId,
                    segmentIndex,
                    persistent);
            }
        }
        catch (OutOfMemoryException oom)
        {
            OomDiagnostics.LogHeapStateOnOom(oom, "pipelined segment batch");
            throw;
        }
        catch (SeekPositionNotFoundException)
        {
            throw;
        }
        catch (Exception e) when (
            !cancellationToken.IsCancellationRequested
            && e is not OutOfMemoryException
            && e is not PersistentUsenetCorruptionException)
        {
            // A failure inside a pipelined batch says nothing about whether the article
            // can be fetched at all: the batch shares one connection, so a stall or a
            // dropped socket takes out unrelated segments with it. Re-request this
            // segment on its own first, which is what gives provider failover and the
            // streaming-timeout retries a chance before any data is degraded.
            DrainedSegment? rescued;
            try
            {
                rescued = await TryRescueSegmentAsync(
                        segmentId, segmentIndex, e, lease, cancellationToken)
                    .ConfigureAwait(false);
                if (rescued is not null)
                    lease = null;
            }
            catch (UsenetArticleNotFoundException notFound)
            {
                // Rescue confirmed the article is genuinely missing — gap-fill
                // instead of treating it as a transient transport failure.
                if (_failFastOnFirstSegment && isFirstSegment) throw;
                return ZeroFillSegment(
                    "Article {SegmentId} missing on all providers while reading {FileName}. Filling the {Bytes}-byte gap to preserve later file offsets.",
                    notFound.SegmentId,
                    segmentIndex,
                    notFound);
            }

            if (rescued is not null)
                return ToDownloadResult(rescued.Value, estimate, segmentId);

            if (_failFastOnFirstSegment && isFirstSegment) throw;
            throw CreateTransientSegmentFailure(segmentId, segmentIndex, e);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    /// <summary>
    /// Re-requests a segment individually after its pipelined response failed. Returns
    /// null once the retries are spent. Throws <see cref="UsenetArticleNotFoundException"/>
    /// if rescue confirms the article is genuinely missing, so the caller can gap-fill
    /// rather than treating it as a transient transport failure.
    /// </summary>
    private async Task<DrainedSegment?> TryRescueSegmentAsync(
        string segmentId,
        int segmentIndex,
        Exception batchFailure,
        ArticleByteLease? existingLease,
        CancellationToken cancellationToken)
    {
        var lease = existingLease;
        var persistent = new PersistentCorruptionTracker();
        if (batchFailure is UsenetCorruptArticleException corruptBatch)
            persistent.NoteOrThrow(corruptBatch);
        try
        {
            for (var attempt = 1; attempt <= MaxBodyRetries; attempt++)
            {
                Log.Debug(
                    batchFailure,
                    "Pipelined segment {SegmentId} failed; re-requesting it individually (attempt {Attempt}).",
                    segmentId, attempt);
                if (MultiProviderNntpClient.CurrentReadSessionId is { } retrySession)
                    StreamTrace.TryRetry(retrySession, segmentId, attempt, batchFailure.Message);

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken)
                        .ConfigureAwait(false);
                    if (lease is null)
                        lease = await LeaseSegmentBytesAsync(
                            GetPlannedSegmentBytes(segmentIndex), cancellationToken).ConfigureAwait(false);

                    UsenetDecodedBodyResponse response;
                    using (FetchAttributionContext.Begin(_fileName))
                    {
                        response = await _usenetClient.DecodedBodyAsync(segmentId, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    await ThrowOnSegmentIdMismatchAsync(segmentId, response).ConfigureAwait(false);
#pragma warning disable CA2000 // stream ownership transfers to the returned SegmentDownloadResult
                    var rescued = await ValidateAndDrainSegmentAsync(
#pragma warning restore CA2000
                        response.Stream!, segmentIndex, cancellationToken, lease, GetPlannedSegmentBytes(segmentIndex))
                        .ConfigureAwait(false);
                    lease = null;
                    return rescued;
                }
                catch (UsenetArticleNotFoundException)
                {
                    throw;
                }
                catch (PersistentUsenetCorruptionException)
                {
                    throw;
                }
                catch (UsenetCorruptArticleException e)
                {
                    persistent.NoteOrThrow(e);
                    Log.Debug(e, "Individual rescue of segment {SegmentId} failed (attempt {Attempt}).",
                        segmentId, attempt);
                }
                catch (OutOfMemoryException oom)
                {
                    OomDiagnostics.LogHeapStateOnOom(oom, "individual segment rescue");
                    throw;
                }
                catch (SeekPositionNotFoundException)
                {
                    throw;
                }
                catch (Exception e) when (
                    !cancellationToken.IsCancellationRequested
                    && e is not OutOfMemoryException
                    && e is not PersistentUsenetCorruptionException)
                {
                    // A non-corrupt rescue failure is swallowed here and the original
                    // batch failure is surfaced as TransientSegmentExhaustionException.
                    Log.Debug(e, "Individual rescue of segment {SegmentId} failed (attempt {Attempt}).",
                        segmentId, attempt);
                }
            }

            return null;
        }
        finally
        {
            if (existingLease is null)
                lease?.Dispose();
        }
    }

    private static Task ThrowOnSegmentIdMismatchAsync(
        string segmentId,
        UsenetDecodedBodyResponse response) =>
        SegmentResponseValidator.ThrowOnSegmentIdMismatchAsync(segmentId, response);

    private async Task<DrainedSegment> RetryCorruptSegmentAsync(
        string segmentId,
        int segmentIndex,
        UsenetCorruptArticleException initialFailure,
        ArticleByteLease? existingLease,
        CancellationToken cancellationToken)
    {
        var failure = initialFailure;
        var lease = existingLease;
        var persistent = new PersistentCorruptionTracker();
        persistent.NoteOrThrow(initialFailure);
        try
        {
            for (var attempt = 1; attempt <= GetCorruptionRetryLimit(segmentId); attempt++)
            {
                Log.Debug(
                    failure,
                    "Corrupt pipelined segment {SegmentId} from provider {Provider}; retrying to allow provider failover (attempt {Attempt}).",
                    segmentId,
                    failure.ProviderKey,
                    attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken)
                    .ConfigureAwait(false);

                try
                {
                    if (lease is null)
                        lease = await LeaseSegmentBytesAsync(
                            GetPlannedSegmentBytes(segmentIndex), cancellationToken).ConfigureAwait(false);

                    UsenetDecodedBodyResponse response;
                    using (FetchAttributionContext.Begin(_fileName))
                    {
                        response = await _usenetClient.DecodedBodyAsync(segmentId, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    await ThrowOnSegmentIdMismatchAsync(segmentId, response).ConfigureAwait(false);
#pragma warning disable CA2000 // stream ownership transfers to the returned SegmentDownloadResult
                    var retried = await ValidateAndDrainSegmentAsync(
#pragma warning restore CA2000
                        response.Stream!, segmentIndex, cancellationToken, lease, GetPlannedSegmentBytes(segmentIndex))
                        .ConfigureAwait(false);
                    lease = null;
                    return retried;
                }
                catch (UsenetCorruptArticleException exception)
                {
                    persistent.NoteOrThrow(exception);
                    failure = exception;
                }
            }

            var fallback = await TryFallbackSegmentsAsync(segmentIndex, lease, primaryMiss: null, cancellationToken)
                .ConfigureAwait(false);
            if (fallback is not null)
            {
                lease = null;
                return fallback.Value;
            }

            ExceptionDispatchInfo.Capture(failure).Throw();
            throw new InvalidOperationException("Unreachable after rethrowing a corrupt segment failure.");
        }
        finally
        {
            lease?.Dispose();
        }
    }

    /// <summary>
    /// Try alternate MessageIds for a missing primary segment. Each BODY
    /// attempt completes its callback exactly once via DecodedBodyAsync.
    /// When <paramref name="existingLease"/> is supplied it is retained across
    /// attempts; on success ownership transfers to the returned stream, on miss
    /// the caller still owns the lease.
    /// An inconclusive alternate miss makes <paramref name="primaryMiss"/> inconclusive too.
    /// </summary>
    private async Task<DrainedSegment?> TryFallbackSegmentsAsync(
        int segmentIndex,
        ArticleByteLease? existingLease,
        UsenetArticleNotFoundException? primaryMiss,
        CancellationToken cancellationToken)
    {
        var fallbacks = GetFallbacks(segmentIndex);
        if (fallbacks.Length == 0) return null;

        var lease = existingLease;
        var ownsLease = existingLease is null;
        SegmentGeometryMismatchException? contradiction = null;
        try
        {
            foreach (var fallbackId in fallbacks)
            {
                try
                {
                    if (lease is null)
                        lease = await LeaseSegmentBytesAsync(
                            GetPlannedSegmentBytes(segmentIndex), cancellationToken).ConfigureAwait(false);

                    UsenetDecodedBodyResponse bodyResponse;
                    using (FetchAttributionContext.Begin(_fileName))
                    {
                        bodyResponse = await _usenetClient
                            .DecodedBodyAsync(fallbackId, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    await ThrowOnSegmentIdMismatchAsync(fallbackId, bodyResponse).ConfigureAwait(false);
                    if (!await SegmentResponseValidator.IsFallbackPartSizeCompatibleAsync(
                            bodyResponse.Stream!, _segmentSizes, segmentIndex, cancellationToken,
                            IsClippedAtFileEnd(segmentIndex))
                        .ConfigureAwait(false))
                    {
                        Log.Debug(
                            "Fallback MessageId {FallbackId} for segment {PrimaryIndex} of {FileName} has a mismatched yEnc part size; skipping.",
                            fallbackId, segmentIndex, _fileName);
                        if (_recordedSizesInferred)
                        {
                            contradiction ??= await SegmentResponseValidator.GetRecordedSizeContradictionAsync(
                                    bodyResponse.Stream!, _segmentSizes, segmentIndex, _fileName, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        await bodyResponse.Stream!.DisposeAsync().ConfigureAwait(false);
                        continue;
                    }
                    Log.Debug(
                        "Segment {PrimaryIndex} recovered via fallback MessageId {FallbackId} while reading {FileName}.",
                        segmentIndex, fallbackId, _fileName);
#pragma warning disable CA2000 // stream ownership transfers to the returned SegmentDownloadResult
                    var drained = await ValidateAndDrainSegmentAsync(
#pragma warning restore CA2000
                        bodyResponse.Stream!, segmentIndex, cancellationToken, lease, GetPlannedSegmentBytes(segmentIndex))
                        .ConfigureAwait(false);
                    lease = null;
                    return drained;
                }
                catch (UsenetArticleNotFoundException alternateMiss)
                {
                    // An alternate that could not be confirmed missing leaves the segment unconfirmed.
                    if (primaryMiss is not null)
                        primaryMiss.InconclusiveReason ??= alternateMiss.InconclusiveReason;
                }
                catch (UsenetCorruptArticleException)
                {
                    // Corrupt fallback — try the next alternate MessageId.
                }
                catch (SeekPositionNotFoundException)
                {
                    // The positioned fallback had incompatible BODY geometry — try the next alternate MessageId.
                }
                catch (UsenetUnexpectedResponseException e)
                {
                    Log.Debug(e, "Fallback MessageId {FallbackId} returned another article.", fallbackId);
                }
            }

            // Zero-filling at the recorded size would emit bytes later recovery cannot retract.
            if (contradiction is not null) throw contradiction;
            return null;
        }
        finally
        {
            if (ownsLease)
                lease?.Dispose();
        }
    }

    private string[] GetFallbacks(int segmentIndex)
    {
        if (_segmentFallbacks is null ||
            segmentIndex < 0 ||
            segmentIndex >= _segmentFallbacks.Length)
            return [];

        return _segmentFallbacks[segmentIndex] ?? [];
    }

    private async Task DisposeStreamAsync(
        Task<SegmentDownloadResult> streamTask,
        bool releaseInFlight = false)
    {
        try
        {
            var result = await streamTask.ConfigureAwait(false);
            await using var stream = result.Stream;
            if (releaseInFlight)
                ReleaseInFlightPrefetchBytes(result.PlannedBytes);
        }
        catch
        {
            // The producer owns reporting download failures.
        }
    }

    /// <summary>
    /// Substitutes a bounded gap for a segment that could not be downloaded, but only for
    /// a known fill length. Every byte after this segment is positioned by how many bytes
    /// it contributes, so a wrong length corrupts the rest of the file instead of just
    /// the part that failed — better to fail the read and let the player retry or report it.
    /// </summary>
    private SegmentDownloadResult ZeroFillSegment(
        string messageTemplate,
        string segmentId,
        int segmentIndex,
        Exception exception)
    {
        // A hedge already delivered this segment; a late failure of the original is not a hole.
        if (_supersededSegments?.ContainsKey(segmentIndex) == true)
            ExceptionDispatchInfo.Capture(exception).Throw();

        if (!_segmentSizes.TryGetFillLength(segmentIndex, out var fill, out var isExact))
        {
            if (exception.TryGetCausingException(out UsenetCorruptArticleException? _))
                Par2RepairTriggerSink.ReportCorruption(_fileName, segmentId);
            throw CreateUnknownLengthFailure(segmentId, segmentIndex, exception);
        }

        if (!isExact)
        {
            Log.Debug(
                "Using the observed {Bytes}-byte segment size of {FileName} to replace failed segment {SegmentId}.",
                fill, _fileName, segmentId);
        }

        // An inconclusive miss fills this read only: no PAR2 zero-fill report, and the tracker
        // does not remember the segment as missing.
        var inconclusive = exception.IsInconclusiveArticleMiss();
        if (exception.TryGetCausingException(out UsenetCorruptArticleException? _))
            Par2RepairTriggerSink.ReportCorruption(_fileName, segmentId);
        else if (!inconclusive)
            Par2RepairTriggerSink.Current?.ReportZeroFill(_fileName, segmentId, segmentIndex, fill);

        PlaybackHoleTracker.RecordHole(_fileName, segmentId, exception);

#pragma warning disable CA2000 // gap-fill stream ownership transfers to the returned SegmentDownloadResult
        return SegmentDownloadResult.ZeroFill(
            CreateGapFillStream(fill, segmentIndex),
            inconclusive ? InconclusiveGapFillTemplate : messageTemplate,
            segmentId,
            fill,
            exception,
            GetPlannedSegmentBytes(segmentIndex));
#pragma warning restore CA2000
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

    private Exception CreateUnknownLengthFailure(string segmentId, int segmentIndex, Exception failure)
    {
        var message =
            $"Segment {segmentIndex + 1} of {_segmentIds.Length} ({segmentId}) could not be downloaded " +
            $"while reading \"{_fileName}\", and its exact length is unknown, so the rest of the file " +
            "cannot be delivered at the right offsets. Repair the item to restore its segment sizes.";
        return failure.IsNonRetryableDownloadException()
            ? new NonRetryableDownloadException(message, failure)
            : new RetryableDownloadException(message, failure);
    }

    private TransientSegmentExhaustionException CreateTransientSegmentFailure(
        string segmentId, int segmentIndex, Exception failure)
    {
        var message =
            $"Segment {segmentIndex + 1} of {_segmentIds.Length} ({segmentId}) could not be downloaded " +
            $"while reading \"{_fileName}\" after all retry attempts were exhausted. " +
            "The client should retry this range request.";
        return new TransientSegmentExhaustionException(message, failure);
    }

    private bool IsClippedAtFileEnd(int segmentIndex) =>
        segmentIndex == 0 && _expectedFirstSegmentRangeWasClippedAtFileEnd;

    private async Task<DrainedSegment> ValidateAndDrainSegmentAsync(
        Stream source,
        int segmentIndex,
        CancellationToken cancellationToken,
        ArticleByteLease? existingLease = null,
        long? leasedEstimate = null,
        IIncrementalSegmentHandler? incremental = null)
    {
        try
        {
            if (!await MatchesPositioningGeometryAsync(
                    source, segmentIndex, cancellationToken).ConfigureAwait(false))
            {
                throw new SeekPositionNotFoundException(
                    $"BODY geometry for segment {segmentIndex} of {_fileName} does not match " +
                    $"the expected positioning range {_expectedFirstSegmentRange}.");
            }

            // A clipped final segment's size is its in-file length, so its full yEnc part cannot match.
            if (!IsClippedAtFileEnd(segmentIndex))
            {
                await SegmentResponseValidator.ThrowOnRecordedSizeMismatchAsync(
                    source, _segmentSizes, segmentIndex, _fileName, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            try
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.Debug(e, "Failed to dispose a BODY stream after positioning validation failed.");
            }
            throw;
        }

        return await DrainSegmentAsync(
                source, segmentIndex, cancellationToken, existingLease, leasedEstimate, incremental)
            .ConfigureAwait(false);
    }

    private async Task<bool> MatchesPositioningGeometryAsync(
        Stream stream,
        int segmentIndex,
        CancellationToken cancellationToken)
    {
        if (segmentIndex != 0 || _expectedFirstSegmentRange is not { } expected)
            return true;
        if (stream is not YencStream yenc)
            return false;
        var header = await yenc.GetYencHeadersAsync(cancellationToken).ConfigureAwait(false);
        if (header is null)
            return false;
        var actual = new LongRange(header.PartOffset, header.PartOffset + header.PartSize);
        if (_expectedFirstSegmentRangeWasClippedAtFileEnd && actual.EndExclusive > expected.EndExclusive)
            actual = new LongRange(actual.StartInclusive, expected.EndExclusive);
        return actual == expected;
    }

    private async Task<DrainedSegment> DrainSegmentAsync(
        Stream source,
        int segmentIndex,
        CancellationToken cancellationToken,
        ArticleByteLease? existingLease = null,
        long? leasedEstimate = null,
        IIncrementalSegmentHandler? incremental = null)
    {
        ArticleByteLease? lease = existingLease;
        var ownsLease = existingLease is null;
        PooledBufferStream? buffer = null;
        var sourceDisposeAttempted = false;
        try
        {
            var hasExactSize = _segmentSizes.TryGetExactSize(segmentIndex, out var exactSize);
            var expected = hasExactSize ? exactSize : _estimatedSegmentSize;
            var estimate = leasedEstimate
                ?? (expected is > 0 and <= int.MaxValue ? expected : _estimatedSegmentSize);
            if (estimate < 0) estimate = 0;

            // Never lease while holding an open body pipe: a waiter in LeaseAsync must
            // not hold pipe bytes. All production call sites lease before issuing the
            // BODY request; this branch is defensive and currently unreachable. Future
            // callers must also lease before BODY, not here after the pipe exists.
            if (lease is null)
                lease = await LeaseSegmentBytesAsync(estimate, cancellationToken).ConfigureAwait(false);

            var capacity = await ResolveDrainCapacityHintAsync(
                source, segmentIndex, estimate, cancellationToken).ConfigureAwait(false);
            // A clipped first segment is realigned against its positioning range after the
            // drain, so it keeps the buffered path.
            if (incremental is not null
                && !(segmentIndex == 0 && _expectedFirstSegmentRangeWasClippedAtFileEnd))
            {
                var incrementalLease = lease;
                var incrementalTrace = MultiProviderNntpClient.CurrentStreamTraceRange;
                var incrementalStarted = Stopwatch.GetTimestamp();
                var incrementalSegmentId = _segmentIds.Span[segmentIndex];
                var incrementalStream = new IncrementalSegmentStream(
                    source,
                    capacity,
                    hasExactSize ? exactSize : -1,
                    incremental,
                    (drainedBytes, length) =>
                    {
                        StreamTrace.TryStall(
                            incrementalTrace,
                            StreamStallKind.BodyDrain,
                            Stopwatch.GetElapsedTime(incrementalStarted));
                        if (!hasExactSize)
                            _segmentSizes.RecordObservedSize(segmentIndex, drainedBytes);
                        else if (drainedBytes < exactSize)
                            SegmentHoleReporter.ReportShortDecode(
                                _fileName, incrementalSegmentId, segmentIndex, exactSize - drainedBytes);
                        if (length != estimate)
                            incrementalLease.Adjust(length - estimate);
                    },
                    cancellationToken);
                sourceDisposeAttempted = true;
                ownsLease = false;
                return new DrainedSegment(
                    ReferenceEquals(lease, ArticleByteLease.Empty)
                        ? incrementalStream
                        : new BudgetedStream(incrementalStream, lease),
                    false,
                    Incremental: true);
            }

            buffer = new PooledBufferStream(capacity);
            var traceRange = MultiProviderNntpClient.CurrentStreamTraceRange;
            var drainStarted = Stopwatch.GetTimestamp();
            await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            StreamTrace.TryStall(
                traceRange,
                StreamStallKind.BodyDrain,
                Stopwatch.GetElapsedTime(drainStarted));
            var drained = buffer.Length;
            var shortPadded = false;
            if (segmentIndex == 0
                && _expectedFirstSegmentRangeWasClippedAtFileEnd
                && _expectedFirstSegmentRange is { } expectedFirstSegmentRange)
            {
                shortPadded = AlignDrainedSegment(
                    buffer, segmentIndex, drained, expectedFirstSegmentRange.Count);
                if (!hasExactSize)
                    _segmentSizes.RecordObservedSize(segmentIndex, buffer.Length);
            }
            else if (hasExactSize)
            {
                shortPadded = AlignDrainedSegment(buffer, segmentIndex, drained, exactSize);
            }
            else
                _segmentSizes.RecordObservedSize(segmentIndex, drained);

            var actual = buffer.Length;
            buffer.Position = 0;
            // Keep the buffer and any internally acquired lease locally owned until
            // source disposal succeeds. A disposal failure must not strand either.
            sourceDisposeAttempted = true;
            await source.DisposeAsync().ConfigureAwait(false);
            if (actual != estimate)
                lease.Adjust(actual - estimate);
            // Build the wrapper that takes over the buffer and lease before dropping
            // local ownership, so a failure here still routes both through the catch.
            var result = ReferenceEquals(lease, ArticleByteLease.Empty)
                ? (Stream)buffer
                : new BudgetedStream(buffer, lease);
            ownsLease = false;
            buffer = null;
            return new DrainedSegment(result, shortPadded);
        }
        catch
        {
            if (buffer is not null)
                await buffer.DisposeAsync().ConfigureAwait(false);
            if (ownsLease)
                lease?.Dispose();
            throw;
        }
        finally
        {
            if (!sourceDisposeAttempted)
                await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Uses imported ranges first, then the body's exact decoded yEnc part size, and leaves
    /// the file average as a fallback only. This chooses a rent hint; it never controls output.
    /// </summary>
    private async ValueTask<int> ResolveDrainCapacityHintAsync(
        Stream source,
        int segmentIndex,
        long estimate,
        CancellationToken cancellationToken)
    {
        if (_segmentSizes.TryGetExactSize(segmentIndex, out var exact))
            return ToCapacity(exact);

        if (estimate <= 0 || estimate > Array.MaxLength)
            return 0;

        if (source is not YencStream yencSource)
            return ToCapacity(estimate);

        UsenetYencHeader? header;
        try
        {
            header = await yencSource.GetYencHeadersAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            // Header parse failed on a body that still may decode via ReadAsync (test fakes
            // and some nonstandard streams). Keep the estimate; do not swallow corrupt-article
            // or other download failures — those are not thrown from GetYencHeadersAsync here.
            return ToCapacity(estimate);
        }

        if (header is not null
            && header.PartSize > 0
            && header.PartSize <= Array.MaxLength
            && IsPlausiblePartSize(
                header.PartSize, header.TotalParts, _segmentIds.Length, estimate))
            return (int)header.PartSize;

        return ToCapacity(estimate);
    }

    internal static int ToCapacity(long value) =>
        value > 0 && value <= Array.MaxLength ? (int)value : 0;

    /// <summary>
    /// Rejects remote yEnc PartSize values that cannot be a full-part size for this file
    /// average, so a malformed header cannot request an arbitrary multi-gigabyte rent.
    /// </summary>
    internal static bool IsPlausiblePartSize(
        long partSize, int totalParts, int remainingParts, long estimate)
    {
        if (partSize <= 0 || estimate <= 0) return false;
        if (totalParts < remainingParts) return false;
        if (totalParts <= 1) return partSize <= estimate;
        // EstimatedSegmentSize is floor(fileSize / totalParts), so add one before deriving
        // the strict upper bound to cover the discarded integer-division remainder.
        var upperBound = Math.Ceiling((estimate + 1d) * totalParts / (totalParts - 1));
        return partSize <= upperBound;
    }

    private async ValueTask<ArticleByteLease> LeaseSegmentBytesAsync(
        long estimate,
        CancellationToken cancellationToken)
    {
        if (_budget is null || estimate <= 0)
            return ArticleByteLease.Empty;
        return await _budget.LeaseAsync(estimate, cancellationToken).ConfigureAwait(false);
    }

    /// <returns>True when the body was short and padded to the recorded length.</returns>
    private bool AlignDrainedSegment(PooledBufferStream buffer, int segmentIndex, long drained, long expected)
    {
        if (drained == expected) return false;

        if (drained > expected)
        {
            Log.Debug(
                "Segment {SegmentIndex} of {FileName} decoded {Drained} bytes but was recorded as {Expected}. Truncating to keep offsets aligned.",
                segmentIndex, _fileName, drained, expected);
            buffer.SetLength(expected);
            return false;
        }

        var shortfall = expected - drained;
        var segmentId = _segmentIds.Span[segmentIndex];
        SegmentHoleReporter.ReportShortDecode(_fileName, segmentId, segmentIndex, shortfall);
        buffer.SetLength(expected);
        return true;
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
                // Time spent here is the consumer starving: prefetch has not yet delivered
                // the next segment. Low provider time with high consumer wait means the
                // pipeline is not running far enough ahead, not that the provider is slow.
                var traceRange = MultiProviderNntpClient.CurrentStreamTraceRange;
                var waitStarted = Stopwatch.GetTimestamp();
                var wasQueued = _streamTasks.Reader.TryRead(out var streamTask);
                if (!wasQueued)
                {
                    if (!await _streamTasks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return 0;
                    if (!_streamTasks.Reader.TryRead(out streamTask)) return 0;
                }

                // Ready means prefetch stayed ahead; use IsCompleted (not Successfully) so
                // faulted tasks still count as present when the consumer arrived.
                var nextSegment = streamTask
                    ?? throw new InvalidOperationException("Segment channel returned a null task.");
                var headIndex = _nextHeadIndex++;
                var readyWhenNeeded = wasQueued && nextSegment.IsCompleted;
                // Test hook: fires after readiness is sampled and before the segment task is awaited,
                // so lockstep tests can keep the gate closed until starvation is observed.
                TestOnSegmentReadiness?.Invoke(readyWhenNeeded);
                var result = nextSegment.IsCompleted
                    ? await nextSegment.ConfigureAwait(false)
                    : await AwaitHeadSegmentAsync(nextSegment, headIndex).ConfigureAwait(false);
                StreamTrace.TryStall(
                    traceRange,
                    StreamStallKind.ConsumerWait,
                    Stopwatch.GetElapsedTime(waitStarted));
                Interlocked.Add(ref _consumedPrefetchBytes, result.PlannedBytes);
                ReleaseInFlightPrefetchBytes(result.PlannedBytes);
                // Ignore the first delivered segment (startup warm-up).
                var observeReadiness = _deliveredSegments++ > 0;
                // An incremental segment is only ready if its body bytes also arrive before
                // they are read; body-read waits or the segment's end resolve it.
                _incrementalReadinessPending = observeReadiness && readyWhenNeeded && result.IsIncremental;
                if (observeReadiness && !_incrementalReadinessPending)
                    ObserveBatchReadiness(readyWhenNeeded);
                _stream = AcceptSegment(result);
            }

            // read from the stream
            var read = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0) return read;

            // if the stream ended, continue to the next stream.
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }
    }

    private void NoteSegmentIssued(int segmentIndex) =>
        Volatile.Write(ref _segmentIssuedAt[segmentIndex % _segmentIssuedAt.Length], Stopwatch.GetTimestamp());

    private void NoteSegmentResponded(int segmentIndex)
    {
        // Zero marks the segment as answered: bytes are flowing, so a duplicate would only double them.
        var issuedAt = Interlocked.Exchange(ref _segmentIssuedAt[segmentIndex % _segmentIssuedAt.Length], 0);
        if (issuedAt == 0) return;
        var sample = (uint)(Interlocked.Increment(ref _responseLatencyCount) - 1) % ResponseLatencySamples;
        Volatile.Write(ref _responseLatencyTicks[sample], Stopwatch.GetElapsedTime(issuedAt).Ticks);
        int seen;
        while ((seen = Volatile.Read(ref _highestRespondedIndex)) < segmentIndex &&
               Interlocked.CompareExchange(ref _highestRespondedIndex, segmentIndex, seen) != seen)
        {
        }
    }

    private TimeSpan GetHedgeDelay()
    {
        var count = (int)Math.Min((uint)Volatile.Read(ref _responseLatencyCount), ResponseLatencySamples);
        if (count == 0) return HedgeFloor;
        Span<long> samples = stackalloc long[ResponseLatencySamples];
        for (var index = 0; index < count; index++)
            samples[index] = Volatile.Read(ref _responseLatencyTicks[index]);
        samples[..count].Sort();
        var hedgeDelay = TimeSpan.FromTicks(samples[count / 2] * HedgeLatencyMultiplier);
        return hedgeDelay > HedgeFloor ? hedgeDelay : HedgeFloor;
    }

    /// <summary>
    /// Waits for the head segment; once it has had no server response for the hedge delay while a
    /// later segment has already answered, races one duplicate fetch and keeps whichever finishes first.
    /// </summary>
    private async Task<SegmentDownloadResult> AwaitHeadSegmentAsync(
        Task<SegmentDownloadResult> head,
        int headIndex)
    {
        if (_knownMissingSegmentIndices?.Contains(headIndex) == true ||
            DownloadWorkloadClassifier.Classify(_cts.Token) != DownloadWorkload.Streaming)
        {
            return await head.ConfigureAwait(false);
        }

        // Time the reader's own wait: read-ahead segments are issued long before a paced reader needs them.
        var waitStarted = Stopwatch.GetTimestamp();
        var hedgeDelay = GetHedgeDelay();
        while (!head.IsCompleted)
        {
            var issuedAt = Volatile.Read(ref _segmentIssuedAt[headIndex % _segmentIssuedAt.Length]);
            if (issuedAt == 0 || _cts.IsCancellationRequested) return await head.ConfigureAwait(false);
            var remaining = hedgeDelay - Stopwatch.GetElapsedTime(waitStarted);
            if (remaining <= TimeSpan.Zero && Volatile.Read(ref _highestRespondedIndex) > headIndex)
                break;
            await Task.WhenAny(head, Task.Delay(remaining > HedgePollInterval ? remaining : HedgePollInterval))
                .ConfigureAwait(false);
        }

        if (head.IsCompleted) return await head.ConfigureAwait(false);

#pragma warning disable CA2000 // disposed by the continuation once the hedge settles
        var hedgeCts = ContextualCancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
#pragma warning restore CA2000
        var hedge = HedgeSegmentAsync(headIndex, hedgeCts.Token);
        _ = hedge.ContinueWith(
            static (_, state) => ((ContextualCancellationTokenSource)state!).Dispose(),
            hedgeCts,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        Log.Debug(
            "Segment {SegmentIndex} of {FileName} unanswered after a {ElapsedMs} ms read wait; racing a duplicate fetch.",
            headIndex, _fileName, (long)Stopwatch.GetElapsedTime(waitStarted).TotalMilliseconds);

        var first = await Task.WhenAny(head, hedge).ConfigureAwait(false);
        if (first == hedge && hedge.IsCompletedSuccessfully)
        {
            LazyInitializer.EnsureInitialized(ref _supersededSegments).TryAdd(headIndex, 0);
            // The original shares its connection with other batched articles, so it is not
            // cancelled; its body is drained or discarded on arrival without a provider failure.
            _orphanedDisposals.Enqueue(DisposeStreamAsync(head));
            Log.Debug("Duplicate fetch won segment {SegmentIndex} of {FileName}.", headIndex, _fileName);
            return await hedge.ConfigureAwait(false);
        }

        if (!hedge.IsCompleted)
        {
            try
            {
                // Caller cancellation releases the loser's connection without penalizing its provider.
                await hedgeCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The hedge completed and released its token source.
            }
        }

        _orphanedDisposals.Enqueue(DisposeStreamAsync(hedge));
        return await head.ConfigureAwait(false);
    }

    private async Task<SegmentDownloadResult> HedgeSegmentAsync(int segmentIndex, CancellationToken cancellationToken)
    {
        var segmentId = _segmentIds.Span[segmentIndex];
        var estimate = GetPlannedSegmentBytes(segmentIndex);
        UsenetDecodedBodyResponse response;
        using (FetchAttributionContext.Begin(_fileName))
        using (MultiProviderNntpClient.BeginHedgeFetchScope())
        {
            response = await _usenetClient.DecodedBodyAsync(segmentId, cancellationToken).ConfigureAwait(false);
        }

        await ThrowOnSegmentIdMismatchAsync(segmentId, response).ConfigureAwait(false);
        // ponytail: the hedge body is unbudgeted (at most one article per stalled reader); lease it if memory pressure shows up.
#pragma warning disable CA2000 // stream ownership transfers to the returned SegmentDownloadResult
        var drained = await ValidateAndDrainSegmentAsync(
#pragma warning restore CA2000
                response.Stream!, segmentIndex, cancellationToken, ArticleByteLease.Empty, estimate)
            .ConfigureAwait(false);
        return ToDownloadResult(drained, estimate, segmentId);
    }

    private void ObserveBatchReadiness(bool readyWhenNeeded)
    {
        if (_batchSizer is null) return;
        var change = _batchSizer.Observe(readyWhenNeeded);
        if (change is null) return;

        Log.Debug(
            "Prefetch batch size for {FileName} changed from {PreviousBatchSize} to {BatchSize}. " +
            "ReadyWhenNeeded={ReadyWhenNeeded}",
            _fileName, change.Value.Previous, change.Value.Current, change.Value.ReadyWhenNeeded);

        if (MultiProviderNntpClient.CurrentReadSessionId is { } sessionId)
            StreamTrace.TryPrefetchWidth(sessionId, change.Value.Previous, change.Value.Current);
    }

    private void ResolveIncrementalReadiness(bool ready)
    {
        if (!_incrementalReadinessPending) return;
        _incrementalReadinessPending = false;
        ObserveBatchReadiness(ready);
    }

    private Stream AcceptSegment(SegmentDownloadResult result)
    {
        // Incremental segments are accounted when the reader reaches their outcome.
        if (result.IsIncremental) return result.Stream;
        try
        {
            AccountSegment(result);
        }
        catch
        {
            result.Stream.Dispose();
            throw;
        }

        return result.Stream;
    }

    private void AccountSegment(SegmentDownloadResult result)
    {
        if (!result.IsZeroFill)
        {
            if (result.IsShortPad)
            {
                _consecutiveZeroFills++;
                if (_consecutiveZeroFills < PlaybackHoleTracker.ConsecutiveFillLimit(_fileName)
                    && !PlaybackHoleTracker.ShouldFailFast(_fileName, out _))
                    return;

                _cts.Cancel();
                if (PlaybackHoleTracker.ShouldFailFast(_fileName, out var failFast)
                    && failFast is not null)
                {
                    ExceptionDispatchInfo.Capture(failFast).Throw();
                }

                throw new UsenetArticleNotFoundException(result.SegmentId ?? _segmentIds.Span[0]);
            }

            _consecutiveZeroFills = 0;
            PlaybackHoleTracker.RecordGoodSegment(_fileName);
            return;
        }

        _consecutiveZeroFills++;
        ZeroFillLogLimiter.Write(
            result.MessageTemplate!,
            result.SegmentId!,
            _fileName,
            result.Bytes,
            result.Failure);
        if (MultiProviderNntpClient.CurrentReadSessionId is { } sessionId)
            StreamTrace.TryZeroFill(sessionId, result.SegmentId!, result.Bytes);

        if (_consecutiveZeroFills < PlaybackHoleTracker.ConsecutiveFillLimit(_fileName)
            && !PlaybackHoleTracker.ShouldFailFast(_fileName, out _))
            return;

        _cts.Cancel();
        if (PlaybackHoleTracker.ShouldFailFast(_fileName, out var retained) && retained is not null)
            ExceptionDispatchInfo.Capture(retained).Throw();
        ExceptionDispatchInfo.Capture(result.Failure!).Throw();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;
        // Sync Dispose must stay non-blocking (Seek calls it). Start the same
        // idempotent cleanup that DisposeAsync awaits for lease release.
        _ = EnsureDisposeAsync();
        // Must be the protected overload: the parameterless Stream.Dispose() routes
        // back through Close() into this method and recurses until the stack overflows.
        base.Dispose(disposing);
    }

#pragma warning disable CA2215 // base.DisposeAsync() would route through Close()/Dispose(true) back into EnsureDisposeAsync's sync-over-async teardown; the _disposeGate/_disposeTask pair already guarantees exactly-once cleanup (see Dispose(bool) recursion note)
    public override async ValueTask DisposeAsync()
    {
        await EnsureDisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
#pragma warning restore CA2215

    private Task EnsureDisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_disposeTask is not null) return _disposeTask;
            // Mark disposed before async cleanup so sync Dispose immediately rejects reads.
            _disposed = true;
            _disposeTask = DisposeCoreAsync();
            return _disposeTask;
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            try
            {
#pragma warning disable CA1849 // synchronous Cancel is required -- teardown callbacks must run before _streamTasks.Writer completes; CancelAsync would race TryComplete
                _cts.Cancel();
#pragma warning restore CA1849
            }
            catch (ObjectDisposedException)
            {
                // Already torn down.
            }

            _streamTasks.Writer.TryComplete();

            if (_stream is not null)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
                _stream = null;
            }

            // Drain queued segments concurrently so a task blocked on LeaseAsync can
            // wake when another queued BudgetedStream releases its lease.
            var pending = new List<Task>();
            while (_streamTasks.Reader.TryRead(out var streamTask))
                pending.Add(DisposeStreamAsync(streamTask, releaseInFlight: true));

            try
            {
                await _downloadTask.ConfigureAwait(false);
            }
            catch
            {
                // Producer failures are surfaced on ReadAsync; teardown only needs cleanup.
            }

            while (_streamTasks.Reader.TryRead(out var streamTask))
                pending.Add(DisposeStreamAsync(streamTask, releaseInFlight: true));

            // Join the producer's out-of-band disposals so leases they hold are released
            // before DisposeAsync completes. The producer has exited by now (awaited above),
            // so no new orphans can be enqueued; drain is safe without a lock.
            while (_orphanedDisposals.TryDequeue(out var orphaned))
                pending.Add(orphaned);

            while (_batchCompletionObservers.TryDequeue(out var observer))
                pending.Add(observer);

            if (pending.Count > 0)
                await Task.WhenAll(pending).ConfigureAwait(false);
        }
        finally
        {
            _cts.Dispose();
        }
    }

    private readonly record struct DrainedSegment(Stream Stream, bool ShortPadded, bool Incremental = false);

    private sealed record SegmentDownloadResult(
        Stream Stream,
        long PlannedBytes = 0,
        string? MessageTemplate = null,
        string? SegmentId = null,
        long Bytes = 0,
        Exception? Failure = null,
        bool IsShortPad = false,
        bool IsIncremental = false)
    {
        public bool IsZeroFill => Failure is not null;

        public static SegmentDownloadResult Success(
            Stream stream,
            long plannedBytes = 0,
            bool isShortPad = false,
            string? segmentId = null,
            bool isIncremental = false) =>
            new(stream, plannedBytes, SegmentId: segmentId, IsShortPad: isShortPad, IsIncremental: isIncremental);

        public static SegmentDownloadResult ZeroFill(
            Stream stream,
            string messageTemplate,
            string segmentId,
            long bytes,
            Exception failure,
            long plannedBytes = 0) =>
            new(stream, plannedBytes, messageTemplate, segmentId, bytes, failure);
    }
}
