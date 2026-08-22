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
        var flags = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var code in codes)
        {
            if (AudioFlags.TryGetValue(code, out var flag) && seen.Add(flag))
                flags.Add(flag);
        }

        return flags;
    }

    private static List<string> Normalize(string? value)
        => SubtitlePreference.ParseLanguages(value)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();
}
