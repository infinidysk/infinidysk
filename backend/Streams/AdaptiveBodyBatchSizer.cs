using System.Numerics;

namespace NzbWebDAV.Streams;

/// <summary>
/// Adapts BODY pipeline batch width from consumer readiness at segment boundaries.
/// Narrows 4→2→1 when prefetch starves; recovers gradually after sustained readiness.
/// </summary>
internal sealed class AdaptiveBodyBatchSizer
{
    private const int ObservationWindow = 8;
    private const int StarvedBoundariesToNarrow = 2;
    private const int ReadyBoundariesToRecover = 16;

    internal const int RewidenHoldMilliseconds = 250;
    private static readonly TimeSpan RewidenHold = TimeSpan.FromMilliseconds(RewidenHoldMilliseconds);

    private readonly int _maximum;
    private readonly int _wideningObservationFloor;
    private readonly TimeProvider _timeProvider;
    private int _current;
    private byte _starvationWindow;
    private int _observations;
    private int _totalObservations;
    private int _ready;
    private DateTimeOffset _lastTransition;

    internal AdaptiveBodyBatchSizer(int maximumBatchSize, TimeProvider? timeProvider = null)
        : this(maximumBatchSize, maximumBatchSize, wideningObservationFloor: 0, timeProvider)
    {
    }

    internal AdaptiveBodyBatchSizer(
        int maximumBatchSize,
        int initialBatchSize,
        int wideningObservationFloor,
        TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(initialBatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(initialBatchSize, maximumBatchSize);
        ArgumentOutOfRangeException.ThrowIfNegative(wideningObservationFloor);

        _maximum = maximumBatchSize;
        _current = initialBatchSize;
        _wideningObservationFloor = wideningObservationFloor;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int Current => Volatile.Read(ref _current);

    public BatchSizeChange? Observe(bool readyWhenNeeded)
    {
        var current = Current;
        _starvationWindow = (byte)((_starvationWindow << 1) | (readyWhenNeeded ? 0 : 1));
        _observations = Math.Min(ObservationWindow, _observations + 1);
        if (_totalObservations < int.MaxValue)
            _totalObservations++;

        if (readyWhenNeeded)
            _ready++;
        else
            _ready = 0;

        int? next = null;
        var isWiden = false;
        if (_observations == ObservationWindow
            && BitOperations.PopCount(_starvationWindow) >= StarvedBoundariesToNarrow)
        {
            next = Math.Max(1, (current + 1) / 2);
        }
        else if (_totalObservations >= _wideningObservationFloor &&
                 _ready >= ReadyBoundariesToRecover)
        {
            next = Math.Min(_maximum, current * 2);
            isWiden = true;
        }

        if (next is null) return null;

        var now = _timeProvider.GetUtcNow();
        if (isWiden && (now - _lastTransition) < RewidenHold)
        {
            return null;
        }

        _lastTransition = now;
        _starvationWindow = 0;
        _observations = 0;
        _ready = 0;
        if (next == current) return null;
        Volatile.Write(ref _current, next.Value);
        return new BatchSizeChange(current, next.Value, readyWhenNeeded);
    }
}

internal readonly record struct BatchSizeChange(int Previous, int Current, bool ReadyWhenNeeded);
