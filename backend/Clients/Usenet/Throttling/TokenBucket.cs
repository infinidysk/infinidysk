using System.Diagnostics;

namespace NzbWebDAV.Clients.Usenet.Throttling;

/// <summary>
/// A simple async token bucket used to cap sustained throughput (bytes/second).
/// Allows a burst of up to one second's worth of bytes, then throttles to the configured rate.
/// </summary>
public class TokenBucket
{
    private readonly Lock _lock = new();
    private double _bytesPerSecond;
    private double _availableBytes;
    private long _lastRefillTimestamp;
    private long _totalBytesConsumed;

    public TokenBucket(double bytesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerSecond);
        _bytesPerSecond = bytesPerSecond;
        _availableBytes = bytesPerSecond;
        _lastRefillTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Running total of bytes consumed since this bucket was created. Used to derive a
    /// live throughput reading (by sampling the delta over a time window) for the UI.
    /// </summary>
    public long TotalBytesConsumed
    {
        get { lock (_lock) return _totalBytesConsumed; }
    }

    public async Task ConsumeAsync(int byteCount, CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan waitTime;
            lock (_lock)
            {
                Refill();
                if (_availableBytes >= byteCount)
                {
                    _availableBytes -= byteCount;
                    _totalBytesConsumed += byteCount;
                    return;
                }

                var missingBytes = byteCount - _availableBytes;
                waitTime = TimeSpan.FromSeconds(missingBytes / _bytesPerSecond);
            }

            await Task.Delay(waitTime, cancellationToken).ConfigureAwait(false);
        }
    }

    public void UpdateRate(double bytesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerSecond);
        lock (_lock)
        {
            Refill();
            _bytesPerSecond = bytesPerSecond;
            _availableBytes = Math.Min(_availableBytes, _bytesPerSecond);
        }
    }

    // must be called while holding _lock
    private void Refill()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedSeconds = (now - _lastRefillTimestamp) / (double)Stopwatch.Frequency;
        _lastRefillTimestamp = now;

        // burst capacity is capped at one second's worth of bytes
        _availableBytes = Math.Min(_bytesPerSecond, _availableBytes + elapsedSeconds * _bytesPerSecond);
    }
}
