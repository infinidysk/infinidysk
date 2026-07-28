using System.Text.RegularExpressions;

namespace NzbWebDAV.Utils;

public partial class FilenameUtil
{
    // Group `pw` contains the password,
    // Group `rm` contains the part of the filename that should be removed to create a clean job name
    // Group `br` ensures that the brackets are closed / is used for back reference
    // Tests: https://regex101.com/r/qsIcnE/1
    [GeneratedRegex(@"(?<rm>[\s-]*(?:(?<br>{{)|password=)(?<pw>.+)(?(br)}}))\.nzb$", RegexOptions.IgnoreCase)]
    public static partial Regex PasswordRegex { get; }

    private static readonly HashSet<string> VideoExtensions =
    [
        ".webm", ".m4v", ".3gp", ".nsv", ".ty", ".strm", ".rm", ".rmvb", ".m3u", ".ifo", ".mov", ".qt", ".divx",
        ".xvid", ".bivx", ".nrg", ".pva", ".wmv", ".asf", ".asx", ".ogm", ".ogv", ".m2v", ".avi", ".bin", ".dat",
        ".dvr-ms", ".mpg", ".mpeg", ".mp4", ".avc", ".vp3", ".svq3", ".nuv", ".viv", ".dv", ".fli", ".flv", ".wpl",
        ".img", ".iso", ".vob", ".mkv", ".mk3d", ".ts", ".wtv", ".m2ts"
    ];

    private static readonly HashSet<string> DegradableVideoExtensions =
    [
        ".mp4", ".m4v", ".mov", ".mkv", ".webm"
    ];

    public static bool IsImportantFileType(string filename)
    {
        return IsVideoFile(filename)
               || IsRarFile(filename)
               || Is7zFile(filename)
               || IsMultipartMkv(filename);
    }

    public static bool IsVideoFile(string filename)
    {
        return VideoExtensions.Contains(Path.GetExtension(filename).ToLower());
    }

    /// <summary>
    /// Plain video containers a player can keep decoding through a zero-filled gap.
    /// Deliberately narrower than <see cref="IsVideoFile"/>, whose list also carries disc
    /// images, playlists and raw streams, where a hole is corruption rather than a glitch.
    /// </summary>
    public static bool IsDegradableVideoFile(string filename)
    {
        return DegradableVideoExtensions.Contains(Path.GetExtension(filename).ToLower());
    }

    public static bool IsRarFile(string? filename)
    {
        if (string.IsNullOrEmpty(filename)) return false;
        return filename.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)
               || Regex.IsMatch(filename, @"\.r(\d+)$", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Injective ordinal for RAR volume identity checks.
    /// .partN.rar → N, .rNN → N + 100_000, bare .rar → -1, else null.
    /// </summary>
    public static int? GetRarPartOrdinal(string? filename)
    {
        if (string.IsNullOrEmpty(filename)) return null;
        var partMatch = Regex.Match(filename, @"\.part(\d+)\.rar$", RegexOptions.IgnoreCase);
        if (partMatch.Success) return int.Parse(partMatch.Groups[1].Value);
        var rMatch = Regex.Match(filename, @"\.r(\d+)$", RegexOptions.IgnoreCase);
        if (rMatch.Success) return int.Parse(rMatch.Groups[1].Value) + 100_000;
        if (filename.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)) return -1;
        return null;
    }

    public static bool Is7zFile(string? filename)
    {
        if (string.IsNullOrEmpty(filename)) return false;
        return Regex.IsMatch(filename, @"\.7z(\.(\d+))?$", RegexOptions.IgnoreCase);
    }

    public static bool IsMultipartMkv(string? filename)
    {
        if (string.IsNullOrEmpty(filename)) return false;
        return Regex.IsMatch(filename, @"\.mkv\.(\d+)?$", RegexOptions.IgnoreCase);
    }

    public static string GetJobName(string filename)
    {
        var passMatch = PasswordRegex.Match(filename);
        var jobName = Path.GetFileNameWithoutExtension(
            passMatch.Success ?
            filename.Replace(passMatch.Groups["rm"].Value, "") :
            filename
        );
        return PathSanitizer.SanitizeComponentWithLog(jobName);
    }

    public static string? GetNzbPassword(string filename)
    {
        var passMatch = PasswordRegex.Match(filename);
        return passMatch.Success ? passMatch.Groups["pw"].Value : null;
    }
}
