using System.Text;
using Serilog;

namespace NzbWebDAV.Utils;

/// <summary>
/// Sanitizes Dav path components for Windows-invalid names. Uses an explicit
/// Windows character list — never <see cref="Path.GetInvalidFileNameChars()"/>
/// (host-OS dependent; Linux returns only '/' and NUL).
/// </summary>
public static class PathSanitizer
{
    private static readonly char[] WindowsInvalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static volatile bool _windowsSafePathsEnabled = true;

    public static void SetWindowsSafePathsEnabled(bool enabled) =>
        _windowsSafePathsEnabled = enabled;

    public static bool IsWindowsSafePathsEnabled => _windowsSafePathsEnabled;

    public static string SanitizeComponent(string name, bool? windowsSafe = null)
    {
        var enabled = windowsSafe ?? _windowsSafePathsEnabled;
        if (string.IsNullOrEmpty(name))
            return "untitled";

        if (!enabled)
            return SanitizeMinimal(name);

        var sb = new StringBuilder(name.Length);
        foreach (var ch in XmlTextUtil.ReplaceInvalidXmlChars(name, '_'))
        {
            if (ch < 0x20 || WindowsInvalidChars.Contains(ch))
                sb.Append('_');
            else
                sb.Append(ch);
        }

        var sanitized = sb.ToString();

        // Windows silently strips trailing dots/spaces — trim all of them.
        sanitized = sanitized.TrimEnd('.', ' ');

        if (string.IsNullOrEmpty(sanitized))
            return "untitled";

        var stem = Path.GetFileNameWithoutExtension(sanitized);
        if (WindowsReservedNames.Contains(stem))
            sanitized = "_" + sanitized;

        if (sanitized.Length > MaxComponentLength)
        {
            sanitized = TruncateToMaxComponentLength(sanitized);

            if (string.IsNullOrEmpty(sanitized))
                return "untitled";
        }

        return sanitized;
    }

    /// <summary>
    /// Minimal sanitization when Windows-safe paths are disabled: only '/', NUL, and
    /// characters XML 1.0 forbids (they would break every WebDAV listing of the parent).
    /// Still length-capped so a sanitized name can never overflow DavItem.Name (255).
    /// </summary>
    private static string SanitizeMinimal(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in XmlTextUtil.ReplaceInvalidXmlChars(name, '_'))
        {
            if (ch is '/' or '\0')
                sb.Append('_');
            else
                sb.Append(ch);
        }

        var sanitized = sb.ToString();
        if (string.IsNullOrEmpty(sanitized))
            return "untitled";

        // Truncation trims trailing dots/spaces, which can empty an over-long component.
        sanitized = TruncateToMaxComponentLength(sanitized);
        return string.IsNullOrEmpty(sanitized) ? "untitled" : sanitized;
    }

    // DavItem.Name is HasMaxLength(255); 240 leaves headroom for " (xxxxx)" duplicate suffixes.
    private const int MaxComponentLength = 240;

    private static string TruncateToMaxComponentLength(string name)
    {
        if (name.Length <= MaxComponentLength)
            return name;

        var extension = Path.GetExtension(name);
        string truncated;
        if (extension.Length > 0 && extension.Length < MaxComponentLength)
        {
            var maxStem = MaxComponentLength - extension.Length;
            truncated = name[..maxStem].TrimEnd('.', ' ') + extension;
        }
        else
        {
            truncated = name[..MaxComponentLength].TrimEnd('.', ' ');
        }

        return truncated;
    }

    public static string SanitizeComponentWithLog(string original)
    {
        var sanitized = SanitizeComponent(original);
        if (!string.Equals(original, sanitized, StringComparison.Ordinal))
        {
            Log.Information("Sanitized path component {Original} -> {Sanitized}", original, sanitized);
        }

        return sanitized;
    }
}
