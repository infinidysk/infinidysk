using System.Globalization;
using System.Text.RegularExpressions;

namespace UsenetSharp.Models;

public partial record UsenetArticleHeader
{
    public required Dictionary<string, string> Headers { get; init; }

    /// <summary>All header fields in wire order, including duplicates.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> AllHeaders { get; init; } = [];

    private DateTimeOffset? _date;

    // Common header accessors for convenience
    public string? Subject => Headers.GetValueOrDefault("Subject");
    public string? From => Headers.GetValueOrDefault("From");
    public string? RawDate => Headers.GetValueOrDefault("Date");
    public string? MessageId => Headers.GetValueOrDefault("Message-ID");
    public string? References => Headers.GetValueOrDefault("References");
    public string? ContentType => Headers.GetValueOrDefault("Content-Type");
    public string? ContentTransferEncoding => Headers.GetValueOrDefault("Content-Transfer-Encoding");
    public string? Newsgroups => Headers.GetValueOrDefault("Newsgroups");
    public string? XrefFull => Headers.GetValueOrDefault("Xref");
    public string? Lines => Headers.GetValueOrDefault("Lines");
    public string? Bytes => Headers.GetValueOrDefault("Bytes");

    // Lazy-loaded parsed date accessor
    public DateTimeOffset Date
    {
        get
        {
            _date ??= GetDateTimeOffset(RawDate);
            return _date.Value;
        }
    }

    private static DateTimeOffset GetDateTimeOffset(string? date)
    {
        return !string.IsNullOrEmpty(date) && TryParseDateUtc(date, out var parsedDate)
            ? parsedDate
            : DateTimeOffset.UtcNow;
    }

    // Comprehensive zone map (RFC 822 + common real-world + military)
    private static readonly Dictionary<string, string> TimeZoneOffsets = new(StringComparer.OrdinalIgnoreCase)
    {
        // RFC 822
        ["UT"] = "+0000",
        ["UTC"] = "+0000",
        ["GMT"] = "+0000",
        ["EST"] = "-0500",
        ["EDT"] = "-0400",
        ["CST"] = "-0600",
        ["CDT"] = "-0500",
        ["MST"] = "-0700",
        ["MDT"] = "-0600",
        ["PST"] = "-0800",
        ["PDT"] = "-0700",

        // Common European
        ["BST"] = "+0100",
        ["CET"] = "+0100",
        ["CEST"] = "+0200",
        ["EET"] = "+0200",
        ["EEST"] = "+0300",
        ["WET"] = "+0000",
        ["WEST"] = "+0100",

        // Asia–Pacific (often seen in NNTP)
        ["IST"] = "+0530",
        ["JST"] = "+0900",
        ["KST"] = "+0900",
        ["AEST"] = "+1000",
        ["AEDT"] = "+1100",
        ["ACST"] = "+0930",
        ["ACDT"] = "+1030",
        ["AWST"] = "+0800",
        ["NZST"] = "+1200",
        ["NZDT"] = "+1300",

        // Military letters (A–Z except J)
        ["A"] = "+0100",
        ["B"] = "+0200",
        ["C"] = "+0300",
        ["D"] = "+0400",
        ["E"] = "+0500",
        ["F"] = "+0600",
        ["G"] = "+0700",
        ["H"] = "+0800",
        ["I"] = "+0900",
        ["K"] = "+1000",
        ["L"] = "+1100",
        ["M"] = "+1200",
        ["N"] = "-0100",
        ["O"] = "-0200",
        ["P"] = "-0300",
        ["Q"] = "-0400",
        ["R"] = "-0500",
        ["S"] = "-0600",
        ["T"] = "-0700",
        ["U"] = "-0800",
        ["V"] = "-0900",
        ["W"] = "-1000",
        ["X"] = "-1100",
        ["Y"] = "-1200",
        ["Z"] = "+0000"
    };

    private static readonly string[] DateFormats =
    {
        // RFC 5322-style (preferred)
        "ddd, dd MMM yyyy HH:mm:ss zzz",
        "ddd, dd MMM yyyy HH:mm zzz",
        // Legacy 2-digit year
        "ddd, dd MMM yy HH:mm:ss zzz",
        "ddd, dd MMM yy HH:mm zzz",
        // Missing comma variations
        "dd MMM yyyy HH:mm:ss zzz",
        "dd MMM yy HH:mm:ss zzz",
        // Occasionally missing seconds
        "ddd, dd MMM yyyy HH:mm zzz",
        "ddd, dd MMM yy HH:mm zzz"
    };

    private static bool TryParseDateUtc(string input, out DateTimeOffset dateTimeOffset)
    {
        dateTimeOffset = default;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        // Remove comments like "(UTC)"
        string cleaned = CommentsRegex().Replace(input, " ");

        // Collapse multiple spaces
        cleaned = MultipleSpacesRegex().Replace(cleaned.Trim(), " ");

        // Normalize known named time zones
        foreach (var kvp in TimeZoneOffsets)
        {
            cleaned = Regex.Replace(
                cleaned,
                $@"\b{Regex.Escape(kvp.Key)}\b",
                kvp.Value,
                RegexOptions.IgnoreCase);
        }

        // Ensure we have something like +0000 at the end if possible
        cleaned = OffsetRegex().Replace(cleaned, " +$1");

        // Try each explicit format
        foreach (var fmt in DateFormats)
        {
            if (DateTimeOffset.TryParseExact(
                    cleaned,
                    fmt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                    out dateTimeOffset))
            {
                dateTimeOffset = dateTimeOffset.ToUniversalTime();
                return true;
            }
        }

        // Fallback flexible parse
        if (DateTimeOffset.TryParse(
                cleaned,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out dateTimeOffset))
        {
            dateTimeOffset = dateTimeOffset.ToUniversalTime();
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"\s*\(.*?\)\s*")]
    private static partial Regex CommentsRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultipleSpacesRegex();

    [GeneratedRegex(@"\s([0-9]{4})$")]
    private static partial Regex OffsetRegex();
}
