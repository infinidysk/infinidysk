using NzbWebDAV.Clients.Rclone.Models;
using NzbWebDAV.Config;

namespace NzbWebDAV.Services;

/// <summary>One row in the admin UI's mount table.</summary>
/// <param name="Id">Configured id, or the mount point for an unconfigured mount.</param>
/// <param name="Configured">False for something mounted that config no longer describes.</param>
/// <param name="Enabled">Whether config asks for this mount to exist.</param>
/// <param name="Mounted">Whether it is live right now.</param>
public sealed record RcloneMountStatusRow(
    string Id,
    string? Name,
    string MountPoint,
    string RemotePath,
    string VfsCacheMode,
    bool Configured,
    bool Enabled,
    bool Mounted,
    bool AllowOther = true,
    bool Links = true,
    long DirCacheTimeSeconds = 0,
    long VfsCacheMaxAgeSeconds = 0,
    long? VfsCacheMaxSizeBytes = null,
    long? ReadAheadBytes = null);

/// <summary>Everything the Rclone settings tab needs in one response.</summary>
public sealed record RcloneMountsStatus(
    bool Enabled,
    bool Running,
    string? BaseUrl,
    IReadOnlyList<string> BlockingReasons,
    IReadOnlyList<RcloneMountStatusRow> Mounts,
    bool RemoteConfigured);

/// <summary>
/// Merges what is configured with what is actually mounted.
///
/// The two can disagree in both directions, and the UI has to show both: a
/// configured mount that is not live yet, and a live mount whose configuration
/// was removed. Hiding either would make a half-applied state look finished.
/// </summary>
public static class RcloneMountsStatusBuilder
{
    public static RcloneMountsStatus Build(
        RcloneDaemonStatus daemon,
        IReadOnlyList<RcloneMountConfig> configured,
        IReadOnlyList<RcloneMountPoint> live,
        bool remoteConfigured = false)
    {
        // Nothing can be mounted by a daemon that is not running, whatever a stale
        // listing might say.
        var livePoints = daemon.Running
            ? live.Where(m => m.MountPoint is not null)
                .Select(m => m.MountPoint!)
                .ToHashSet(StringComparer.Ordinal)
            : [];

        var rows = configured
            .Select(mount => new RcloneMountStatusRow(
                mount.Id,
                mount.Name,
                mount.MountPoint,
                mount.RemotePath,
                mount.VfsCacheMode.ToString().ToLowerInvariant(),
                Configured: true,
                mount.Enabled,
                livePoints.Contains(mount.MountPoint),
                mount.AllowOther,
                mount.Links,
                (long)mount.DirCacheTime.TotalSeconds,
                (long)mount.VfsCacheMaxAge.TotalSeconds,
                mount.VfsCacheMaxSizeBytes,
                mount.ReadAheadBytes))
            .ToList();

        var configuredPoints = configured
            .Select(mount => mount.MountPoint)
            .ToHashSet(StringComparer.Ordinal);

        rows.AddRange(livePoints
            .Where(mountPoint => !configuredPoints.Contains(mountPoint))
            .Select(mountPoint => new RcloneMountStatusRow(
                mountPoint,
                Name: null,
                mountPoint,
                RemotePath: "/",
                VfsCacheMode: "unknown",
                Configured: false,
                Enabled: false,
                Mounted: true)));

        return new RcloneMountsStatus(
            daemon.Enabled,
            daemon.Running,
            daemon.BaseUrl,
            daemon.BlockingReasons,
            rows,
            remoteConfigured);
    }
}
