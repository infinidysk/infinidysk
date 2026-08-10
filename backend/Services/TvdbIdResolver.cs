using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Serilog;

namespace NzbWebDAV.Services;

public class TvdbIdResolver
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    public async Task<int?> GetTvdbIdAsync(string imdbDigits, CancellationToken ct)
    {
        if (_cache.TryGetValue(imdbDigits, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.TvdbId;

        var tvmazeTask = TryTvmazeAsync(imdbDigits, ct);
        var wikidataTask = TryWikidataAsync(imdbDigits, ct);
        var titleTask = TryWikidataTitleAndYearAsync(imdbDigits, ct);

        var tvdb = await tvmazeTask.ConfigureAwait(false)
                   ?? await wikidataTask.ConfigureAwait(false);
        if (tvdb is null)
        {
            var (title, year) = await titleTask.ConfigureAwait(false);
            tvdb = await TryTvmazeByTitleAsync(title, year, ct).ConfigureAwait(false);
        }

        if (tvdb is null)
            Log.Information("TvdbResolver: no tvdb id for tt{Imdb}; TV search will fall back to imdbid", imdbDigits);
        else
            Log.Information("TvdbResolver: resolved tt{Imdb} to tvdb {Tvdb}", imdbDigits, tvdb);

        _cache[imdbDigits] = new CacheEntry(tvdb, DateTimeOffset.UtcNow.Add(tvdb is null ? NegativeTtl : CacheTtl));
        return tvdb;
    }

    private async Task<int?> TryTvmazeAsync(string imdbDigits, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.tvmaze.com/lookup/shows?imdb=tt{imdbDigits}";
            using var resp = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                LogStatus("tvmaze", (int)resp.StatusCode, $"tt{imdbDigits}");
                return null;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("externals", out var externals)) return null;
            if (!externals.TryGetProperty("thetvdb", out var tvdbElement)) return null;
            if (tvdbElement.ValueKind == JsonValueKind.Number && tvdbElement.TryGetInt32(out var tvdbId))
            {
                Log.Debug("TvdbResolver: tvmaze mapped tt{Imdb} to tvdb {Tvdb}", imdbDigits, tvdbId);
                return tvdbId;
            }
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            if (!ct.IsCancellationRequested)
                Log.Debug("TvdbResolver: tvmaze lookup failed for tt{Imdb}: {Message}", imdbDigits, e.Message);
            return null;
        }
    }

    private async Task<int?> TryWikidataAsync(string imdbDigits, CancellationToken ct)
    {
        try
        {
            var query = $"SELECT ?tvdb WHERE {{ ?item wdt:P345 \"tt{imdbDigits}\" . ?item wdt:P4835 ?tvdb . }} LIMIT 1";
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
            var tvdbStr = bindings[0].GetProperty("tvdb").GetProperty("value").GetString();
            if (int.TryParse(tvdbStr, out var tvdbId))
            {
                Log.Debug("TvdbResolver: wikidata mapped tt{Imdb} to tvdb {Tvdb}", imdbDigits, tvdbId);
                return tvdbId;
            }
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            if (!ct.IsCancellationRequested)
                Log.Debug("TvdbResolver: wikidata lookup failed for tt{Imdb}: {Message}", imdbDigits, e.Message);
            return null;
        }
    }

    // TVmaze's imdb lookup and Wikidata's P345->P4835 link both miss some titles
    // (specials/docs TVmaze never mapped to an imdb id, items lacking a tvdb statement).
    // Last resort: resolve the title from Wikidata, then recover the tvdb id via TVmaze's
    // name search. Guarded by exact name match (and year when available) to avoid collisions.
    private async Task<int?> TryTvmazeByTitleAsync(string? title, int? year, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        try
        {
            var url = $"https://api.tvmaze.com/singlesearch/shows?q={Uri.EscapeDataString(title)}";
            using var resp = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                LogStatus("tvmaze-title", (int)resp.StatusCode, title);
                return null;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            if (!NamesEqual(name, title)) return null;

            if (year is { } expectedYear
                && root.TryGetProperty("premiered", out var premEl)
                && premEl.ValueKind == JsonValueKind.String
                && premEl.GetString() is { Length: >= 4 } prem
                && int.TryParse(prem[..4], out var premYear)
                && Math.Abs(premYear - expectedYear) > 1)
            {
                return null;
            }

            if (!root.TryGetProperty("externals", out var externals)) return null;
            if (!externals.TryGetProperty("thetvdb", out var tvdbElement)) return null;
            if (tvdbElement.ValueKind == JsonValueKind.Number && tvdbElement.TryGetInt32(out var tvdbId))
            {
                Log.Debug("TvdbResolver: tvmaze title search mapped {Title} to tvdb {Tvdb}", title, tvdbId);
                return tvdbId;
            }
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            if (!ct.IsCancellationRequested)
                Log.Debug("TvdbResolver: tvmaze title search failed for {Title}: {Message}", title, e.Message);
            return null;
        }
    }

    private async Task<(string? Title, int? Year)> TryWikidataTitleAndYearAsync(string imdbDigits, CancellationToken ct)
    {
        try
        {
            var query =
                $"SELECT ?label ?date WHERE {{ " +
                $"?item wdt:P345 \"tt{imdbDigits}\" . " +
                $"?item rdfs:label ?label . FILTER(LANG(?label) = \"en\") " +
                $"OPTIONAL {{ ?item wdt:P580 ?start }} " +
                $"OPTIONAL {{ ?item wdt:P577 ?pub }} " +
                $"BIND(COALESCE(?start, ?pub) AS ?date) }} LIMIT 1";
            var url = $"https://query.wikidata.org/sparql?query={Uri.EscapeDataString(query)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/sparql-results+json");
            req.Headers.UserAgent.ParseAdd("InfiniDysk (https://github.com/infinidysk/infinidysk)");
            using var resp = await HttpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                LogStatus("wikidata-title", (int)resp.StatusCode, $"tt{imdbDigits}");
                return (null, null);
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var bindings = doc.RootElement.GetProperty("results").GetProperty("bindings");
            if (bindings.GetArrayLength() == 0) return (null, null);
            var row = bindings[0];
            var title = row.TryGetProperty("label", out var l) && l.TryGetProperty("value", out var lv)
                ? lv.GetString()
                : null;
            int? year = null;
            if (row.TryGetProperty("date", out var d) && d.TryGetProperty("value", out var dv)
                && dv.GetString() is { Length: >= 4 } ds && int.TryParse(ds[..4], out var y))
            {
                year = y;
            }
            return (string.IsNullOrWhiteSpace(title) ? null : title, year);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            if (!ct.IsCancellationRequested)
                Log.Debug("TvdbResolver: wikidata title lookup failed for tt{Imdb}: {Message}", imdbDigits, e.Message);
            return (null, null);
        }
    }

    private static void LogStatus(string source, int status, string id)
    {
        if (status == 429 || status >= 500)
            Log.Warning("TvdbResolver: {Source} returned HTTP {Status} for {Id} — rate-limited or unavailable", source, status, id);
        else
            Log.Debug("TvdbResolver: {Source} returned HTTP {Status} for {Id}", source, status, id);
    }

    private static bool NamesEqual(string? a, string? b)
    {
        var na = NormalizeName(a);
        return na.Length > 0 && na == NormalizeName(b);
    }

    private static string NormalizeName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (char.IsWhiteSpace(ch) || ch is '-' or ':' or '.' or '\'' or '_') sb.Append(' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed record CacheEntry(int? TvdbId, DateTimeOffset ExpiresAt);
}
