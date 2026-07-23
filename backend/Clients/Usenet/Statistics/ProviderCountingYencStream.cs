using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Clients.Usenet.Statistics;

/// <summary>
/// Wraps a decoded YencStream so that every byte actually read from it is reported to the
/// per-provider usage aggregator, independent of the article's advertised size.
/// </summary>
public class ProviderCountingYencStream : YencStream
{
    private readonly YencStream _innerStream;
    private readonly ProviderUsageStatsAggregator _statsAggregator;
    private readonly Guid _providerId;

    public ProviderCountingYencStream(YencStream innerStream, ProviderUsageStatsAggregator statsAggregator,
        Guid providerId) : base(Null)
    {
        _innerStream = innerStream;
        _statsAggregator = statsAggregator;
        _providerId = providerId;
    }

    public override ValueTask<UsenetYencHeader?> GetYencHeadersAsync(CancellationToken cancellationToken = default)
    {
        return _innerStream.GetYencHeadersAsync(cancellationToken);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0) _statsAggregator.RecordBytesDownloaded(_providerId, read);
        return read;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _innerStream.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _innerStream.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
