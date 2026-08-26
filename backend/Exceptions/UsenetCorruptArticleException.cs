using System.Globalization;
using System.Text.RegularExpressions;

namespace NzbWebDAV.Exceptions;

public sealed class UsenetCorruptArticleException(
    string segmentId,
    string providerKey,
    Exception innerException)
    : RetryableDownloadException(
        $"Provider {providerKey} returned corrupt yEnc data for segment {segmentId}.",
        innerException)
{
    private static readonly Regex CrcPairPattern = new(
        @"The decoded yEnc CRC32 was ([0-9a-f]{8}), but the trailer expected ([0-9a-f]{8})\.",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public string SegmentId { get; } = segmentId;
    public string ProviderKey { get; } = providerKey;

    public bool TryGetCrcPair(out uint actualCrc, out uint expectedCrc)
    {
        actualCrc = 0;
        expectedCrc = 0;
        for (var current = InnerException; current != null; current = current.InnerException)
        {
            var match = CrcPairPattern.Match(current.Message);
            if (!match.Success) continue;
            if (!uint.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out actualCrc))
                continue;
            if (!uint.TryParse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out expectedCrc))
                continue;
            return true;
        }

        return false;
    }
}
