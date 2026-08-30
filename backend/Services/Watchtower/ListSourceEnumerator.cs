using System.Text;
using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

public class ListSourceEnumerator
{
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    private const int MaxCatalogPages = 100;

    private readonly HttpClient _http;
    private readonly Func<long> _getMaxResponseBytes;

    public ListSourceEnumerator(ConfigManager configManager)
        : this(
            ProxyHttpClientPool.GetClient(null),
            configManager.GetWatchtowerListSourceMaxResponseBytes)
    {
    }

    internal ListSourceEnumerator(HttpClient http, Func<long> getMaxResponseBytes)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(getMaxResponseBytes);
        _http = http;
        _getMaxResponseBytes = getMaxResponseBytes;
    }

    public async Task<IReadOnlyList<WtContentRef>> EnumerateAsync(ListSource source, CancellationToken ct)
    {
        return source.Kind switch
        {
            ListSource.KindStremioCatalog => await FetchStremioCatalogAsync(source.Name, source.Url, source.Cap, ct).ConfigureAwait(false),
            ListSource.KindUrlList => await FetchUrlListAsync(source.Url, ct).ConfigureAwait(false),
            _ => Array.Empty<WtContentRef>(),
        };
    }

    private async Task<IReadOnlyList<WtContentRef>> FetchStremioCatalogAsync(
        string sourceName, string? url, int cap, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return Array.Empty<WtContentRef>();

        var refs = new List<WtContentRef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var limit = cap > 0 ? cap : int.MaxValue;
        var pageSize = 0;

        var page = 0;
        for (; page < MaxCatalogPages; page++)
        {
            var skip = pageSize > 0 ? page * pageSize : 0;
            var body = await HttpGetBodyAsync(BuildPagedUrl(url!, skip), ct).ConfigureAwait(false);
            if (body is null)
            {
                if (page == 0)
                    throw new ListSourceGuidanceException("Catalog request failed or returned an empty response.");
                break;
            }

            using var doc = ParseJsonOrThrow(body.Bytes);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("metas", out var metas) || metas.ValueKind != JsonValueKind.Array)
            {
                if (page > 0) break;
                if (root.ValueKind == JsonValueKind.Object &&
                    (root.TryGetProperty("catalogs", out _) || root.TryGetProperty("resources", out _)))
                    throw new ListSourceGuidanceException(
                        "This URL is an addon manifest, not a catalog. Use \"Discover catalogs\" to pick which " +
                        "catalogs to add, or point this list at a catalog endpoint such as .../catalog/movie/<id>.json.");
                throw new ListSourceGuidanceException("Catalog response did not contain a \"metas\" array.");
            }

            int pageCount = 0, newCount = 0;
            foreach (var meta in metas.EnumerateArray())
            {
                pageCount++;
                var type = GetStr(meta, "type");
                var id = GetStr(meta, "id");
                if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id)) continue;
                if (!seen.Add($"{NormalizeType(type!)}:{id!}")) continue;
                newCount++;
                refs.Add(new WtContentRef { Type = NormalizeType(type!), ContentId = id!, Title = GetStr(meta, "name") });
                if (refs.Count >= limit) return refs;
            }

            if (pageSize == 0) pageSize = pageCount;
            if (pageCount == 0 || newCount == 0) break;
            if (pageCount < pageSize) break;
        }

        if (page >= MaxCatalogPages)
            Log.Information(
                "Watchtower: source {Source} reached the {Max}-page ceiling ({Count} titles); later titles were not pulled",
                sourceName, MaxCatalogPages, refs.Count);

        return refs;
    }

    public async Task<DiscoverResult> DiscoverCatalogsAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ListSourceGuidanceException("A manifest URL is required.");

        var manifestUrl = NormalizeManifestUrl(url.Trim());
        var body = await HttpGetBodyAsync(manifestUrl, ct).ConfigureAwait(false);
        if (body is null)
            throw new ListSourceGuidanceException("Could not fetch the addon manifest.");

        using var doc = ParseJsonOrThrow(body.Bytes);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("catalogs", out var catalogs) || catalogs.ValueKind != JsonValueKind.Array)
        {
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("metas", out _))
                throw new ListSourceGuidanceException(
                    "That looks like a catalog endpoint, not a manifest. Add it directly as a Stremio catalog list.");
            throw new ListSourceGuidanceException("No catalogs were found in this addon manifest.");
        }

        var addonName = GetStr(root, "name");
        var baseUrl = StripManifestSuffix(manifestUrl);
        var choices = new List<CatalogChoice>();
        foreach (var cat in catalogs.EnumerateArray())
        {
            if (cat.ValueKind != JsonValueKind.Object) continue;
            var type = GetStr(cat, "type");
            var id = GetStr(cat, "id");
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id)) continue;
            var name = GetStr(cat, "name");
            choices.Add(new CatalogChoice
            {
                Type = type!,
                Id = id!,
                Name = string.IsNullOrWhiteSpace(name) ? $"{type} · {id}" : name!,
                Url = BuildCatalogUrl(baseUrl, type!, id!),
                ExtraRequired = DescribeRequiredExtra(cat),
            });
        }

        if (choices.Count == 0)
            throw new ListSourceGuidanceException("This addon manifest lists no usable catalogs.");

        return new DiscoverResult { AddonName = addonName, Catalogs = choices };
    }

    private static string NormalizeManifestUrl(string url)
    {
        if (url.StartsWith("stremio://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url["stremio://".Length..];
        if (!url.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            url = url.TrimEnd('/') + "/manifest.json";
        return url;
    }

    private static string StripManifestSuffix(string manifestUrl)
    {
        const string suffix = "/manifest.json";
        if (manifestUrl.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return manifestUrl[..^suffix.Length];
        var slash = manifestUrl.LastIndexOf('/');
        return slash > "https://".Length ? manifestUrl[..slash] : manifestUrl;
    }

    private static string BuildCatalogUrl(string baseUrl, string type, string id)
        => $"{baseUrl}/catalog/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(id)}.json";

    private static string BuildPagedUrl(string url, int skip)
    {
        if (skip <= 0) return url;

        const string ext = ".json";
        if (!url.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            return url.Contains('?', StringComparison.Ordinal) ? $"{url}&skip={skip}" : $"{url}?skip={skip}";

        var stem = url[..^ext.Length];
        const string marker = "/catalog/";
        var markerIdx = stem.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIdx < 0) return $"{stem}/skip={skip}{ext}";

        var head = stem[..(markerIdx + marker.Length)];
        var segments = stem[(markerIdx + marker.Length)..].Split('/');

        if (segments.Length >= 3 && segments[^1].Contains('=', StringComparison.Ordinal))
        {
            var merged = segments[^1].Split('&')
                .Where(p => !p.StartsWith("skip=", StringComparison.OrdinalIgnoreCase))
                .Append($"skip={skip}");
            segments[^1] = string.Join('&', merged);
            return $"{head}{string.Join('/', segments)}{ext}";
        }

        return $"{stem}/skip={skip}{ext}";
    }

    private static string? DescribeRequiredExtra(JsonElement cat)
    {
        var names = new List<string>();
        if (cat.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in extra.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                if (!(e.TryGetProperty("isRequired", out var r) && r.ValueKind == JsonValueKind.True)) continue;
                var nm = GetStr(e, "name");
                if (!string.IsNullOrWhiteSpace(nm) && !nm!.Equals("skip", StringComparison.OrdinalIgnoreCase))
                    names.Add(nm);
            }
        }
        else if (cat.TryGetProperty("extraRequired", out var er) && er.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in er.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.String) continue;
                var nm = e.GetString();
                if (!string.IsNullOrWhiteSpace(nm) && !nm!.Equals("skip", StringComparison.OrdinalIgnoreCase))
                    names.Add(nm);
            }
        }
        return names.Count > 0 ? string.Join(", ", names) : null;
    }

    private static JsonDocument ParseJsonOrThrow(ReadOnlyMemory<byte> body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException e)
        {
            throw new RemoteResponseFormatException(
                "The addon response was not valid JSON.",
                e);
        }
    }

    public sealed class CatalogChoice
    {
        public required string Type { get; init; }
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Url { get; init; }
        public string? ExtraRequired { get; init; }
    }

    public sealed class DiscoverResult
    {
        public string? AddonName { get; init; }
        public required IReadOnlyList<CatalogChoice> Catalogs { get; init; }
    }

    private async Task<IReadOnlyList<WtContentRef>> FetchUrlListAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return Array.Empty<WtContentRef>();
        var fetched = await HttpGetBodyAsync(url!, ct).ConfigureAwait(false);
        if (fetched is null)
            throw new ListSourceGuidanceException("List request failed or returned an empty response.");

        if (LooksLikeJson(fetched.Bytes))
            return ParseJsonListOrThrow(fetched.Bytes);

        return ParsePlainList(DecodePlainText(fetched));
    }

    private static bool LooksLikeJson(ReadOnlySpan<byte> body)
    {
        var i = 0;
        if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF)
            i = 3;
        while (i < body.Length)
        {
            var b = body[i];
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                i++;
                continue;
            }

            return b is (byte)'[' or (byte)'{';
        }

        return false;
    }

    private static List<WtContentRef> ParseJsonListOrThrow(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var refs = new List<WtContentRef>();
            var arr = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement
                : (doc.RootElement.TryGetProperty("items", out var items) ? items : default);
            if (arr.ValueKind != JsonValueKind.Array) return refs;
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var (type, id) = SplitTypeId(el.GetString() ?? "");
                    if (id.Length > 0) refs.Add(new WtContentRef { Type = type, ContentId = id });
                }
                else if (el.ValueKind == JsonValueKind.Object)
                {
                    var id = GetStr(el, "id") ?? GetStr(el, "imdb") ?? "";
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    refs.Add(new WtContentRef
                    {
                        Type = NormalizeType(GetStr(el, "type") ?? "movie"),
                        ContentId = id,
                        Title = GetStr(el, "name") ?? GetStr(el, "title"),
                    });
                }
            }

            return refs;
        }
        catch (JsonException e)
        {
            throw new RemoteResponseFormatException("The list response was not valid JSON.", e);
        }
    }

    private static List<WtContentRef> ParsePlainList(string body)
    {
        var refs = new List<WtContentRef>();
        using var reader = new StringReader(body);
        string? raw;
        while ((raw = reader.ReadLine()) is not null)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var (type, id) = SplitTypeId(line);
            if (id.Length == 0) continue;
            refs.Add(new WtContentRef { Type = type, ContentId = id });
        }

        return refs;
    }

    private static string DecodePlainText(FetchedBody fetched)
    {
        var text = fetched.Encoding.GetString(fetched.Bytes);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    private static (string Type, string Id) SplitTypeId(string line)
    {
        if (line.StartsWith("tt", StringComparison.OrdinalIgnoreCase) && !line.Contains(':', StringComparison.Ordinal))
            return ("movie", line);

        var firstColon = line.IndexOf(':', StringComparison.Ordinal);
        if (firstColon > 0)
        {
            var maybeType = line[..firstColon].ToLowerInvariant();
            if (maybeType is "movie" or "series" or "tv" or "show")
                return (NormalizeType(maybeType), line[(firstColon + 1)..]);
        }
        return ("movie", line);
    }

    private static string NormalizeType(string type)
    {
        type = type.Trim().ToLowerInvariant();
        return type is "series" or "tv" or "show" ? "series" : "movie";
    }

    private static string? GetStr(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private async Task<FetchedBody?> HttpGetBodyAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "NzbDav-Watchtower");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(FetchTimeout);
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var encoding = GetContentEncoding(response.Content);
            try
            {
                var bytes = await HttpContentReadUtil
                    .ReadBoundedAsync(response.Content, _getMaxResponseBytes(), timeoutCts.Token)
                    .ConfigureAwait(false);
                return new FetchedBody(bytes, encoding);
            }
            catch (NzbResponseTooLargeException e)
            {
                throw new RemoteResponseTooLargeException(e.MaxBytes, e.ContentLength, e);
            }
        }
        catch (RemoteResponseException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (
            e is HttpRequestException or InvalidOperationException
            || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            Log.Debug("Watchtower: remote list fetch failed ({FailureType})", e.GetType().Name);
            return null;
        }
    }

    private static Encoding GetContentEncoding(HttpContent content)
    {
        var charset = content.Headers.ContentType?.CharSet?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(charset)) return Encoding.UTF8;
        try
        {
            return Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    private sealed record FetchedBody(byte[] Bytes, Encoding Encoding);
}
