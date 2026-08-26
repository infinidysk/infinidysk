using NzbWebDAV.Exceptions;

namespace NzbWebDAV.Streams;

/// <summary>
/// Collects yEnc CRC failures across providers. When two or more providers
/// report the same (actual, expected) pair, the article is persistently
/// corrupt and further retries cannot recover it.
/// </summary>
internal sealed class PersistentCorruptionTracker
{
    private readonly Dictionary<string, (uint Actual, uint Expected)> _pairs =
        new(StringComparer.OrdinalIgnoreCase);

    public void NoteOrThrow(UsenetCorruptArticleException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (!exception.TryGetCrcPair(out var actual, out var expected))
            return;

        _pairs[exception.ProviderKey] = (actual, expected);

        var matching = 0;
        foreach (var observed in _pairs.Values)
        {
            if (observed.Actual == actual && observed.Expected == expected)
                matching++;
        }

        if (matching >= 2)
            throw new PersistentUsenetCorruptionException(exception.SegmentId, actual, expected, exception);
    }
}
