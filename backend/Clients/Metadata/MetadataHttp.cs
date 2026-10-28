using System.Globalization;
using System.Net;
using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Clients.Metadata;

/// <summary>
/// Shared plumbing for the TMDB/TVDB HTTP clients: named client identities, bounded
/// response reading, provider-neutral error translation, and tolerant JSON accessors.
/// Every message produced here is safe to show a browser: provider bodies, request
/// URLs, and credentials never reach it.
/// </summary>
internal static class MetadataHttp
{
    internal const string TmdbClientName = "tmdb";
    internal const string TvdbClientName = "tvdb";

    internal const string UserAgent = "InfiniDysk (https://github.com/infinidysk/infinidysk)";

    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Sends <paramref name="request"/> and returns the parsed body, or null when the
    /// provider answered 404 and the caller asked to treat that as absence.
    /// </summary>
    internal static async Task<JsonDocument?> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        string provider,
        bool allowNotFound,
        CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or TaskCanceledException)
        {
            throw CatalogException.Provider($"The {provider} service is unavailable.", DefaultRetryAfter);
        }

        using (response)
        {
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
                return null;
            return await ReadJsonAsync(response, provider, ct).ConfigureAwait(false);
        }
    }

    internal static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        string provider,
        CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw Translate(response, provider);

        byte[] payload;
        try
        {
            payload = await HttpContentReadUtil
                .ReadBoundedAsync(response.Content, ExternalMetadataResponseLimits.HardMaxResponseBytes, ct)
                .ConfigureAwait(false);
        }
        catch (NzbResponseTooLargeException)
        {
            throw CatalogException.Provider($"The {provider} response was too large to read.");
        }

        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            throw CatalogException.Provider($"The {provider} response could not be read.");
        }
    }

    internal static CatalogException Translate(HttpResponseMessage response, string provider)
    {
        var status = (int)response.StatusCode;
        if (status == (int)HttpStatusCode.NotFound)
            return CatalogException.NotFound();
        if (status is 401 or 403)
            return CatalogException.Provider($"The {provider} credentials were rejected.");
        if (status is 429 or >= 500)
            return CatalogException.Provider(
                $"The {provider} service is temporarily unavailable.",
                ReadRetryAfter(response) ?? DefaultRetryAfter);
        return CatalogException.Provider($"The {provider} request failed.");
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            return delta;
        if (retryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
                return wait;
        }

        return null;
    }

    internal static string? ReadString(this JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    internal static int? ReadInt(this JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;
        return value.TryGetInt32(out var number) ? number : null;
    }

    internal static double? ReadDouble(this JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;
        return value.TryGetDouble(out var number) ? number : null;
    }

    /// <summary>Anchors a provider image path on TMDB's CDN, preserving nulls for the UI placeholder.</summary>
    internal static string? BuildTmdbImageUrl(string? path, string size)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path.Trim();
        if (!trimmed.StartsWith('/'))
            return null;
        return string.Create(CultureInfo.InvariantCulture, $"https://image.tmdb.org/t/p/{size}{trimmed}");
    }

    /// <summary>Zero when a provider sent no usable numeric value.</summary>
    internal static double? NormalizeRating(double? rating)
        => rating is { } value && value > 0 ? value : null;
}
