namespace NzbWebDAV.Streams;

/// <summary>
/// Immutable construction-time seed for a finite-range buffered BODY pipeline.
/// This is a scheduling hint only; live admission remains authoritative.
/// </summary>
internal readonly record struct InitialBodyBatchPlan(
    int PlannedSegmentCount,
    long ExactPlannedSegmentBytes,
    int InitialBatchWidth,
    int ConfiguredMaximumBatchWidth,
    int EffectiveConnectionTarget,
    int WideningNotBeforeDeliveredSegment,
    InitialBodyBatchPlanReason Reason)
{
    internal static InitialBodyBatchPlan Create(
        int plannedSegmentCount,
        long exactPlannedSegmentBytes,
        int effectiveConnectionTarget,
        int configuredMaximumBatchWidth,
        int articleBufferSize,
        InitialBodyBatchPlanReason reason = InitialBodyBatchPlanReason.ExactFiniteRange)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(plannedSegmentCount, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(exactPlannedSegmentBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(effectiveConnectionTarget, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(configuredMaximumBatchWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(articleBufferSize, 1);

        var effectiveMaximum = Math.Min(
            configuredMaximumBatchWidth,
            Math.Min(articleBufferSize, plannedSegmentCount));
        var initialWidth = SelectInitialBatchWidth(
            plannedSegmentCount,
            effectiveConnectionTarget,
            effectiveMaximum);

        return new InitialBodyBatchPlan(
            plannedSegmentCount,
            exactPlannedSegmentBytes,
            initialWidth,
            effectiveMaximum,
            effectiveConnectionTarget,
            CalculateWideningObservationFloor(plannedSegmentCount, effectiveConnectionTarget),
            reason);
    }

    internal static int SelectInitialBatchWidth(
        int remainingSegments,
        int targetConnections,
        int configuredMaximum)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(remainingSegments, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetConnections, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(configuredMaximum, 1);

        var desiredBatches = Math.Min(remainingSegments, targetConnections);
        if (desiredBatches == 1)
            return Math.Min(remainingSegments, configuredMaximum);

        var widestWidthThatStillCreatesDesiredBatches =
            (remainingSegments - 1) / (desiredBatches - 1);
        return Math.Min(
            configuredMaximum,
            Math.Max(1, widestWidthThatStillCreatesDesiredBatches));
    }

    internal static int CountBatches(int segmentCount, int width)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(segmentCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        return 1 + (segmentCount - 1) / width;
    }

    internal static int CalculateWideningObservationFloor(
        int plannedSegmentCount,
        int effectiveConnectionTarget)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(plannedSegmentCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(effectiveConnectionTarget, 1);

        var twoWaves = effectiveConnectionTarget > int.MaxValue / 2
            ? int.MaxValue
            : effectiveConnectionTarget * 2;
        return Math.Min(plannedSegmentCount, twoWaves);
    }
}

internal enum InitialBodyBatchPlanReason
{
    ExactFiniteRange,
    DegradedNoHealthyPrimaryCapacity,
}
