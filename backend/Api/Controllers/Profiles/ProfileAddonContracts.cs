using System.Text.Json.Serialization;
using NzbWebDAV.Config;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.Profiles;

public sealed class ProfileAddonManifest
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("logo")]
    public required string Logo { get; init; }

    [JsonPropertyName("resources")]
    public required IReadOnlyList<string> Resources { get; init; }

    [JsonPropertyName("types")]
    public required IReadOnlyList<string> Types { get; init; }

    [JsonPropertyName("idPrefixes")]
    public required IReadOnlyList<string> IdPrefixes { get; init; }

    [JsonPropertyName("behaviorHints")]
    public required ProfileAddonManifestBehaviorHints BehaviorHints { get; init; }
}

public sealed class ProfileAddonManifestBehaviorHints
{
    [JsonPropertyName("configurable")]
    public bool Configurable { get; init; }

    [JsonPropertyName("configurationRequired")]
    public bool ConfigurationRequired { get; init; }
}

public sealed class ProfileAddonStreamResponse
{
    [JsonPropertyName("streams")]
    public required IReadOnlyList<ProfileAddonStream> Streams { get; init; }
}

public sealed class ProfileAddonStream
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("behaviorHints")]
    public required ProfileAddonStreamBehaviorHints BehaviorHints { get; init; }

    [JsonPropertyName("meta")]
    public required ProfileAddonStreamMeta Meta { get; init; }

    [JsonPropertyName("failoverId")]
    public required string FailoverId { get; init; }

    [JsonPropertyName("extra")]
    public required ProfileAddonStreamExtra Extra { get; init; }
}

public sealed class ProfileAddonStreamBehaviorHints
{
    [JsonPropertyName("filename")]
    public required string Filename { get; init; }

    [JsonPropertyName("videoSize")]
    public long VideoSize { get; init; }

    [JsonPropertyName("bingeGroup")]
    public required string BingeGroup { get; init; }

    [JsonPropertyName("notWebReady")]
    public bool NotWebReady { get; init; }
}

public sealed class ProfileAddonStreamMeta
{
    [JsonPropertyName("indexer")]
    public required string Indexer { get; init; }

    [JsonPropertyName("inLibrary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? InLibrary { get; init; }

    [JsonPropertyName("availability")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Availability { get; init; }

    [JsonPropertyName("languages")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Languages { get; init; }

    [JsonPropertyName("subtitleLanguages")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? SubtitleLanguages { get; init; }
}

public sealed class ProfileAddonStreamExtra
{
    [JsonPropertyName("failoverId")]
    public required string FailoverId { get; init; }
}

public static class ProfileAddonFactory
{
    public const string LogoUrl =
        "https://raw.githubusercontent.com/infinidysk/infinidysk/main/docs/assets/logo.png";

    public static ProfileAddonManifest CreateManifest(ProfileConfig.Profile profile, string token)
    {
        return new ProfileAddonManifest
        {
            Id = $"nzbdav.profile.{token}",
            Version = ConfigManager.AppVersion,
            Name = string.IsNullOrWhiteSpace(profile.Name) ? "InfiniDysk Search Profile" : profile.Name,
            Description =
                "Playable Usenet streams from this InfiniDysk Search Profile's configured indexers.",
            Logo = LogoUrl,
            Resources = ["stream"],
            Types = ["movie", "series"],
            IdPrefixes = ["tt", "tmdb", "tvdb", "kitsu", "mal", "anilist"],
            BehaviorHints = new ProfileAddonManifestBehaviorHints
            {
                Configurable = false,
                ConfigurationRequired = false,
            },
        };
    }

    public static ProfileAddonStreamResponse CreateStreamResponse(
        SearchProfileService.SearchResult result,
        string publicBaseUrl,
        IReadOnlySet<string> readyNzbFileNames,
        Func<string, bool> isVerifiedAvailable)
    {
        if (result.Candidates.Count == 0)
            return new ProfileAddonStreamResponse { Streams = [] };

        var streams = result.Candidates
            .Select((candidate, index) => CreateStream(
                candidate,
                result.Type,
                result.ProfileToken,
                result.PlayTokens[index],
                publicBaseUrl,
                readyNzbFileNames.Contains(ProfileReleaseName.ToNzbFileName(candidate.Title)),
                isVerifiedAvailable(candidate.NzbUrl) || candidate.VerifiedAvailable))
            .ToList();
        return new ProfileAddonStreamResponse { Streams = streams };
    }

    public static ProfileAddonStream CreateStream(
        NzbResolutionCache.Candidate candidate,
        string type,
        string token,
        string playToken,
        string publicBaseUrl,
        bool inLibrary,
        bool verifiedAvailable)
    {
        var indexer = string.IsNullOrWhiteSpace(candidate.SourceIndexerName)
            ? candidate.IndexerName
            : candidate.SourceIndexerName;
        var audioLanguages = ProfileLanguageMetadata.NormalizeAudioLanguages(candidate.Language);
        var subtitleLanguages = ProfileLanguageMetadata.NormalizeSubtitleLanguages(candidate.Subs);
        var description = BuildDescription(
            candidate,
            indexer,
            inLibrary,
            verifiedAvailable,
            audioLanguages,
            subtitleLanguages);

        return new ProfileAddonStream
        {
            Name = $"[NZB] {indexer}",
            Description = description,
            Title = description,
            Url = $"{publicBaseUrl.TrimEnd('/')}/adapters/addon/{token}/play/{playToken}.mkv",
            BehaviorHints = new ProfileAddonStreamBehaviorHints
            {
                Filename = candidate.Title,
                VideoSize = candidate.Size,
                BingeGroup = $"nzbdav|{indexer}|{type}",
                NotWebReady = true,
            },
            Meta = new ProfileAddonStreamMeta
            {
                Indexer = indexer,
                InLibrary = inLibrary ? true : null,
                Availability = verifiedAvailable ? "available" : null,
                Languages = audioLanguages.Count > 0 ? audioLanguages : null,
                SubtitleLanguages = subtitleLanguages.Count > 0 ? subtitleLanguages : null,
            },
            FailoverId = playToken,
            Extra = new ProfileAddonStreamExtra { FailoverId = playToken },
        };
    }

    private static string BuildDescription(
        NzbResolutionCache.Candidate candidate,
        string indexer,
        bool inLibrary,
        bool verifiedAvailable,
        List<string> audioLanguages,
        List<string> subtitleLanguages)
    {
        var details = new List<string> { $"💾 {FormatBytes(candidate.Size)}" };
        if (candidate.Posted is { } posted)
            details.Add($"📅 {FormatAge(DateTimeOffset.UtcNow - posted)}");

        var lines = new List<string>
        {
            candidate.Title,
            string.Join(" | ", details),
            $"🌐 {indexer}",
        };
        var markers = new List<string>();
        if (inLibrary) markers.Add("⚡ Ready");
        if (verifiedAvailable) markers.Add("✅ Verified");
        markers.AddRange(ProfileLanguageMetadata.AudioFlagMarkers(audioLanguages));
        if (markers.Count > 0)
            lines.Add(string.Join(" ", markers));
        if (subtitleLanguages.Count > 0)
            lines.Add($"💬 Subs: {string.Join(", ", subtitleLanguages)}");
        return string.Join('\n', lines);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "?";
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        var index = 0;
        double value = bytes;
        while (value >= 1024 && index < suffixes.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.##} {suffixes[index]}";
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalDays >= 365) return $"{(int)(age.TotalDays / 365)}y";
        if (age.TotalDays >= 1) return $"{(int)age.TotalDays}d";
        if (age.TotalHours >= 1) return $"{(int)age.TotalHours}h";
        return $"{Math.Max(1, (int)age.TotalMinutes)}m";
    }
}
