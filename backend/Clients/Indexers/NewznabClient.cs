using System.Xml.Linq;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Clients.Indexers;

public class NewznabClient(
    string baseUrl,
    string apiKey,
    string userAgent = "NzbDav",
    string? proxyUrl = null,
    int timeoutSeconds = 30,
    bool skipTlsVerification = false)
{
    private static readonly XNamespace Newznab = "http://www.newznab.com/DTD/2010/feeds/attributes/";

    private readonly Uri _apiUri = NormalizeApiUri(baseUrl);
    private readonly HttpClient _http = ProxyHttpClientPool.GetClient(proxyUrl, skipTlsVerification);
    private readonly int _timeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : 30;

    private static Uri NormalizeApiUri(string baseUrl)
    {
        var uri = new Uri(baseUrl, UriKind.Absolute);
        var pathTrimmed = uri.AbsolutePath.TrimEnd('/');
        if (pathTrimmed.EndsWith("/api", StringComparison.OrdinalIgnoreCase)
            || pathTrimmed.Equals("/api", StringComparison.OrdinalIgnoreCase))
        {
            return new UriBuilder(uri) { Path = pathTrimmed }.Uri;
        }
        return new UriBuilder(uri)
        {
            Path = pathTrimmed.Length == 0 ? "/api" : pathTrimmed + "/api",
        }.Uri;
    }

    private string BuildUrl(IEnumerable<KeyValuePair<string, string>> extraParams)
    {
        var parts = new List<string>();
        var existing = _apiUri.Query;
        if (!string.IsNullOrEmpty(existing))
        {
            if (existing.StartsWith('?')) existing = existing[1..];
            if (!string.IsNullOrEmpty(existing)) parts.Add(existing);
        }
        foreach (var kv in extraParams)
            parts.Add($"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}");

        var builder = new UriBuilder(_apiUri) { Query = string.Join("&", parts) };
        return builder.Uri.ToString();
    }

    private async Task<T> WithTimeoutAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            return await work(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"Indexer request timed out after {_timeoutSeconds}s.");
        }
    }

    private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(userAgent);
            try
            {
                return await _http.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (attempt == 0 && !ct.IsCancellationRequested
                                      && e is HttpRequestException or IOException)
            {
                // Transient network failure on the first attempt; loop retries once.
            }
        }
    }

    public Task<bool> TestAsync(CancellationToken ct = default)
    {
        return WithTimeoutAsync(async token =>
        {
            var url = BuildUrl(new[]
            {
                new KeyValuePair<string, string>("t", "caps"),
                new KeyValuePair<string, string>("apikey", apiKey),
            });
            using var resp = await GetAsync(url, token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            var body = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return body.Contains("<caps", StringComparison.OrdinalIgnoreCase);
        }, ct);
    }

    public Task<List<NewznabItem>> SearchAsync(string query, int limit, CancellationToken ct = default)
    {
        return QueryAsync(new Dictionary<string, string>
        {
            ["t"] = "search",
            ["q"] = query,
            ["limit"] = limit.ToString(),
        }, ct);
    }

    public Task<List<NewznabItem>> QueryAsync(IReadOnlyDictionary<string, string> queryParams, CancellationToken ct = default)
    {
        return WithTimeoutAsync(async token =>
        {
            var extra = new List<KeyValuePair<string, string>>
            {
                new("apikey", apiKey),
                new("extended", "1"),
            };
            foreach (var (k, v) in queryParams)
                extra.Add(new KeyValuePair<string, string>(k, v));
            var url = BuildUrl(extra);
            using var resp = await GetAsync(url, token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var doc = await XDocument.LoadAsync(stream, LoadOptions.None, token).ConfigureAwait(false);
            if (doc.Root?.Name.LocalName == "error")
            {
                var code = doc.Root.Attribute("code")?.Value;
                var desc = doc.Root.Attribute("description")?.Value ?? "Indexer returned an error.";
                throw new InvalidOperationException(code is null ? desc : $"[{code}] {desc}");
            }
            var items = doc.Root?.Element("channel")?.Elements("item") ?? [];
            return items.Select(ParseItem).ToList();
        }, ct);
    }

    private static NewznabItem ParseItem(XElement item)
    {
        var attrs = item.Elements(Newznab + "attr")
            .Where(x => x.Attribute("name")?.Value is not null)
            .GroupBy(x => x.Attribute("name")!.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.Attribute("value")?.Value).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "",
                StringComparer.OrdinalIgnoreCase);

        var enclosure = item.Element("enclosure");
        var sizeStr = enclosure?.Attribute("length")?.Value ?? GetAttr(attrs, "size");
        var size = long.TryParse(sizeStr, out var parsedSize) ? parsedSize : 0;

        var nzbUrl = enclosure?.Attribute("url")?.Value
                     ?? item.Element("link")?.Value
                     ?? "";

        DateTimeOffset? posted = null;
        if (DateTimeOffset.TryParse(item.Element("pubDate")?.Value, out var p)) posted = p;

        DateTimeOffset? usenetDate = null;
        var udRaw = GetAttr(attrs, "usenetdate");
        if (!string.IsNullOrEmpty(udRaw) && DateTimeOffset.TryParse(udRaw, out var ud))
            usenetDate = ud;

        var sourceIndexerName =
            GetAttr(attrs, "sourceIndexerName")
            ?? GetAttr(attrs, "hydraIndexerName")
            ?? GetAttr(attrs, "indexer")
            ?? GetAttr(attrs, "provider")
            ?? GetElementText(item, "jackettindexer")
            ?? GetElementText(item, "source")
            ?? GetElementText(item, "indexer")
            ?? GetElementText(item, "provider");

        return new NewznabItem
        {
            Title = item.Element("title")?.Value ?? "",
            Guid = item.Element("guid")?.Value ?? "",
            NzbUrl = nzbUrl,
            Size = size,
            Posted = posted,
            UsenetDate = usenetDate,
            Grabs = ParseNonNegInt(GetAttr(attrs, "grabs")),
            Comments = ParseNonNegInt(GetAttr(attrs, "comments")),
            Password = ParseNonNegInt(GetAttr(attrs, "password")),
            Files = ParseNonNegInt(GetAttr(attrs, "files")),
            Group = GetAttr(attrs, "group"),
            Poster = GetAttr(attrs, "poster"),
            SourceIndexerName = sourceIndexerName,
            Language = GetAttr(attrs, "language"),
            Subs = GetAttr(attrs, "subs"),
            InfoHash = GetAttr(attrs, "infohash"),
        };
    }

    private static string? GetElementText(XElement item, string localName)
    {
        var el = item.Elements().FirstOrDefault(x => string.Equals(x.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));
        var v = el?.Value;
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    private static string? GetAttr(Dictionary<string, string> attrs, string name) =>
        attrs.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v) ? v : null;

    private static int? ParseNonNegInt(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (!int.TryParse(raw, out var n)) return null;
        return n < 0 ? 0 : n;
    }

    public class NewznabItem
    {
        public required string Title { get; init; }
        public required string Guid { get; init; }
        public required string NzbUrl { get; init; }
        public long Size { get; init; }
        public DateTimeOffset? Posted { get; init; }
        public DateTimeOffset? UsenetDate { get; init; }
        public int? Grabs { get; init; }
        public int? Comments { get; init; }
        public int? Password { get; init; }
        public int? Files { get; init; }
        public string? Group { get; init; }
        public string? Poster { get; init; }
        public string? SourceIndexerName { get; init; }
        public string? Language { get; init; }
        public string? Subs { get; init; }
        public string? InfoHash { get; init; }
    }
}
