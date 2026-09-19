using System.Diagnostics;

namespace NzbWebDAV.Services.StreamTrace;

internal sealed class StreamTraceRequestTiming : IDisposable
{
    private readonly Stopwatch _stopwatch;
    private readonly CancellationTokenRegistration _cancellation;
    private readonly CancellationToken _cancellationToken;
    private CancellationObservation? _cancellationObservation;
    private long? _transferEndedMs;

    internal StreamTraceRequestTiming(Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        _stopwatch = stopwatch;
        _cancellationToken = cancellationToken;
        _cancellation = cancellationToken.UnsafeRegister(static state =>
        {
            var timing = (StreamTraceRequestTiming)state!;
            timing.ObserveCancellation("callback");
        }, this);
    }

    internal StreamTraceRangeContext? Range { get; set; }

    internal void TransferEnded()
    {
        _transferEndedMs ??= _stopwatch.ElapsedMilliseconds;
        ObserveCancellation("transfer-end");
    }

    internal void Complete(StreamTraceBuffer buffer, long? firstByteMs)
    {
        ObserveCancellation("request-end");
        _cancellation.Dispose();
        if (Range is not { } range)
            return;
        var cancellation = Volatile.Read(ref _cancellationObservation);
        buffer.RequestEnd(range, firstByteMs, _stopwatch.ElapsedMilliseconds,
            _transferEndedMs, cancellation?.ElapsedMs, cancellation?.Source);
    }

    public void Dispose() => _cancellation.Dispose();

    private void ObserveCancellation(string source)
    {
        if (_cancellationToken.IsCancellationRequested && Volatile.Read(ref _cancellationObservation) is null)
            Interlocked.CompareExchange(ref _cancellationObservation,
                new CancellationObservation(_stopwatch.ElapsedMilliseconds, source), null);
    }

    private sealed record CancellationObservation(long ElapsedMs, string Source);
}