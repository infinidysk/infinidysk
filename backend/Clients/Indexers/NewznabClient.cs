using System.Xml;
using System.Xml.Linq;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Clients.Indexers;

public class NewznabClient
{
    private static readonly XNamespace Newznab = "http://www.newznab.com/DTD/2010/feeds/attributes/";

    private readonly Uri _apiUri;
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _userAgent;
    private readonly int _timeoutSeconds;
    private readonly long _maxResponseBytes;

    public NewznabClient(
        string baseUrl,
        string apiKey,
        long maxResponseBytes,
        string userAgent = "NzbDav",
        string? proxyUrl = null,
        int timeoutSeconds = 30,
        bool skipTlsVerification = false)
        : this(
            ProxyHttpClientPool.GetClient(proxyUrl, skipTlsVerification),
            baseUrl,
            apiKey,
            maxResponseBytes,
            userAgent,
            timeoutSeconds)
    {
    }

    internal NewznabClient(
        HttpClient http,
        string baseUrl,
        string apiKey,
        long maxResponseBytes,
        string userAgent = "NzbDav",
        int timeoutSeconds = 30)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResponseBytes);
        _http = http;
        _apiUri = NormalizeApiUri(baseUrl);
        _apiKey = apiKey;
        _userAgent = userAgent;
        _timeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : 30;
        _maxResponseBytes = maxResponseBytes;
    }

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
            req.Headers.UserAgent.ParseAdd(_userAgent);
            try
            {
                return await _http
                    .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (attempt == 0 && !ct.IsCancellationRequested
                                      && e is HttpRequestException or IOException)
            {
                // Transient network failure on the first attempt; loop retries once.
            }
        }
    }

    private async Task<byte[]> ReadResponseBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await HttpContentReadUtil
                .ReadBoundedAsync(response.Content, _maxResponseBytes, ct)
                .ConfigureAwait(false);
        }
        catch (NzbResponseTooLargeException e)
        {
            throw new RemoteResponseTooLargeException(e.MaxBytes, e.ContentLength, e);
        }
    }

    private static async Task<XDocument> ParseXmlAsync(
        byte[] body,
        long maxResponseBytes,
        CancellationToken ct)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = maxResponseBytes,
            CloseInput = false,
        };

        try
        {
            using var stream = new MemoryStream(body, writable: false);
            using var reader = XmlReader.Create(stream, settings);
            return await XDocument
                .LoadAsync(reader, LoadOptions.None, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (XmlException e)
        {
            throw new RemoteResponseFormatException("Indexer returned invalid XML.", e);
        }
    }

    private static bool HasCapsElement(XDocument doc)
    {
        if (doc.Root?.Name.LocalName.Equals("caps", StringComparison.OrdinalIgnoreCase) == true)
            return true;
        return doc.Descendants().Any(e =>
            e.Name.LocalName.Equals("caps", StringComparison.OrdinalIgnoreCase));
    }

    public Task<bool> TestAsync(CancellationToken ct = default)
    {
        return WithTimeoutAsync(async token =>
        {
            var url = BuildUrl(new[]
            {
                new KeyValuePair<string, string>("t", "caps"),
                new KeyValuePair<string, string>("apikey", _apiKey),
            });
            using var resp = await GetAsync(url, token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            var body = await ReadResponseBodyAsync(resp, token).ConfigureAwait(false);
            var doc = await ParseXmlAsync(body, _maxResponseBytes, token).ConfigureAwait(false);
            return HasCapsElement(doc);
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
                new("apikey", _apiKey),
                new("extended", "1"),
            };
            foreach (var (k, v) in queryParams)
                extra.Add(new KeyValuePair<string, string>(k, v));
            var url = BuildUrl(extra);
            using var resp = await GetAsync(url, token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var body = await ReadResponseBodyAsync(resp, token).ConfigureAwait(false);
            var doc = await ParseXmlAsync(body, _maxResponseBytes, token).ConfigureAwait(false);
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
