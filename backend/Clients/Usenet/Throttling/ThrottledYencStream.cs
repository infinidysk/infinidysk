using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Clients.Usenet.Throttling;

/// <summary>
/// Wraps a decoded YencStream so that every byte read from it is metered against
/// a shared TokenBucket, capping the sustained download rate.
/// </summary>
public class ThrottledYencStream : YencStream
{
    private readonly YencStream _innerStream;
    private readonly TokenBucket _tokenBucket;

    public ThrottledYencStream(YencStream innerStream, TokenBucket tokenBucket) : base(Null)
    {
        _innerStream = innerStream;
        _tokenBucket = tokenBucket;
    }

    public override ValueTask<UsenetYencHeader?> GetYencHeadersAsync(CancellationToken cancellationToken = default)
    {
        return _innerStream.GetYencHeadersAsync(cancellationToken);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0) await _tokenBucket.ConsumeAsync(read, cancellationToken).ConfigureAwait(false);
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
