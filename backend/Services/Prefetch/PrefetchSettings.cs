using System.Text.Json;
using System.Text.Json.Serialization;

namespace NzbWebDAV.Services.Prefetch;

public sealed record PrefetchSource
{
    public string ServerId { get; init; } = "";
    public string LibraryId { get; init; } = "";
    public string Kind { get; init; } = "hub";
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string Type { get; init; } = "movie";
    public bool Enabled { get; init; } = true;
    public int Limit { get; init; } = 10;
    public string[] ExcludedShows { get; init; } = [];
}

public sealed record PrefetchLibraryIdentity(string ServerId = "", string LibraryId = "", string Type = "movie");

public sealed record PrefetchSettings
{
    public bool Enabled { get; init; }
    public bool HistoryEnabled { get; init; }
    public bool RealtimeEnabled { get; init; } = true;
    public bool ReadActivityEnabled { get; init; }
    public bool PredictionsEnabled { get; init; } = true;
    public bool MinimumWarmEnabled { get; init; }
    public bool FullFileWarming { get; init; } = true;
    public bool MovieEnabled { get; init; } = true;
    public bool TvEnabled { get; init; } = true;
    public bool WarmLocalFiles { get; init; }
    public bool PauseDuringPlayback { get; init; } = true;
    public int SyncIntervalMinutes { get; init; } = 15;
    public int RealtimeCheckIntervalSeconds { get; init; } = 30;
    public int MovieSyncIntervalMinutes { get; init; } = 60;
    public int TvSyncIntervalMinutes { get; init; } = 60;
    public int LookbackDays { get; init; } = 7;
    public int MoviePartialWatchLookbackDays { get; init; } = 7;
    public int MinEpisodesForPrediction { get; init; } = 2;
    public double ConfidenceThreshold { get; init; } = 0.5;
    public int CooldownMinutes { get; init; } = 15;
    public int QueueCapacity { get; init; } = 256;
    public int MaxRetries { get; init; } = 3;
    public int IntentTtlHours { get; init; } = 24;
    public int VerifiedSessionExpirySeconds { get; init; } = 60;
    public int MaxQueueAhead { get; init; } = 2;
    public int TvEpisodesPerShow { get; init; } = 2;
    public int MaxConcurrentJobs { get; init; } = 1;
    public int ConnectionsPerJob { get; init; } = 2;
    public long MaxBytesPerItem { get; init; } = 500_000_000_000;
    public long DailyByteBudget { get; init; } = 10_000_000_000;
    public int MinimumHeadMb { get; init; } = 16;
    public int MinimumTailMb { get; init; } = 8;
    public string[] Users { get; init; } = [];
    public PrefetchSource[] Sources { get; init; } = [];
    public PrefetchLibraryIdentity[] DisabledLibraries { get; init; } = [];
    public static string LibraryType(PrefetchSource source) => source.Type == "movie" ? "movie" : "show";
    public bool IsLibraryDisabled(PrefetchSource source) => DisabledLibraries.Any(library =>
        library.ServerId == source.ServerId && library.LibraryId == source.LibraryId
        && library.Type == LibraryType(source));
    public static PrefetchSettings Parse(string? json)
    {
        PrefetchSettings settings;
        try
        {
            settings = string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<PrefetchSettings>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
                ?? throw new ArgumentException("Smart Prefetch settings must be an object.");
        }
        catch (JsonException) { throw new ArgumentException("Smart Prefetch settings contain invalid or unknown properties."); }
        if (settings.MaxConcurrentJobs is < 1 or > 4 || settings.ConnectionsPerJob is < 1 or > 8
            || settings.RealtimeCheckIntervalSeconds is < 5 or > 3600
            || settings.SyncIntervalMinutes is < 1 or > 1440 || settings.MovieSyncIntervalMinutes is < 1 or > 1440
            || settings.TvSyncIntervalMinutes is < 1 or > 1440 || settings.LookbackDays is < 1 or > 365
            || settings.MoviePartialWatchLookbackDays is < 1 or > 365 || settings.MinEpisodesForPrediction is < 1 or > 100
            || !double.IsFinite(settings.ConfidenceThreshold) || settings.ConfidenceThreshold is < 0 or > 1
            || settings.CooldownMinutes is < 1 or > 1440
            || settings.QueueCapacity is < 1 or > 256 || settings.MaxRetries is < 0 or > 10
            || settings.IntentTtlHours is < 1 or > 168 || settings.VerifiedSessionExpirySeconds is < 5 or > 300
            || settings.MaxQueueAhead is < 1 or > 20 || settings.TvEpisodesPerShow is < 1 or > 20
            || settings.MaxBytesPerItem is <= 0 or > 100_000_000_000_000 || settings.DailyByteBudget is < 0 or > 100_000_000_000_000
            || settings.MinimumHeadMb is < 0 or > 1024 || settings.MinimumTailMb is < 0 or > 1024)
            throw new ArgumentException("Smart Prefetch concurrency, intervals, prediction limits, or byte budgets are out of range.");
        if (settings.Users is null || settings.Users.Length > 256 || settings.Users.Any(user => string.IsNullOrWhiteSpace(user) || user.Length > 256)
            || settings.Sources is null || settings.Sources.Length > 128
            || settings.DisabledLibraries is null || settings.DisabledLibraries.Length > 128)
            throw new ArgumentException("Smart Prefetch user, source, and disabled-library selections must be bounded arrays.");
        var libraryKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var library in settings.DisabledLibraries)
        {
            if (library is null || string.IsNullOrWhiteSpace(library.ServerId) || library.ServerId.Length > 128
                || library.LibraryId is null || library.LibraryId.Length > 128 || library.Type is not ("movie" or "show")
                || !libraryKeys.Add(library.ServerId + "\n" + library.LibraryId + "\n" + library.Type))
                throw new ArgumentException("Smart Prefetch disabled libraries need unique server/library/media identities.");
        }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in settings.Sources)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.ServerId) || source.ServerId.Length > 128
                || source.LibraryId is null || source.LibraryId.Length > 128 || source.Title is null || source.Title.Length > 512
                || source.Kind is not ("hub" or "collection") || source.Type is not ("movie" or "show" or "episode" or "clip")
                || source.Limit is < 1 or > 1000 || source.ExcludedShows is null || source.ExcludedShows.Length > 1000
                || source.ExcludedShows.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128)
                || string.IsNullOrWhiteSpace(source.Key) || source.Key.Length > 2048 || !source.Key.StartsWith('/') || source.Key.StartsWith("//", StringComparison.Ordinal)
                || source.Key.Any(char.IsControl) || !keys.Add(source.ServerId + "\n" + source.Kind + "\n" + source.Key))
                throw new ArgumentException("Smart Prefetch sources need unique server-relative hub/collection paths and valid limits/exclusions.");
        }
        return settings;
    }
}
