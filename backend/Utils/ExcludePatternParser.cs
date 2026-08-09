using System.Text.RegularExpressions;
using Serilog;

namespace NzbWebDAV.Utils;

/// <summary>
/// Parses exclude-filter regex lines — from both the manual textarea and synced-URL
/// payloads — into compiled <see cref="Regex"/> objects, and produces a stable dedup
/// key so the same pattern arriving from different sources is compiled and run once.
///
/// Two input shapes are accepted:
///   - a bare .NET regex body, e.g. <c>\.iso$</c>
///   - a JavaScript-style wrapper, e.g. <c>/\.(iso|img)$/i</c> — the shape community
///     lists (TRaSH-derived "excluded-regex" URLs, AIOStreams templates) publish.
///
/// Matching is always case-insensitive (the existing DAVX convention — authors opt out
/// per-pattern with inline <c>(?-i:...)</c>). The <c>m</c>/<c>s</c> wrapper flags add
/// Multiline / Singleline; other JS flags (g/u/y/n) do not affect
/// <see cref="Regex.IsMatch(string)"/> and are ignored.
/// </summary>
public static class ExcludePatternParser
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private const RegexOptions BaseOptions =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    // /body/flags — greedy body up to the final slash, optional trailing flags.
    private static readonly Regex JsWrapper = new(@"^/(.+)/([a-z]*)$", RegexOptions.Compiled);

    public readonly record struct ParsedPattern(string Key, Regex Regex);

    /// <summary>
    /// Parse a single line. Returns <c>null</c> for blanks, <c>#</c> comments, or patterns
    /// that fail to compile (logged and skipped — never throws).
    /// </summary>
    public static ParsedPattern? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#')) return null;

        var body = trimmed;
        var options = BaseOptions;
        var extraFlags = "";

        var wrapper = JsWrapper.Match(trimmed);
        if (wrapper.Success)
        {
            body = wrapper.Groups[1].Value;
            var flags = wrapper.Groups[2].Value;
            // Built in a fixed order so "/x/ms" and "/x/sm" yield the same dedup key.
            if (flags.Contains('m', StringComparison.Ordinal)) { options |= RegexOptions.Multiline; extraFlags += "m"; }
            if (flags.Contains('s', StringComparison.Ordinal)) { options |= RegexOptions.Singleline; extraFlags += "s"; }
        }

        if (body.Length == 0) return null;

        try
        {
            var regex = new Regex(body, options, MatchTimeout);
            // IgnoreCase is constant, so it is excluded from the key — that lets a raw body
            // and its /.../i wrapper form (identical intent) collapse to one entry.
            var key = body + " " + extraFlags;
            return new ParsedPattern(key, regex);
        }
        catch (ArgumentException e)
        {
            Log.Warning("Skipping invalid exclude pattern {Pattern}: {Message}", trimmed, e.Message);
            return null;
        }
    }
}
