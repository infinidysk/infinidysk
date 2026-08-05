using System.Collections.Concurrent;
using System.Text.Json;

namespace NzbWebDAV.Services;

public class TmdbIdResolver
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    public async Task<string?> GetImdbIdAsync(string type, string tmdbId, CancellationToken ct)
    {
        var property = type switch
        {
            "movie" => "P4947",
            "series" => "P4983",
            _ => null,
        };
        if (property is null) return null;
        if (!ulong.TryParse(tmdbId, out _)) return null;

        var key = $"{type}|{tmdbId}";
        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.ImdbId;

        var imdb = await ResolveAsync(property, tmdbId, ct).ConfigureAwait(false);
        _cache[key] = new CacheEntry(imdb, DateTimeOffset.UtcNow.Add(imdb is null ? NegativeTtl : CacheTtl));
        return imdb;
    }

    private static async Task<string?> ResolveAsync(string property, string tmdbId, CancellationToken ct)
    {
        try
        {
            var query = $"SELECT ?imdb WHERE {{ ?item wdt:{property} \"{tmdbId}\" . ?item wdt:P345 ?imdb . }} LIMIT 1";
            var url = $"https://query.wikidata.org/sparql?query={Uri.EscapeDataString(query)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/sparql-results+json");
            req.Headers.UserAgent.ParseAdd("InfiniDysk (https://github.com/infinidysk/infinidysk)");
            using var resp = await HttpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var bindings = doc.RootElement.GetProperty("results").GetProperty("bindings");
            if (bindings.GetArrayLength() == 0) return null;
            var imdb = bindings[0].GetProperty("imdb").GetProperty("value").GetString();
            if (string.IsNullOrWhiteSpace(imdb)) return null;
            return imdb.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? imdb : $"tt{imdb}";
        }
        catch
        {
            return null;
        }
    }

    private sealed record CacheEntry(string? ImdbId, DateTimeOffset ExpiresAt);
}
