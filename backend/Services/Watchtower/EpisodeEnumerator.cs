using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using NzbWebDAV.Extensions;
using Serilog;

namespace NzbWebDAV.Services;

public class EpisodeEnumerator
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    public sealed record Episode(int Season, int Number, long? AirDateUnix, string? Title);

    public async Task<IReadOnlyList<Episode>> EnumerateImdbAsync(string imdbId, CancellationToken ct)
    {
        var imdb = NormalizeImdb(imdbId);
        if (imdb is null) return Array.Empty<Episode>();

        if (_cache.TryGetValue(imdb, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Episodes;

        var episodes = await FetchTvmazeAsync(imdb, ct).ConfigureAwait(false);
        _cache[imdb] = new CacheEntry(episodes, DateTimeOffset.UtcNow.Add(episodes.Count == 0 ? NegativeTtl : CacheTtl));
        return episodes;
    }

    public async Task<IReadOnlyList<Episode>> EnumerateKitsuAsync(string kitsuId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(kitsuId) || !kitsuId.All(char.IsDigit)) return Array.Empty<Episode>();

        var cacheKey = "kitsu:" + kitsuId;
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Episodes;

        var episodes = await FetchKitsuEpisodesAsync(kitsuId, ct).ConfigureAwait(false);
        _cache[cacheKey] = new CacheEntry(episodes, DateTimeOffset.UtcNow.Add(episodes.Count == 0 ? NegativeTtl : CacheTtl));
        return episodes;
    }

    private static async Task<IReadOnlyList<Episode>> FetchKitsuEpisodesAsync(string kitsuId, CancellationToken ct)
    {
        try
        {
            var result = new List<Episode>();
            for (var page = 0; page < 6; page++)
            {
                var url = $"https://kitsu.io/api/edge/anime/{kitsuId}/episodes?sort=-number&page%5Blimit%5D=20&page%5Boffset%5D={page * 20}";
                using var resp = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) break;
                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) break;

                var pageCount = 0;
                foreach (var el in data.EnumerateArray())
                {
                    pageCount++;
                    if (!el.TryGetProperty("attributes", out var attrs)) continue;
                    if (!attrs.TryGetProperty("number", out var numEl) || numEl.ValueKind != JsonValueKind.Number) continue;
                    var number = numEl.GetInt32();
                    if (number < 1) continue;

                    long? air = null;
                    if (attrs.TryGetProperty("airDate", out var ad) && ad.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(ad.GetString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
                        air = d.ToUnixTimeSeconds();

                    var title = attrs.TryGetProperty("canonicalTitle", out var tEl) && tEl.ValueKind == JsonValueKind.String
                        ? tEl.GetString()
                        : null;

                    result.Add(new Episode(1, number, air, title));
                }

                if (pageCount < 20) break;
            }

            result.Sort(static (a, b) => a.Number.CompareTo(b.Number));
            return result;
        }
        catch (Exception e) when (!e.IsCancellationException(ct))
        {
            Log.Debug(e, "EpisodeEnumerator: Kitsu episode fetch failed for {Id}", kitsuId);
            return Array.Empty<Episode>();
        }
    }

    private static async Task<IReadOnlyList<Episode>> FetchTvmazeAsync(string imdb, CancellationToken ct)
    {
        try
        {
            var showId = await FetchTvmazeShowIdAsync(imdb, ct).ConfigureAwait(false);
            if (showId is null) return Array.Empty<Episode>();

            var url = $"https://api.tvmaze.com/shows/{showId.Value}/episodes";
            using var resp = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return Array.Empty<Episode>();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<Episode>();

            var result = new List<Episode>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("season", out var seasonEl) || seasonEl.ValueKind != JsonValueKind.Number) continue;
                if (!el.TryGetProperty("number", out var numberEl) || numberEl.ValueKind != JsonValueKind.Number) continue;
                var season = seasonEl.GetInt32();
                var number = numberEl.GetInt32();
                if (season < 1 || number < 1) continue;

                var title = el.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                    ? nameEl.GetString()
                    : null;

                result.Add(new Episode(season, number, ParseAirDateUnix(el), title));
            }

            result.Sort(static (a, b) => a.Season != b.Season ? a.Season.CompareTo(b.Season) : a.Number.CompareTo(b.Number));
            return result;
        }
        catch (Exception e) when (!e.IsCancellationException(ct))
        {
            Log.Debug(e, "EpisodeEnumerator: TVmaze episode fetch failed for {Imdb}", imdb);
            return Array.Empty<Episode>();
        }
    }

    private static async Task<int?> FetchTvmazeShowIdAsync(string imdb, CancellationToken ct)
    {
        var url = $"https://api.tvmaze.com/lookup/shows?imdb={imdb}";
        using var resp = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        if (!doc.RootElement.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) return null;
        return idEl.GetInt32();
    }

    private static long? ParseAirDateUnix(JsonElement el)
    {
        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        if (el.TryGetProperty("airstamp", out var stampEl) && stampEl.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(stampEl.GetString(), CultureInfo.InvariantCulture, styles, out var stamp))
            return stamp.ToUnixTimeSeconds();
        if (el.TryGetProperty("airdate", out var dateEl) && dateEl.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(dateEl.GetString(), CultureInfo.InvariantCulture, styles, out var date))
            return date.ToUnixTimeSeconds();
        return null;
    }

    private static string? NormalizeImdb(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        var colon = s.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0) s = s[..colon];
        if (s.StartsWith("tt", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return s.Length > 0 && s.All(char.IsDigit) ? "tt" + s : null;
    }

    private sealed record CacheEntry(IReadOnlyList<Episode> Episodes, DateTimeOffset ExpiresAt);
}
