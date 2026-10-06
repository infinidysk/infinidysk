using NzbWebDAV.Config;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.RcloneMounts;

/// <summary>
/// Maps mounts onto the wire shape the settings tab renders.
///
/// Shared so the status endpoint and the import preview cannot drift: an import
/// that dropped fields the status endpoint reports would silently discard a
/// tuned mount's settings on the way in.
/// </summary>
internal static class RcloneMountRowFactory
{
    public static RcloneMountsStatusResponse.RcloneMountRow FromConfig(RcloneMountConfig mount) => new()
    {
        Id = mount.Id,
        Name = mount.Name,
        MountPoint = mount.MountPoint,
        RemotePath = mount.RemotePath,
        VfsCacheMode = mount.VfsCacheMode.ToString().ToLowerInvariant(),
        Configured = false,
        Enabled = mount.Enabled,
        Mounted = false,
        AllowOther = mount.AllowOther,
        Links = mount.Links,
        DirCacheTimeSeconds = (long)mount.DirCacheTime.TotalSeconds,
        VfsCacheMaxAgeSeconds = (long)mount.VfsCacheMaxAge.TotalSeconds,
        VfsCacheMaxSizeBytes = mount.VfsCacheMaxSizeBytes,
        ReadAheadBytes = mount.ReadAheadBytes,
    };

    public static RcloneMountsStatusResponse.RcloneMountRow FromStatus(RcloneMountStatusRow row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        MountPoint = row.MountPoint,
        RemotePath = row.RemotePath,
        VfsCacheMode = row.VfsCacheMode,
        Configured = row.Configured,
        Enabled = row.Enabled,
        Mounted = row.Mounted,
        AllowOther = row.AllowOther,
        Links = row.Links,
        DirCacheTimeSeconds = row.DirCacheTimeSeconds,
        VfsCacheMaxAgeSeconds = row.VfsCacheMaxAgeSeconds,
        VfsCacheMaxSizeBytes = row.VfsCacheMaxSizeBytes,
        ReadAheadBytes = row.ReadAheadBytes,
    };
}
