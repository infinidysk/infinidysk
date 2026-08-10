using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using NzbWebDAV.Config;

namespace NzbWebDAV.Clients.Prowlarr;

public interface IProwlarrClient
{
    Task<ProwlarrSystemStatus> GetStatusAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ProwlarrIndexer>> GetIndexersAsync(CancellationToken ct = default);
}

public interface IProwlarrClientFactory
{
    IProwlarrClient Create(string baseUrl, string apiKey);
}

public class ProwlarrClientFactory : IProwlarrClientFactory
{
    public virtual IProwlarrClient Create(string baseUrl, string apiKey) =>
        new ProwlarrClient(baseUrl, apiKey);
}

public class ProwlarrClient : IProwlarrClient
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly HttpClient _http;

    public ProwlarrClient(string baseUrl, string apiKey)
        : this(SharedHttpClient, baseUrl, apiKey)
    {
    }

    internal ProwlarrClient(HttpClient http, string baseUrl, string apiKey)
    {
        _baseUrl = NormalizeBaseUrl(baseUrl);
        _apiKey = apiKey;
        _http = http;
    }

    public static string NormalizeBaseUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("Prowlarr URL must be an absolute http(s) URL without credentials, query, or fragment.");
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    public static string BuildIndexerApiUrl(string baseUrl, int indexerId) =>
        $"{NormalizeBaseUrl(baseUrl)}/{indexerId}/api";

    public Task<ProwlarrSystemStatus> GetStatusAsync(CancellationToken ct = default) =>
        GetAsync<ProwlarrSystemStatus>("system/status", ct);

    public async Task<IReadOnlyList<ProwlarrIndexer>> GetIndexersAsync(CancellationToken ct = default)
    {
        var indexers = await GetAsync<List<ProwlarrIndexer>>("indexer", ct).ConfigureAwait(false);
        foreach (var indexer in indexers)
        {
            if (indexer.Id <= 0 || string.IsNullOrWhiteSpace(indexer.Name))
                throw new InvalidDataException("Prowlarr returned an indexer without a valid ID or name.");
        }

        return indexers;
    }

    private async Task<T> GetAsync<T>(string resource, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/v1/{resource}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd($"NzbDAV/{ConfigManager.AppVersion}");
        request.Headers.TryAddWithoutValidation("X-Api-Key", _apiKey);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var reason = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "Prowlarr rejected the API key."
                : $"Prowlarr returned HTTP {(int)response.StatusCode}.";
            throw new ProwlarrClientException(reason);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct).ConfigureAwait(false)
                   ?? throw new InvalidDataException("Prowlarr returned an empty response.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("Prowlarr returned invalid JSON.", e);
        }
    }
}

public sealed class ProwlarrClientException(string message) : Exception(message);

public sealed class ProwlarrSystemStatus
{
    [JsonPropertyName("version")] public string? Version { get; set; }
}

public sealed class ProwlarrIndexer
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("enable")] public bool Enable { get; set; }
    [JsonPropertyName("supportsSearch")] public bool SupportsSearch { get; set; }
    [JsonPropertyName("protocol")] public string? Protocol { get; set; }
}
