namespace NzbWebDAV.Utils;

public static class ProfileLanguageMetadata
{
    private static readonly Dictionary<string, string> AudioFlags = new(StringComparer.Ordinal)
    {
        ["en"] = "🇬🇧",
        ["es"] = "🇪🇸",
        ["fr"] = "🇫🇷",
        ["de"] = "🇩🇪",
        ["it"] = "🇮🇹",
        ["pt"] = "🇵🇹",
        ["nl"] = "🇳🇱",
        ["ru"] = "🇷🇺",
        ["ja"] = "🇯🇵",
        ["ko"] = "🇰🇷",
        ["zh"] = "🇨🇳",
        ["ar"] = "🇸🇦",
        ["hi"] = "🇮🇳",
        ["swe"] = "🇸🇪",
        ["nor"] = "🇳🇴",
        ["dan"] = "🇩🇰",
        ["fin"] = "🇫🇮",
        ["pol"] = "🇵🇱",
        ["tur"] = "🇹🇷",
    };

    public static List<string> NormalizeAudioLanguages(string? value)
        => Normalize(value);

    public static List<string> NormalizeSubtitleLanguages(string? value)
        => Normalize(value);

    public static List<string> AudioFlagMarkers(IEnumerable<string> codes)
    {
        return codes
            .Where(AudioFlags.ContainsKey)
            .Select(code => AudioFlags[code])
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> Normalize(string? value)
        => SubtitlePreference.ParseLanguages(value)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();
}
