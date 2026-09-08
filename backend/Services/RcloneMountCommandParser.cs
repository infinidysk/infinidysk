using System.Globalization;
using NzbWebDAV.Config;

namespace NzbWebDAV.Services;

/// <summary>The outcome of parsing a pasted <c>rclone mount</c> command.</summary>
/// <param name="Success">Whether a mount could be read from the command.</param>
/// <param name="Mount">The translated mount, when parsing succeeded.</param>
/// <param name="Error">Why parsing failed, when it did.</param>
/// <param name="UnsupportedFlags">
/// Flags the built-in mount does not model. These are reported so the operator
/// knows what will not carry across; they are deliberately not persisted, because
/// nothing applies them.
/// </param>
public sealed record RcloneMountCommandResult(
    bool Success,
    RcloneMountConfig? Mount,
    string? Error,
    IReadOnlyList<string> UnsupportedFlags)
{
    public static RcloneMountCommandResult Failed(string error) => new(false, null, error, []);
}

/// <summary>
/// Turns a pasted <c>rclone mount ...</c> command line into a built-in mount
/// definition.
///
/// This is the fallback import path, for the common case where the external
/// rclone has no remote-control API enabled and so cannot be interrogated
/// directly. The parser knows rclone's flag table only for the settings the
/// built-in mount models; anything else is reported rather than guessed at,
/// because guessing wrong about a mount point silently breaks a whole library.
/// </summary>
public static class RcloneMountCommandParser
{
    private static readonly char[] Separators = [' ', '\t', '\n', '\r', '\\'];

    /// <summary>Flags this parser understands that take a value.</summary>
    private static readonly HashSet<string> ValuedFlags = new(StringComparer.Ordinal)
    {
        "--vfs-cache-mode",
        "--dir-cache-time",
        "--vfs-cache-max-age",
        "--vfs-cache-max-size",
        "--vfs-read-ahead",
    };

    /// <summary>Flags this parser understands that take no value.</summary>
    private static readonly HashSet<string> BooleanFlags = new(StringComparer.Ordinal)
    {
        "--allow-other",
        "--links",
    };

    public static RcloneMountCommandResult Parse(string commandLine)
    {
        var tokens = (commandLine ?? string.Empty)
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        var mountIndex = tokens.FindIndex(t => t.Equals("mount", StringComparison.OrdinalIgnoreCase));
        if (mountIndex < 0)
            return RcloneMountCommandResult.Failed("That does not look like an 'rclone mount' command.");

        var positional = new List<string>();
        var unsupported = new List<string>();
        var allowOther = false;
        var links = false;
        RcloneVfsCacheMode? cacheMode = null;
        TimeSpan? dirCacheTime = null;
        TimeSpan? cacheMaxAge = null;
        long? cacheMaxSize = null;
        long? readAhead = null;

        for (var i = mountIndex + 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!token.StartsWith('-'))
            {
                positional.Add(token);
                continue;
            }

            // Short flags such as -vv are none of the settings this parser models,
            // but they are still flags: treating them as positional arguments
            // would push the real mount point out of position.
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                unsupported.Add(token);
                continue;
            }

            var (name, inlineValue) = SplitFlag(token);

            if (BooleanFlags.Contains(name))
            {
                if (name == "--allow-other") allowOther = true;
                if (name == "--links") links = true;
                continue;
            }

            if (!ValuedFlags.Contains(name))
            {
                // An unmodelled flag's value cannot be told apart from a positional
                // argument without rclone's own flag table, so nothing is consumed
                // here. A boolean flag parses correctly; a valued one in
                // space-separated form leaves an extra positional, which is caught
                // below rather than silently mistaken for the mount point.
                unsupported.Add(token);
                continue;
            }

            var value = inlineValue;
            if (value is null)
            {
                value = PeekValue(tokens, i, out var consumed);
                if (consumed) i++;
            }

            switch (name)
            {
                case "--vfs-cache-mode" when Enum.TryParse<RcloneVfsCacheMode>(value, true, out var parsedMode):
                    cacheMode = parsedMode;
                    break;
                case "--dir-cache-time" when TryParseDuration(value, out var parsedDirCache):
                    dirCacheTime = parsedDirCache;
                    break;
                case "--vfs-cache-max-age" when TryParseDuration(value, out var parsedMaxAge):
                    cacheMaxAge = parsedMaxAge;
                    break;
                case "--vfs-cache-max-size" when TryParseSize(value, out var parsedMaxSize):
                    cacheMaxSize = parsedMaxSize;
                    break;
                case "--vfs-read-ahead" when TryParseSize(value, out var parsedReadAhead):
                    readAhead = parsedReadAhead;
                    break;
                default:
                    // A modelled flag whose value could not be read is still a
                    // setting that will not carry across; say so rather than
                    // silently falling back to a default.
                    unsupported.Add(value is null ? token : $"{name}={value}");
                    break;
            }
        }

        if (positional.Count < 2)
            return RcloneMountCommandResult.Failed("The command does not include a mount point to mount onto.");

        if (positional.Count > 2)
        {
            var ambiguous = unsupported.Count > 0
                ? $" The flags {string.Join(" ", unsupported)} are not recognised, so their values could not be " +
                  "told apart from the mount point. Rewrite them in '--flag=value' form and try again."
                : string.Empty;

            return RcloneMountCommandResult.Failed(
                $"The command has {positional.Count} arguments where 'rclone mount' takes two " +
                $"(a remote and a mount point).{ambiguous}");
        }

        var mountPoint = positional[1];
        var mount = new RcloneMountConfig
        {
            Id = RcloneImportTranslator.DeriveId(mountPoint),
            Name = mountPoint,
            MountPoint = mountPoint,
            RemotePath = RcloneImportTranslator.ExtractRemotePath(positional[0]),
            // Absent means rclone's own default, which is "off". Preserving that is
            // the point of an import: the mount should behave as it does today.
            VfsCacheMode = cacheMode ?? RcloneVfsCacheMode.Off,
            AllowOther = allowOther,
            Links = links,
            VfsCacheMaxSizeBytes = cacheMaxSize,
            ReadAheadBytes = readAhead,
        };

        if (dirCacheTime is { } dirCache) mount.DirCacheTime = dirCache;
        if (cacheMaxAge is { } maxAge) mount.VfsCacheMaxAge = maxAge;

        return new RcloneMountCommandResult(true, mount, null, unsupported);
    }

    private static (string Name, string? Value) SplitFlag(string token)
    {
        var equals = token.IndexOf('=', StringComparison.Ordinal);
        return equals < 0 ? (token, null) : (token[..equals], token[(equals + 1)..]);
    }

    private static string PeekValue(List<string> tokens, int index, out bool consumed)
    {
        var next = index + 1 < tokens.Count ? tokens[index + 1] : null;
        consumed = next is not null && !next.StartsWith('-');
        return consumed ? next! : string.Empty;
    }

    /// <summary>
    /// Parses rclone's size syntax: a number with an optional binary suffix,
    /// for example <c>512M</c> or <c>20G</c>. A bare number is bytes.
    /// </summary>
    public static bool TryParseSize(string? value, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var trimmed = value.Trim();
        var multiplier = 1L;
        var suffix = char.ToUpperInvariant(trimmed[^1]);

        if (!char.IsDigit(suffix))
        {
            multiplier = suffix switch
            {
                'B' => 1L,
                'K' => 1024L,
                'M' => 1024L * 1024,
                'G' => 1024L * 1024 * 1024,
                'T' => 1024L * 1024 * 1024 * 1024,
                _ => 0,
            };

            if (multiplier == 0) return false;
            trimmed = trimmed[..^1];
        }

        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var magnitude))
            return false;

        bytes = (long)(magnitude * multiplier);
        return true;
    }

    /// <summary>
    /// Parses rclone's duration syntax, for example <c>20s</c>, <c>24h</c>, or
    /// <c>1h30m</c>.
    /// </summary>
    public static bool TryParseDuration(string? value, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var total = TimeSpan.Zero;
        var magnitude = string.Empty;
        var sawUnit = false;

        foreach (var character in value.Trim())
        {
            if (char.IsDigit(character) || character == '.')
            {
                magnitude += character;
                continue;
            }

            if (magnitude.Length == 0) return false;
            if (!double.TryParse(magnitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
                return false;

            var unit = char.ToLowerInvariant(character) switch
            {
                's' => TimeSpan.FromSeconds(amount),
                'm' => TimeSpan.FromMinutes(amount),
                'h' => TimeSpan.FromHours(amount),
                'd' => TimeSpan.FromDays(amount),
                _ => TimeSpan.MinValue,
            };

            if (unit == TimeSpan.MinValue) return false;

            total += unit;
            magnitude = string.Empty;
            sawUnit = true;
        }

        if (!sawUnit || magnitude.Length > 0) return false;

        duration = total;
        return true;
    }
}
