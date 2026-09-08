using System.Text.Json.Serialization;

namespace NzbWebDAV.Clients.Rclone.Models;

/// <summary>One live mount, as reported by <c>mount/listmounts</c>.</summary>
public class RcloneMountPoint
{
    /// <summary>The remote and path being served, for example <c>infinidysk:/</c>.</summary>
    [JsonPropertyName("Fs")]
    public string? Fs { get; set; }

    /// <summary>The directory it is mounted on.</summary>
    [JsonPropertyName("MountPoint")]
    public string? MountPoint { get; set; }

    [JsonPropertyName("MountedOn")]
    public DateTimeOffset? MountedOn { get; set; }
}

/// <summary>Response from the <c>mount/listmounts</c> endpoint.</summary>
public class ListMountsResponse : RcloneResponse
{
    [JsonPropertyName("mountPoints")]
    public List<RcloneMountPoint>? MountPoints { get; set; }
}

/// <summary>Response from the <c>mount/mount</c> endpoint.</summary>
public class MountResponse : RcloneResponse
{
    /// <summary>The mount point rclone actually used.</summary>
    [JsonPropertyName("mountPoint")]
    public string? MountPoint { get; set; }
}

/// <summary>Response from the <c>config/listremotes</c> endpoint.</summary>
public class ListRemotesResponse : RcloneResponse
{
    [JsonPropertyName("remotes")]
    public List<string>? Remotes { get; set; }
}
