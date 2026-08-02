using System.Diagnostics;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

/// <summary>
/// Wraps a YencStream and attributes every byte read back to the provider that
/// served it. The inner stream still performs the real decode work; this class
/// just observes the byte count on each Read and forwards it to
/// ProviderBytesTracker so per-provider download volume can be aggregated.
/// </summary>
public sealed class CountingYencStream : YencStream
{
    private readonly YencStream _inner;
    private readonly ProviderBytesTracker _tracker;
    private readonly string _providerKey;
    private readonly ActiveReadRegistry? _activeReadRegistry;
    private long _bytes;
    private long _activeReadTicks;
    private int _recorded;

    public CountingYencStream(
        YencStream inner,
        ProviderBytesTracker tracker,
        string providerKey,
        ActiveReadRegistry? activeReadRegistry = null) : base(Null)
    {
        _inner = inner;
        _tracker = tracker;
        _providerKey = providerKey;
        _activeReadRegistry = activeReadRegistry;
    }

    public override ValueTask<UsenetYencHeader?> GetYencHeadersAsync(CancellationToken cancellationToken = default)
        => _inner.GetYencHeadersAsync(cancellationToken);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var start = Stopwatch.GetTimestamp();
        var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _activeReadTicks += Stopwatch.GetTimestamp() - start;
        if (n > 0)
        {
            _tracker.Add(_providerKey, n);
            _bytes += n;
            if (MultiProviderNntpClient.CurrentReadSessionId is { } sessionId)
                _activeReadRegistry?.AddBytesFetched(sessionId, n);
        }
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        // Teardown releases a body the consumer may have released already, and the sample
        // feeds a provider-selection average, so record it once regardless.
        if (disposing)
        {
            if (_bytes > 0 && _activeReadTicks > 0 && Interlocked.Exchange(ref _recorded, 1) == 0)
            {
                var activeMs = _activeReadTicks * 1000.0 / Stopwatch.Frequency;
                _tracker.RecordSegmentThroughput(_providerKey, _bytes, activeMs);
            }

            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
