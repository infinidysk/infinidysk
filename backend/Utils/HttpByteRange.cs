namespace NzbWebDAV.Utils;

/// <summary>
/// Parse a single RFC 7233 <c>bytes</c> Range. Returns false for missing,
/// non-bytes, multi-range, or otherwise unparseable headers.
/// </summary>
public static class HttpByteRange
{
    public static bool TryParse(
        string rangeHeader,
        out long? rangeStart,
        out long? rangeEnd,
        out long? suffixLength)
    {
        rangeStart = null;
        rangeEnd = null;
        suffixLength = null;

        if (string.IsNullOrEmpty(rangeHeader) || !rangeHeader.StartsWith("bytes=", StringComparison.Ordinal))
            return false;

        var spec = rangeHeader["bytes=".Length..];
        if (spec.Contains(',', StringComparison.Ordinal))
            return false;

        if (spec.StartsWith('-'))
        {
            if (!long.TryParse(spec[1..], out var suffix) || suffix < 0)
                return false;
            suffixLength = suffix;
            return true;
        }

        var dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
            return false;

        var startPart = spec[..dash];
        var endPart = spec[(dash + 1)..];

        if (!long.TryParse(startPart, out var start) || start < 0)
            return false;

        long? parsedEnd = null;
        if (endPart.Length > 0)
        {
            if (!long.TryParse(endPart, out var end) || end < 0)
                return false;
            parsedEnd = end;
        }

        rangeStart = start;
        rangeEnd = parsedEnd;
        return true;
    }
}
