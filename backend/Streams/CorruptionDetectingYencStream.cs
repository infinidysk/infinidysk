using NzbWebDAV.Exceptions;
using Serilog;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

/// <summary>
/// Adds provider and segment context to decoded yEnc validation failures.
/// </summary>
public sealed class CorruptionDetectingYencStream(
    YencStream inner,
    string segmentId,
    string providerKey) : YencStream(Null)
{
    private int _reported;

    public override async ValueTask<UsenetYencHeader?> GetYencHeadersAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await inner.GetYencHeadersAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            throw Map(exception);
        }
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            throw Map(exception);
        }
    }

    private UsenetCorruptArticleException Map(InvalidDataException exception)
    {
        if (Interlocked.Exchange(ref _reported, 1) == 0)
        {
            ThrottledSegmentWarning.Write(
                $"{providerKey}\n{segmentId}",
                "Provider {Provider} returned corrupt yEnc data for segment {SegmentId}. Reason: {Reason}",
                providerKey,
                segmentId,
                exception.Message);
            Log.Debug(
                exception,
                "Corrupt yEnc data stack for provider {Provider} segment {SegmentId}",
                providerKey,
                segmentId);
        }

        return new UsenetCorruptArticleException(segmentId, providerKey, exception);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }
}
