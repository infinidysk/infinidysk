using System.Text.RegularExpressions;

namespace NzbWebDAV.Utils;

/// <summary>
/// Filename-based filtering shared by queue post-processors and playback selection:
/// sample detection and glob matching.
/// </summary>
public static partial class FileFilterUtil
{
    /// <summary>
    /// A sample must be smaller than this fraction of the largest video file
    /// in the same release before it is treated as a sample.
    /// </summary>
    public const double SampleMaxSizeRatio = 0.20;

    // "sample" / "samples" as a whole word, with any non-alphanumeric
    // delimiter (or a string boundary) on either side. Matches
    // `sample.mkv`, `Show.S01E01.sample.mkv`, `sample-Show.mkv`
    // and `Show (sample).mkv`, but not `Resampled.mkv`.
    [GeneratedRegex(@"(?:^|[^a-z0-9])samples?(?:[^a-z0-9]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex SampleNameRegex();

    /// <summary>
    /// True when the filename looks like a sample, ignoring file size.
    /// Name-only matching cannot distinguish a sample from a release whose
    /// title genuinely contains the word, so prefer <see cref="IsSampleFile"/>
    /// wherever the sizes of the sibling files are known.
    /// </summary>
    public static bool LooksLikeSampleName(string filename)
    {
        return SampleNameRegex().IsMatch(Path.GetFileName(filename));
    }

    /// <summary>
    /// True when the file is a video whose name looks like a sample and which
    /// is much smaller than the largest video in the same release. The size
    /// check is what keeps a real release such as `Free.Samples.2012.mkv` —
    /// which is itself the largest video — from being filtered out.
    /// </summary>
    public static bool IsSampleFile(string filename, long? fileSize, long largestVideoFileSize)
    {
        if (!FilenameUtil.IsVideoFile(filename)) return false;
        if (!LooksLikeSampleName(filename)) return false;

        // Without sizes to compare against, we cannot tell a sample apart
        // from a small release, so leave the file alone.
        if (fileSize is null or <= 0) return false;
        if (largestVideoFileSize <= 0) return false;

        return fileSize.Value < largestVideoFileSize * SampleMaxSizeRatio;
    }

    /// <summary>
    /// True when the filename matches any of the given globs (`*` and `?`).
    /// Matching is case-insensitive and applied to the filename only.
    /// </summary>
    public static bool MatchesAnyGlob(string filename, IReadOnlyCollection<string> globs)
    {
        if (globs.Count == 0) return false;
        var name = Path.GetFileName(filename);
        return globs.Any(glob => GlobToRegex(glob).IsMatch(name));
    }

    private static readonly Dictionary<string, Regex> GlobCache = new(StringComparer.OrdinalIgnoreCase);

    private static Regex GlobToRegex(string glob)
    {
        lock (GlobCache)
        {
            if (GlobCache.TryGetValue(glob, out var cached)) return cached;
            var pattern = "^" + Regex.Escape(glob)
                .Replace("\\*", ".*", StringComparison.Ordinal)
                .Replace("\\?", ".") + "$";
            var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            GlobCache[glob] = regex;
            return regex;
        }
    }
}
