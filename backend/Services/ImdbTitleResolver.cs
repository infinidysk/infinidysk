using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Serilog;

namespace NzbWebDAV.Services;

public sealed record ResolvedTitleMetadata(string Title, int? Year);

public class ImdbTitleResolver
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    public async Task<string?> GetTitleAsync(string type, string? imdbDigits, int? tvdbId, CancellationToken ct)
        => (await GetMetadataAsync(type, imdbDigits, tvdbId, ct).ConfigureAwait(false))?.Title;

    public async Task<ResolvedTitleMetadata?> GetMetadataAsync(
        string type, string? imdbDigits, int? tvdbId, CancellationToken ct)
    {
        var key = $"{type}|{imdbDigits ?? ""}|{tvdbId?.ToString() ?? ""}";
        if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
            return entry.Metadata;

        ResolvedTitleMetadata? metadata = null;
        try
        {
            if (type == "series")
            {
                var title = await TryTvmazeAsync(imdbDigits, tvdbId, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(title) && imdbDigits is not null)
                    title = (await TryWikidataAsync(imdbDigits, ct).ConfigureAwait(false))?.Title;
                // Series premiere/release years are not used for the movie-only year gate.
                if (!string.IsNullOrWhiteSpace(title))
                    metadata = new ResolvedTitleMetadata(title, Year: null);
            }
            else if (type == "movie" && imdbDigits is not null)
            {
                metadata = await TryWikidataAsync(imdbDigits, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            Log.Warning("ImdbTitleResolver lookup failed for {Key}: {Message}", key, ex.Message);
            Log.Debug(ex, "ImdbTitleResolver known lookup failure stack for {Key}", key);
        }

        _cache[key] = new CacheEntry(metadata, DateTimeOffset.UtcNow.Add(metadata is null ? NegativeTtl : CacheTtl));
        return metadata;
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
        return ParseTvmazeTitle(doc.RootElement);
    }

    private static async Task<ResolvedTitleMetadata?> TryWikidataAsync(string imdbDigits, CancellationToken ct)
    {
        var query =
            $"SELECT ?label ?date WHERE {{ ?item wdt:P345 \"tt{imdbDigits}\" . ?item rdfs:label ?label . FILTER(LANG(?label) = \"en\") OPTIONAL {{ ?item wdt:P577 ?date }} }}";
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
        return ParseWikidataMetadata(doc.RootElement);
    }

    internal static string? ParseTvmazeTitle(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("name", out var nameEl)) return null;
        var name = nameEl.GetString();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    internal static ResolvedTitleMetadata? ParseWikidataMetadata(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("results", out var results)
            || !results.TryGetProperty("bindings", out var bindings)
            || bindings.ValueKind != JsonValueKind.Array
            || bindings.GetArrayLength() == 0)
        {
            return null;
        }

        string? title = null;
        int? earliestYear = null;
        foreach (var binding in bindings.EnumerateArray())
        {
            if (title is null
                && binding.TryGetProperty("label", out var labelEl)
                && TryReadSparqlString(labelEl, out var label)
                && !string.IsNullOrWhiteSpace(label))
            {
                title = label;
            }

            if (TryReadSparqlYear(binding, out var year)
                && (earliestYear is null || year < earliestYear))
            {
                earliestYear = year;
            }
        }

        return title is null ? null : new ResolvedTitleMetadata(title, earliestYear);
    }

    private static bool TryReadSparqlString(JsonElement node, out string? value)
    {
        value = null;
        if (node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty("value", out var valueEl))
        {
            value = valueEl.GetString();
            return true;
        }

        return false;
    }

    private static bool TryReadSparqlYear(JsonElement binding, out int year)
    {
        year = 0;
        if (binding.TryGetProperty("year", out var yearEl)
            && TryReadSparqlString(yearEl, out var yearText)
            && int.TryParse(yearText, NumberStyles.Integer, CultureInfo.InvariantCulture, out year)
            && year is >= 1900 and <= 2199)
        {
            return true;
        }

        if (binding.TryGetProperty("date", out var dateEl)
            && TryReadSparqlString(dateEl, out var dateText)
            && dateText is { Length: >= 4 }
            && int.TryParse(dateText.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out year)
            && year is >= 1900 and <= 2199)
        {
            return true;
        }

        return false;
    }

    private static void LogStatus(string source, int status, string id)
    {
        if (status == 429 || status >= 500)
            Log.Warning("ImdbTitleResolver: {Source} returned HTTP {Status} for {Id} — rate-limited or unavailable", source, status, id);
        else
            Log.Debug("ImdbTitleResolver: {Source} returned HTTP {Status} for {Id}", source, status, id);
    }

    private sealed record CacheEntry(ResolvedTitleMetadata? Metadata, DateTimeOffset ExpiresAt);
}
