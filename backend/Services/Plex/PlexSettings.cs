using System.Text.Json;
using System.Text.Json.Serialization;
using NzbWebDAV.Config;

namespace NzbWebDAV.Services.Plex;

public static class PlexSettings
{
    public const string ServersKey = "plex.servers";
    public const string AccountsKey = "plex.accounts";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions StrictJsonOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static IReadOnlyList<PlexServer> ParseServers(string? json, bool strict = false)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var servers = JsonSerializer.Deserialize<PlexServer[]>(json, strict ? StrictJsonOptions : JsonOptions)
                ?? throw new ArgumentException("Plex servers must be a JSON array.");
            if (servers.Length > 32) throw new ArgumentException("At most 32 Plex servers can be configured.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var server in servers)
            {
                if (server is null || string.IsNullOrWhiteSpace(server.Id) || server.Id.Length > 128 || !ids.Add(server.Id))
                    throw new ArgumentException("Plex servers require unique machine identifiers.");
                if (server.AccountId is not null && (string.IsNullOrWhiteSpace(server.AccountId) || server.AccountId.Length > 128))
                    throw new ArgumentException("Invalid linked Plex account identity.");
                ValidateServerUri(server.Url);
                if (string.IsNullOrWhiteSpace(server.Token) || server.Token.Length > 8192 || server.Token.Any(char.IsControl))
                    throw new ArgumentException("Each Plex server requires a token.");
                if (server.Name is null || server.Name.Length > 256 || server.PathMappings is null || server.PathMappings.Count > 64 ||
                    server.PathMappings.Any(mapping => !IsValidMapping(mapping)))
                    throw new ArgumentException("Plex path mappings must contain bounded absolute DAV paths.");
            }
            return servers;
        }
        catch (JsonException) { throw new ArgumentException("Plex servers contain invalid JSON."); }
    }

    public static IReadOnlyList<PlexAccount> ParseAccounts(string? json, bool strict = false)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var accounts = JsonSerializer.Deserialize<PlexAccount[]>(json, strict ? StrictJsonOptions : JsonOptions)
                ?? throw new ArgumentException("Plex accounts must be a JSON array.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (accounts.Length > 16 || accounts.Any(account => account is null || string.IsNullOrWhiteSpace(account.Id) ||
                account.Id.Length > 128 || !ids.Add(account.Id) || string.IsNullOrWhiteSpace(account.Name) || account.Name.Length > 256 ||
                string.IsNullOrWhiteSpace(account.Token) || account.Token.Length > 8192 || account.Token.Any(char.IsControl)))
                throw new ArgumentException("Plex accounts require unique bounded identities and credentials; at most 16 accounts are supported.");
            return accounts;
        }
        catch (JsonException) { throw new ArgumentException("Plex accounts contain invalid JSON."); }
    }

    public static void ValidateItem(string configName, string configValue, bool rejectUnknownJsonProperties = false)
    {
        if (configName is not (ServersKey or AccountsKey) || string.IsNullOrWhiteSpace(configValue)) return;
        if (ConfigSecretMasker.IsMaskToken(configValue)) return;
        if (configName == ServersKey) _ = ParseServers(configValue, rejectUnknownJsonProperties);
        if (configName == AccountsKey) _ = ParseAccounts(configValue, rejectUnknownJsonProperties);
    }

    public static Uri ValidateServerUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Plex server URL must be HTTP(S), without credentials, query, or fragment.");
        return uri;
    }

    private static bool IsValidMapping(PlexPathMapping? mapping)
    {
        if (mapping is null || !SafeAbsolutePath(mapping.PlexPath)) return false;
        var dav = !string.IsNullOrWhiteSpace(mapping.DavPath);
        var local = !string.IsNullOrWhiteSpace(mapping.LocalPath);
        return dav != local && (dav ? SafeAbsolutePath(mapping.DavPath) && mapping.DavPath!.StartsWith('/')
            : SafeAbsolutePath(mapping.LocalPath));
    }

    private static bool SafeAbsolutePath(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 4096 &&
        (Path.IsPathRooted(value) || (value.Length > 2 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '/' or '\\')) &&
        !value.Split(['/', '\\']).Any(part => part is "." or "..") && !value.Any(char.IsControl);
}
