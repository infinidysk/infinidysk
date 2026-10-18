
using System.Text.Json.Serialization;

namespace NzbWebDAV.Config;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MediaServerType
{
    Plex,
    Emby,
    Jellyfin,
}

public sealed class MediaServerConfig
{
    public List<MediaServerInstance> Instances { get; set; } = [];

    public IEnumerable<MediaServerInstance> GetEnabledInstances() =>
        (Instances ?? []).Where(instance => instance is not null && instance.Enabled);

    internal static void Validate(string key, MediaServerConfig? config)
    {
        if (config is null)
            throw new ArgumentException($"Config value for '{key}' must be a JSON object.");
        if (config.Instances is null)
            throw new ArgumentException($"Config value for '{key} Instances' must be an array.");

        var ids = new HashSet<Guid>();
        foreach (var instance in config.Instances)
        {
            if (instance is null)
                throw new ArgumentException($"Config value for '{key} Instances' must not contain null entries.");
            if (instance.Id == Guid.Empty)
                throw new ArgumentException($"Config value for '{key}' contains a media-server instance without an id.");
            if (!ids.Add(instance.Id))
                throw new ArgumentException($"Config value for '{key}' contains duplicate media-server id '{instance.Id}'.");
            if (!Enum.IsDefined(instance.Type))
                throw new ArgumentException($"Config value for '{key}' contains an unsupported media-server type.");
            if (string.IsNullOrWhiteSpace(instance.Name))
                throw new ArgumentException($"Config value for '{key}' contains a media-server instance without a name.");
            if (!TryValidateBaseUrl(instance.BaseUrl))
                throw new ArgumentException(
                    $"Config value for '{key}' media server '{instance.Name}' must use an absolute http(s) base URL without credentials, query, or fragment.");
            if (instance.Enabled && string.IsNullOrWhiteSpace(instance.Token))
                throw new ArgumentException(
                    $"Config value for '{key}' media server '{instance.Name}' requires a token/API key while enabled.");
            if (instance.PathMappings is null)
                throw new ArgumentException(
                    $"Config value for '{key}' media server '{instance.Name}' PathMappings must be an array.");

            foreach (var mapping in instance.PathMappings)
            {
                if (mapping is null
                    || string.IsNullOrWhiteSpace(mapping.MediaServerPrefix)
                    || string.IsNullOrWhiteSpace(mapping.InfiniDyskPrefix))
                {
                    throw new ArgumentException(
                        $"Config value for '{key}' media server '{instance.Name}' contains an incomplete path-prefix mapping.");
                }
                if (!Path.IsPathRooted(mapping.InfiniDyskPrefix))
                {
                    throw new ArgumentException(
                        $"Config value for '{key}' media server '{instance.Name}' InfiniDysk path prefixes must be absolute paths.");
                }
            }
        }
    }

    private static bool TryValidateBaseUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && string.IsNullOrEmpty(uri.UserInfo)
               && string.IsNullOrEmpty(uri.Query)
               && string.IsNullOrEmpty(uri.Fragment);
    }
}

public sealed class MediaServerInstance
{
    public Guid Id { get; set; }
    public MediaServerType Type { get; set; }
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Token { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<MediaServerPathMapping> PathMappings { get; set; } = [];

    internal bool RuntimeEquivalent(MediaServerInstance other)
    {
        if (Id != other.Id
            || Type != other.Type
            || Enabled != other.Enabled
            || !string.Equals(Name, other.Name, StringComparison.Ordinal)
            || !string.Equals(BaseUrl.TrimEnd('/'), other.BaseUrl.TrimEnd('/'), StringComparison.Ordinal)
            || !string.Equals(Token, other.Token, StringComparison.Ordinal))
            return false;

        if (PathMappings.Count != other.PathMappings.Count) return false;
        for (var i = 0; i < PathMappings.Count; i++)
        {
            var left = PathMappings[i];
            var right = other.PathMappings[i];
            if (!string.Equals(left.MediaServerPrefix, right.MediaServerPrefix, StringComparison.Ordinal)
                || !string.Equals(left.InfiniDyskPrefix, right.InfiniDyskPrefix, StringComparison.Ordinal))
                return false;
        }
        return true;
    }
}

public sealed class MediaServerPathMapping
{
    public string MediaServerPrefix { get; set; } = "";
    public string InfiniDyskPrefix { get; set; } = "";
}
