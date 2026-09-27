using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace NzbWebDAV.Services.Plex;

/// <summary>Bounded Plex transport. Configure its HttpClient with redirects disabled.</summary>
public sealed class PlexApiClient(HttpClient http, string installationId)
{
    public string InstallationId => installationId;
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private const int MaximumPages = 20;

    public async Task<PlexPin> StartPinAsync(CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Post, new Uri("https://plex.tv/api/v2/pins"));
        request.Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("strong", "true")]);
        using var json = ParseJson(await SendAsync(request, ct).ConfigureAwait(false));
        return ParsePin(json.RootElement);
    }

    public async Task<PlexPin> PollPinAsync(int pin, CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Get, new Uri($"https://plex.tv/api/v2/pins/{pin}"));
        using var json = ParseJson(await SendAsync(request, ct).ConfigureAwait(false));
        return ParsePin(json.RootElement);
    }

    public async Task<IReadOnlyList<PlexDiscoveredServer>> DiscoverAsync(string token, CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Get, new Uri("https://plex.tv/api/v2/resources?includeHttps=1"), token);
        using var json = ParseJson(await SendAsync(request, ct).ConfigureAwait(false));
        if (json.RootElement.ValueKind != JsonValueKind.Array) throw new PlexRequestException("Plex returned an invalid resource list.");
        var result = new List<PlexDiscoveredServer>();
        foreach (var item in json.RootElement.EnumerateArray().Take(128))
        {
            var provides = Text(item, "provides")?.Split(',') ?? [];
            var role = item.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array &&
                roles.EnumerateArray().Any(value => value.GetString() == "server");
            if (!provides.Contains("server") && !role) continue;
            var id = Text(item, "clientIdentifier");
            if (string.IsNullOrEmpty(id)) continue;
            var connections = new List<PlexConnection>();
            if (item.TryGetProperty("connections", out var candidates) && candidates.ValueKind == JsonValueKind.Array)
                foreach (var candidate in candidates.EnumerateArray().Take(32))
                {
                    var address = Text(candidate, "uri");
                    try
                    {
                        var uri = PlexSettings.ValidateServerUri(address ?? "");
                        connections.Add(new(uri.ToString().TrimEnd('/'), Boolean(candidate, "local"), Boolean(candidate, "relay")));
                    }
                    catch (ArgumentException) { /* Unsafe advertised candidates are not selectable. */ }
                }
            result.Add(new(id, Text(item, "name") ?? "Plex", Text(item, "accessToken") ?? token, connections));
        }
        return result;
    }

    public async Task<PlexIdentity> TestServerAsync(PlexServer server, CancellationToken ct = default)
    {
        var document = await GetXmlAsync(server, "/identity", ct).ConfigureAwait(false);
        var id = Attribute(document.Root, "machineIdentifier");
        if (string.IsNullOrWhiteSpace(id)) throw new PlexRequestException("Plex did not return a server identity.");
        if (!string.IsNullOrEmpty(server.Id) && server.Id != id)
            throw new PlexRequestException("The selected endpoint belongs to a different Plex server.");
        // /identity can be public; it alone does not prove that the supplied token works.
        _ = await GetXmlAsync(server, "/library/sections", ct).ConfigureAwait(false);
        return new(id, Attribute(document.Root, "version") ?? "");
    }

    public async Task<PlexAccount> GetAccountAsync(string token, CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Get, new Uri("https://plex.tv/api/v2/user"), token);
        using var json = ParseJson(await SendAsync(request, ct).ConfigureAwait(false));
        var item = json.RootElement;
        var id = Text(item, "uuid") ?? ScalarText(item, "id");
        var name = Text(item, "username") ?? Text(item, "title");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            throw new PlexRequestException("Plex returned an invalid account identity.");
        return new(id, name, token);
    }

    public async Task<IReadOnlyList<PlexHomeUser>> GetHomeUsersAsync(string accountToken, CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Get, new Uri("https://plex.tv/api/v2/home/users"), accountToken);
        using var json = ParseJson(await SendAsync(request, ct).ConfigureAwait(false));
        var array = json.RootElement;
        if (array.ValueKind == JsonValueKind.Object && array.TryGetProperty("users", out var users)) array = users;
        if (array.ValueKind != JsonValueKind.Array) throw new PlexRequestException("Plex returned an invalid Home user list.");
        return array.EnumerateArray().Take(100).Select(item => new PlexHomeUser(Text(item, "uuid") ?? ScalarText(item, "id") ?? "",
            Text(item, "title") ?? Text(item, "username") ?? "", Boolean(item, "protected"), Boolean(item, "admin")))
            .Where(user => user.Id.Length > 0).ToArray();
    }

    public async Task<string> SwitchHomeUserAsync(string accountToken, string userId, string? pin = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(pin) && (pin.Length != 4 || pin.Any(character => !char.IsAsciiDigit(character))))
            throw new ArgumentException("A Plex Home PIN must contain four digits.");
        using var request = Request(HttpMethod.Post, new Uri($"https://plex.tv/api/v2/home/users/{Identifier(userId)}/switch"), accountToken);
        request.Content = new FormUrlEncodedContent(string.IsNullOrEmpty(pin) ? [] : [new KeyValuePair<string, string>("pin", pin)]);
        using var json = ParseJson(await SendAsync(request, ct).ConfigureAwait(false));
        var token = Text(json.RootElement, "authToken");
        return !string.IsNullOrWhiteSpace(token) ? token : throw new PlexRequestException("Plex did not authorize the selected Home user.");
    }

    public async Task<PlexMediaItem> GetMetadataAsync(PlexServer server, string ratingKey, CancellationToken ct = default)
    {
        var items = await ReadPagesAsync(server, $"/library/metadata/{Identifier(ratingKey)}", 1, ["Video", "Directory"], ct).ConfigureAwait(false);
        var item = items.Count > 0 ? ParseMedia(items[0]) : null;
        return item?.RatingKey == ratingKey ? item : throw new PlexRequestException("Plex metadata is unavailable for the selected item.");
    }

    public async Task<IReadOnlyList<PlexLibrary>> GetLibrariesAsync(PlexServer server, CancellationToken ct = default) =>
        (await ReadPagesAsync(server, "/library/sections", 2000, ["Directory"], ct).ConfigureAwait(false))
        .Select(item => new PlexLibrary(Attribute(item, "key") ?? "", Attribute(item, "title") ?? "", Attribute(item, "type") ?? ""))
        .Where(item => item.Id.Length > 0 && item.Type is "movie" or "show").ToArray();

    public async Task<IReadOnlyList<PlexUser>> GetUsersAsync(PlexServer server, CancellationToken ct = default) =>
        (await ReadPagesAsync(server, "/accounts", 2000, ["Account"], ct).ConfigureAwait(false))
        .Select(item => new PlexUser(Attribute(item, "id") ?? "", Attribute(item, "name") ?? Attribute(item, "title") ?? ""))
        .Where(item => item.Id.Length > 0).ToArray();

    public async Task<IReadOnlyList<PlexSource>> GetCollectionsAsync(PlexServer server, string libraryId,
        CancellationToken ct = default)
    {
        var collections = await ReadPagesAsync(server,
            $"/library/sections/{Identifier(libraryId)}/collections", 2000, ["Directory"], ct).ConfigureAwait(false);
        return collections.Select(item => new PlexSource(server.Id, libraryId, "collection",
            Attribute(item, "ratingKey") ?? Attribute(item, "key") ?? "",
            Attribute(item, "key") ?? $"/library/collections/{Identifier(Attribute(item, "ratingKey") ?? "")}/children",
            Attribute(item, "title") ?? "", Attribute(item, "type") ?? ""))
            .Where(source => source.Id.Length > 0 && IsSafeSourceKey(source.Key)).ToArray();
    }

    public async Task<IReadOnlyList<PlexSource>> GetHubsAsync(PlexServer server, string? libraryId,
        CancellationToken ct = default)
    {
        var path = string.IsNullOrEmpty(libraryId) ? "/hubs" : $"/hubs/sections/{Identifier(libraryId)}";
        var hubs = await ReadPagesAsync(server, path, 2000, ["Hub"], ct).ConfigureAwait(false);
        return hubs.Select(item => new PlexSource(server.Id, libraryId, "hub",
            Attribute(item, "hubIdentifier") ?? Attribute(item, "key") ?? "", Attribute(item, "key") ?? "",
            Attribute(item, "title") ?? "", Attribute(item, "type") ?? ""))
            .Where(source => source.Id.Length > 0 && IsSafeSourceKey(source.Key)).ToArray();
    }

    public async Task<IReadOnlyList<PlexMediaItem>> GetPreviewAsync(PlexServer server, string key, int limit, CancellationToken ct = default)
    {
        ValidateSourceKey(key);
        return (await ReadPagesAsync(server, key, Math.Clamp(limit, 1, 1000), ["Video", "Directory"], ct).ConfigureAwait(false))
            .Select(ParseMedia).ToArray();
    }

    public async Task<IReadOnlyList<PlexMediaItem>> GetNextEpisodesAsync(PlexServer server, string showRatingKey, int limit, CancellationToken ct = default) =>
        (await ReadPagesAsync(server, $"/library/metadata/{Identifier(showRatingKey)}/allLeaves?sort=parentIndex%3Aasc%2Cindex%3Aasc",
            Math.Clamp(limit, 1, 200), ["Video"], ct).ConfigureAwait(false)).Select(ParseMedia).ToArray();

    public async Task<IReadOnlyList<PlexMediaItem>> GetNextEpisodesAsync(PlexServer server, PlexMediaItem current, int limit, CancellationToken ct = default)
    {
        var show = current.ShowRatingKey ?? current.RatingKey;
        var scopedUser = !string.IsNullOrEmpty(current.UserId) && server.AccountId == current.UserId;
        PlexMediaItem Project(XElement element) => ParseMedia(element) with
        { UserId = current.UserId, WatchStateUserId = scopedUser ? current.UserId : null };
        if (scopedUser && current.Type == "show")
            return (await ReadPagesAsync(server,
                $"/library/metadata/{Identifier(show)}/allLeaves?unwatched=1&sort=parentIndex%3Aasc%2Cindex%3Aasc",
                Math.Clamp(limit, 1, 20), ["Video"], ct,
                element =>
                {
                    var media = ParseMedia(element);
                    return (Long(element, "viewCount") ?? 0) == 0 && media.Type == "episode"
                        && media.ShowRatingKey == show && media.Season > 0 && media.Episode > 0;
                }).ConfigureAwait(false)).Select(Project).ToArray();
        var seasons = await ReadPagesAsync(server, $"/library/metadata/{Identifier(show)}/children", 200, ["Directory"], ct).ConfigureAwait(false);
        var result = new List<PlexMediaItem>();
        var maximum = Math.Clamp(limit, 1, 20);
        foreach (var season in seasons.Where(season => Long(season, "index") is > 0
                && Long(season, "index") >= (current.Season ?? 1)).OrderBy(season => Long(season, "index")).Take(3))
        {
            if (Attribute(season, "ratingKey") is not { } key) continue;
            // Query the relevant seasons, not the first N episodes of the entire show.
            // Only a credential verified for the initiating user can filter watched state.
            var watchedFilter = scopedUser ? "&unwatched=1" : "";
            var episodes = await ReadPagesAsync(server, $"/library/metadata/{Identifier(key)}/children?sort=index%3Aasc{watchedFilter}",
                maximum - result.Count, ["Video"], ct, element =>
                {
                    var media = ParseMedia(element);
                    return (!scopedUser || (Long(element, "viewCount") ?? 0) == 0)
                        && media.ShowRatingKey == show && media.Season > 0 && media.Episode > 0
                        && (current.Type == "show" || media.Season > current.Season
                            || media.Season == current.Season && media.Episode > current.Episode);
                }).ConfigureAwait(false);
            result.AddRange(episodes.Select(Project));
            if (result.Count >= maximum) break;
        }
        return result.DistinctBy(item => item.RatingKey).Take(maximum).ToArray();
    }

    public async Task<IReadOnlyList<PlexMediaItem>> GetHistoryAsync(PlexServer server, DateTimeOffset since, int limit = 1000, CancellationToken ct = default) =>
        (await ReadPagesAsync(server, $"/status/sessions/history/all?viewedAt%3E={since.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}",
            Math.Clamp(limit, 1, 2000), ["Video"], ct).ConfigureAwait(false)).Select(ParseMedia)
        .Where(item => item.ViewedAt >= since.ToUnixTimeSeconds()).ToArray();

    public async Task<IReadOnlyList<PlexSession>> GetSessionsAsync(PlexServer server, CancellationToken ct = default) =>
        (await ReadPagesAsync(server, "/status/sessions", 1000, ["Video"], ct).ConfigureAwait(false))
        .Select(item => new PlexSession(Attribute(item.Element("Session"), "id") ?? Attribute(item, "sessionKey") ?? "",
            Attribute(item.Element("User"), "id") ?? "", Attribute(item.Element("Player"), "state") ?? "",
            ParseMedia(item).File, ParseMedia(item))).ToArray();

    private async Task<IReadOnlyList<XElement>> ReadPagesAsync(PlexServer server, string path, int limit, string[] elementNames, CancellationToken ct,
        Func<XElement, bool>? include = null)
    {
        var result = new List<XElement>();
        var start = 0;
        var remainingBytes = MaximumResponseBytes;
        for (var page = 0; page < MaximumPages && result.Count < limit; page++)
        {
            var size = include is null ? Math.Min(100, limit - result.Count) : 100;
            var separator = path.Contains('?') ? '&' : '?';
            var document = await GetXmlAsync(server, $"{path}{separator}X-Plex-Container-Start={start}&X-Plex-Container-Size={size}", ct,
                bytes =>
                {
                    remainingBytes -= bytes;
                    if (remainingBytes < 0) throw new PlexRequestException("Plex catalogue exceeded the aggregate response limit.");
                }).ConfigureAwait(false);
            var roots = document.Root?.Elements().ToArray() ?? [];
            var items = roots.Where(item => elementNames.Contains(item.Name.LocalName))
                .Concat(roots.Where(item => item.Name.LocalName == "Hub" && !elementNames.Contains("Hub"))
                    .SelectMany(hub => hub.Elements().Where(item => elementNames.Contains(item.Name.LocalName))))
                .ToArray();
            // Do not retain the document and unrelated siblings through each element's parent.
            result.AddRange(items.Where(item => include?.Invoke(item) != false).Take(limit - result.Count).Select(item => new XElement(item)));
            if (items.Length == 0) break;
            start += items.Length;
            var total = Long(document.Root, "totalSize");
            if (total is null || start >= total) break;
        }
        return result;
    }

    private async Task<XDocument> GetXmlAsync(PlexServer server, string path, CancellationToken ct, Action<int>? consumeBytes = null)
    {
        var root = PlexSettings.ValidateServerUri(server.Url);
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal)) throw new ArgumentException("Invalid Plex resource path.");
        var target = new Uri(root.ToString().TrimEnd('/') + path, UriKind.Absolute);
        if (target.Scheme != root.Scheme || target.Authority != root.Authority) throw new ArgumentException("Plex resource must remain on its selected server.");
        using var request = Request(HttpMethod.Get, target, server.Token);
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("application/xml");
        var bytes = await SendAsync(request, ct).ConfigureAwait(false);
        consumeBytes?.Invoke(bytes.Length);
        try
        {
            using var stream = new MemoryStream(bytes);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumResponseBytes });
            return XDocument.Load(reader);
        }
        catch (XmlException) { throw new PlexRequestException("Plex returned malformed XML."); }
    }

    private HttpRequestMessage Request(HttpMethod method, Uri uri, string? token = null)
    {
        if (token is not null && (token.Length > 8192 || token.Any(char.IsControl)))
            throw new PlexRequestException("Plex returned an invalid credential.");
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-Plex-Product", "InfiniDysk");
        request.Headers.Add("X-Plex-Client-Identifier", installationId);
        request.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrEmpty(token)) request.Headers.Add("X-Plex-Token", token);
        return request;
    }

    private async Task<byte[]> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeout = http.Timeout == Timeout.InfiniteTimeSpan ? TimeSpan.FromSeconds(15) : http.Timeout;
        deadline.CancelAfter(timeout > TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(15) : timeout);
        var bounded = deadline.Token;
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bounded).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new PlexRequestException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "Plex authorization failed; reconnect or update the server token." : "Plex request failed.");
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new PlexRequestException("Plex response exceeded the size limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(bounded).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(buffer, bounded).ConfigureAwait(false)) > 0)
            {
                if (output.Length + read > MaximumResponseBytes) throw new PlexRequestException("Plex response exceeded the size limit.");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (HttpRequestException) { throw new PlexRequestException("Plex could not be reached."); }
        catch (IOException) { throw new PlexRequestException("Plex response could not be read."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new PlexRequestException("Plex request timed out."); }
    }

    public static void ValidateSourceKey(string key)
    {
        if (!IsSafeSourceKey(key)) throw new ArgumentException("Plex source key must be a relative library or hub resource without credentials.");
    }

    private static bool IsSafeSourceKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 2048 || key.Contains('\\') || key.Contains('#')) return false;
        var decoded = key;
        for (var i = 0; i < 3; i++) decoded = Uri.UnescapeDataString(decoded);
        return (decoded.StartsWith("/library/", StringComparison.Ordinal) || decoded.StartsWith("/hubs/", StringComparison.Ordinal)) &&
            !decoded.Contains("..", StringComparison.Ordinal) && !decoded.Contains("//", StringComparison.Ordinal) &&
            !decoded.Contains("x-plex-token", StringComparison.OrdinalIgnoreCase) && !decoded.Contains('\\') &&
            !decoded.Any(char.IsControl);
    }

    private static string Identifier(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new ArgumentException("Invalid Plex resource identifier.");
        return value;
    }
    private static JsonDocument ParseJson(byte[] bytes)
    {
        try { return JsonDocument.Parse(bytes); }
        catch (JsonException) { throw new PlexRequestException("Plex returned malformed JSON."); }
    }
    private static PlexPin ParsePin(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) ||
            !id.TryGetInt32(out var pin) || pin <= 0) throw new PlexRequestException("Plex returned an invalid login response.");
        return new(pin, Text(item, "code") ?? "",
            item.TryGetProperty("expiresIn", out var expiry) && expiry.TryGetInt32(out var seconds) ? Math.Clamp(seconds, 1, 900) : 900,
            Text(item, "authToken"));
    }
    private static string? Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? ScalarText(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : null;
    private static bool Boolean(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static string? Attribute(XElement? item, string name)
    {
        var value = item?.Attribute(name)?.Value;
        if (value?.Length > 4096) throw new PlexRequestException("Plex returned an oversized metadata field.");
        return value;
    }
    private static long? Long(XElement? item, string name) => long.TryParse(Attribute(item, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static int? Integer(XElement item, string name) => int.TryParse(Attribute(item, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static PlexMediaItem ParseMedia(XElement item)
    {
        var media = item.Elements("Media").FirstOrDefault(element => Attribute(element, "selected") == "1") ?? item.Element("Media");
        var part = media?.Elements("Part").FirstOrDefault(element => Attribute(element, "selected") == "1") ?? media?.Element("Part");
        return new(Attribute(item, "ratingKey") ?? "", Attribute(item, "type") ?? "", Attribute(item, "title") ?? "",
            Attribute(item, "grandparentRatingKey"), Integer(item, "parentIndex"), Integer(item, "index"), Attribute(part, "file"),
            Long(item, "viewOffset") ?? 0, Long(item, "duration") ?? 0, Long(item, "viewedAt"),
            Attribute(item, "accountID") ?? Attribute(item.Element("User"), "id"));
    }
}
