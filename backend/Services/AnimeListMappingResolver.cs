using System.Net;
using System.Text.Json;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Cross-id fallback for anime ids. Kitsu's own mapping table (see <see cref="ExternalIdResolver"/>)
/// is incomplete, so when it returns no tvdb/imdb id we consult the community-maintained
/// Fribb/anime-lists dataset, which frequently carries the id Kitsu's API omits.
/// The dataset is a single large json file; we download, index, and cache it in memory.
/// </summary>
public class AnimeListMappingResolver
{
    private const string DatasetUrl =
        "https://raw.githubusercontent.com/Fribb/anime-lists/master/anime-list-full.json";

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FirstLoadWait = TimeSpan.FromSeconds(8);

    private static readonly HttpClient HttpClient = CreateClient();

    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private volatile Index _index = Index.Empty;

    public async Task<AnimeMapping?> LookupAsync(string provider, long externalId, CancellationToken ct)
    {
        var index = _index;
        if (index.IsStale(RefreshInterval))
        {
            if (index.HasData)
            {
                // serve the (stale) data we already have and refresh in the background
                _ = Task.Run(() => LoadIfDueAsync(CancellationToken.None));
            }
            else
            {
                // cold start: wait briefly for the first load, then let it finish in the background
                var load = LoadIfDueAsync(CancellationToken.None);
                try { await load.WaitAsync(FirstLoadWait, ct).ConfigureAwait(false); }
                catch (TimeoutException) { }
                index = _index;
            }
        }

        return index.Get(provider, externalId);
    }

    private async Task LoadIfDueAsync(CancellationToken ct)
    {
        if (!_index.IsDueForAttempt(RefreshInterval, RetryInterval)) return;
        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_index.IsDueForAttempt(RefreshInterval, RetryInterval)) return;
            var attemptAt = DateTimeOffset.UtcNow;
            try
            {
                var (byKitsu, byMal, byAnilist) = await DownloadAndIndexAsync(ct).ConfigureAwait(false);
                _index = new Index(byKitsu, byMal, byAnilist, DateTimeOffset.UtcNow, attemptAt);
                Log.Information(
                    "Anime-list mapping loaded ({Kitsu} kitsu / {Mal} mal / {Anilist} anilist ids)",
                    byKitsu.Count, byMal.Count, byAnilist.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // keep any previously-loaded data; throttle retries via LastAttempt
                _index = _index with { LastAttempt = attemptAt };
                Log.Warning("Anime-list mapping load failed: {Message}", ex.Message);
            }
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private static async Task<(Dictionary<long, AnimeMapping>, Dictionary<long, AnimeMapping>, Dictionary<long, AnimeMapping>)>
        DownloadAndIndexAsync(CancellationToken ct)
    {
        using var resp = await HttpClient
            .GetAsync(DatasetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var byKitsu = new Dictionary<long, AnimeMapping>();
        var byMal = new Dictionary<long, AnimeMapping>();
        var byAnilist = new Dictionary<long, AnimeMapping>();

        await foreach (var el in JsonSerializer
            .DeserializeAsyncEnumerable<JsonElement>(stream, cancellationToken: ct).ConfigureAwait(false))
        {
            if (el.ValueKind != JsonValueKind.Object) continue;

            var tvdb = GetPositiveInt(el, "tvdb_id");
            var imdb = GetImdb(el, "imdb_id");
            if (tvdb is null && imdb is null) continue;

            var isMovie = el.TryGetProperty("type", out var typeEl)
                          && typeEl.ValueKind == JsonValueKind.String
                          && string.Equals(typeEl.GetString(), "MOVIE", StringComparison.OrdinalIgnoreCase);
            var mapping = new AnimeMapping(tvdb, imdb, isMovie, GetTvdbSeason(el));

            if (GetLong(el, "kitsu_id") is { } kitsu) byKitsu[kitsu] = mapping;
            if (GetLong(el, "mal_id") is { } mal) byMal[mal] = mapping;
            if (GetLong(el, "anilist_id") is { } anilist) byAnilist[anilist] = mapping;
        }

        return (byKitsu, byMal, byAnilist);
    }

    private static long? GetLong(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.Number when p.TryGetInt64(out var v) => v,
            JsonValueKind.String when long.TryParse(p.GetString(), out var v) => v,
            _ => null,
        };
    }

    private static int? GetPositiveInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.Number when p.TryGetInt32(out var v) && v > 0 => v,
            JsonValueKind.String when int.TryParse(p.GetString(), out var v) && v > 0 => v,
            _ => null,
        };
    }

    // dataset carries the canonical tvdb season per entry, e.g. "season": { "tvdb": 2 }.
    // season 0 is valid (tvdb specials), so allow >= 0.
    private static int? GetTvdbSeason(JsonElement el)
    {
        if (!el.TryGetProperty("season", out var season) || season.ValueKind != JsonValueKind.Object) return null;
        if (!season.TryGetProperty("tvdb", out var t) || t.ValueKind != JsonValueKind.Number) return null;
        return t.TryGetInt32(out var v) && v >= 0 ? v : null;
    }

    private static string? GetImdb(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) return null;
        var raw = p.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // the dataset occasionally stores several comma-separated ids; take the first valid one
        var first = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return first is not null && first.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? first : null;
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("InfiniDysk (https://github.com/infinidysk/infinidysk)");
        return client;
    }

    private sealed record Index(
        Dictionary<long, AnimeMapping> ByKitsu,
        Dictionary<long, AnimeMapping> ByMal,
        Dictionary<long, AnimeMapping> ByAnilist,
        DateTimeOffset LoadedAt,
        DateTimeOffset LastAttempt)
    {
        public static readonly Index Empty =
            new(new(), new(), new(), DateTimeOffset.MinValue, DateTimeOffset.MinValue);

        public bool HasData => LoadedAt != DateTimeOffset.MinValue;

        public bool IsStale(TimeSpan ttl) => DateTimeOffset.UtcNow - LoadedAt > ttl;

        public bool IsDueForAttempt(TimeSpan ttl, TimeSpan retry) =>
            IsStale(ttl) && DateTimeOffset.UtcNow - LastAttempt > retry;

        public AnimeMapping? Get(string provider, long id)
        {
            var dict = provider switch
            {
                "kitsu" => ByKitsu,
                "mal" => ByMal,
                "anilist" => ByAnilist,
                _ => null,
            };
            return dict is not null && dict.TryGetValue(id, out var m) ? m : null;
        }
    }
}

public sealed record AnimeMapping(int? TvdbId, string? ImdbId, bool IsMovie, int? TvdbSeason);
