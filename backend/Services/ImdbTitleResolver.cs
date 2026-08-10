using System.Collections.Concurrent;
using System.Text.Json;
using Serilog;

namespace NzbWebDAV.Services;

public class ImdbTitleResolver
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    public async Task<string?> GetTitleAsync(string type, string? imdbDigits, int? tvdbId, CancellationToken ct)
    {
        var key = $"{type}|{imdbDigits ?? ""}|{tvdbId?.ToString() ?? ""}";
        if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
            return entry.Title;

        string? title = null;
        try
        {
            if (type == "series")
            {
                title = await TryTvmazeAsync(imdbDigits, tvdbId, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(title) && imdbDigits is not null)
                    title = await TryWikidataAsync(imdbDigits, ct).ConfigureAwait(false);
            }
            else if (type == "movie" && imdbDigits is not null)
            {
                title = await TryWikidataAsync(imdbDigits, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            Log.Warning("ImdbTitleResolver lookup failed for {Key}: {Message}", key, ex.Message);
        }

        _cache[key] = new CacheEntry(title, DateTimeOffset.UtcNow.Add(title is null ? NegativeTtl : CacheTtl));
        return title;
    }

    private static async Task<string?> TryTvmazeAsync(string? imdbDigits, int? tvdbId, CancellationToken ct)
    {
        string? url = null;
        if (!string.IsNullOrEmpty(imdbDigits))
            url = $"https://api.tvmaze.com/lookup/shows?imdb=tt{imdbDigits}";
        else if (tvdbId.HasValue)
            url = $"https://api.tvmaze.com/lookup/shows?thetvdb={tvdbId.Value}";
        if (url is null) return null;

        using var resp = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            LogStatus("tvmaze", (int)resp.StatusCode, imdbDigits is not null ? $"tt{imdbDigits}" : $"tvdb{tvdbId}");
            return null;
        }
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        if (!doc.RootElement.TryGetProperty("name", out var nameEl)) return null;
        var name = nameEl.GetString();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static async Task<string?> TryWikidataAsync(string imdbDigits, CancellationToken ct)
    {
        var query = $"SELECT ?label WHERE {{ ?item wdt:P345 \"tt{imdbDigits}\" . ?item rdfs:label ?label . FILTER(LANG(?label) = \"en\") }} LIMIT 1";
        var url = $"https://query.wikidata.org/sparql?query={Uri.EscapeDataString(query)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/sparql-results+json");
        req.Headers.UserAgent.ParseAdd("InfiniDysk (https://github.com/infinidysk/infinidysk)");
        using var resp = await HttpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            LogStatus("wikidata", (int)resp.StatusCode, $"tt{imdbDigits}");
            return null;
        }
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var bindings = doc.RootElement.GetProperty("results").GetProperty("bindings");
        if (bindings.GetArrayLength() == 0) return null;
        var label = bindings[0].GetProperty("label").GetProperty("value").GetString();
        return string.IsNullOrWhiteSpace(label) ? null : label;
    }

    private static void LogStatus(string source, int status, string id)
    {
        if (status == 429 || status >= 500)
            Log.Warning("ImdbTitleResolver: {Source} returned HTTP {Status} for {Id} — rate-limited or unavailable", source, status, id);
        else
            Log.Debug("ImdbTitleResolver: {Source} returned HTTP {Status} for {Id}", source, status, id);
    }

    private sealed record CacheEntry(string? Title, DateTimeOffset ExpiresAt);
}
