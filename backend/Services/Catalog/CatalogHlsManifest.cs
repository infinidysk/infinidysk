using System.Globalization;
using System.Text;

namespace NzbWebDAV.Services.Catalog;

/// <summary>
/// Static VOD manifest for the compatibility path. Segments are fixed six-second encodes
/// produced on demand, so the manifest is fully known once the media duration is probed.
/// Segments carry absolute, contiguous timestamps, so no discontinuity tags are needed.
/// </summary>
internal static class CatalogHlsManifest
{
    internal const int SegmentSeconds = 6;

    /// <summary>
    /// A trailing fragment shorter than this is folded into the previous segment: real
    /// durations carry container rounding, and a 0.0000001s segment would be unplayable.
    /// </summary>
    private const double MinimumTrailingSeconds = 0.05;

    internal static int SegmentCount(TimeSpan duration)
    {
        var total = duration.TotalSeconds;
        if (total <= 0) return 0;
        var count = (int)Math.Ceiling(total / SegmentSeconds);
        if (count > 1 && total - (count - 1) * (double)SegmentSeconds < MinimumTrailingSeconds)
            count--;
        return count;
    }

    internal static TimeSpan SegmentStart(int index) => TimeSpan.FromSeconds(index * SegmentSeconds);

    internal static TimeSpan SegmentDuration(TimeSpan duration, int index)
    {
        var remaining = duration - SegmentStart(index);
        var full = TimeSpan.FromSeconds(SegmentSeconds);
        if (remaining <= TimeSpan.Zero) return TimeSpan.Zero;
        return remaining < full ? remaining : full;
    }

    internal static string Build(TimeSpan duration)
    {
        var count = SegmentCount(duration);
        var builder = new StringBuilder(64 + count * 48);
        builder.Append("#EXTM3U\n");
        builder.Append("#EXT-X-VERSION:3\n");
        builder.Append("#EXT-X-TARGETDURATION:").Append(SegmentSeconds.ToString(CultureInfo.InvariantCulture)).Append('\n');
        builder.Append("#EXT-X-MEDIA-SEQUENCE:0\n");
        builder.Append("#EXT-X-PLAYLIST-TYPE:VOD\n");

        for (var index = 0; index < count; index++)
        {
            builder.Append("#EXTINF:")
                .Append(SegmentDuration(duration, index).TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture))
                .Append(",\n");
            builder.Append("segments/").Append(index.ToString(CultureInfo.InvariantCulture)).Append(".ts\n");
        }

        builder.Append("#EXT-X-ENDLIST\n");
        return builder.ToString();
    }
}
