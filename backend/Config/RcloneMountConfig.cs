using System.Text.Json.Serialization;

namespace NzbWebDAV.Config;

/// <summary>
/// VFS cache modes accepted by <c>rclone mount</c>. The names match rclone's own
/// <c>--vfs-cache-mode</c> values so the config value can be handed to the RC API
/// unchanged.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RcloneVfsCacheMode>))]
public enum RcloneVfsCacheMode
{
    Off = 0,
    Minimal = 1,
    Writes = 2,
    Full = 3,
}

/// <summary>
/// One mount managed by the built-in rclone daemon, as stored in the
/// <see cref="ConfigKeys.RcloneBuiltinMounts"/> JSON array.
///
/// Defaults describe the mount InfiniDysk users actually want: the whole WebDAV
/// tree, a disk-backed read cache for smooth seeking, and <c>allow-other</c> so
/// media servers running as another user can read it.
/// </summary>
public class RcloneMountConfig
{
    /// <summary>
    /// Paths a mount must never take over. Mounting a FUSE filesystem on any of
    /// these, or on a directory containing them, hides the running application or
    /// the container's own system directories, and the only recovery is recreating
    /// the container.
    /// </summary>
    private static readonly string[] ProtectedRoots =
    [
        "/app", "/bin", "/boot", "/dev", "/etc", "/lib", "/lib64",
        "/proc", "/root", "/run", "/sbin", "/srv", "/sys", "/usr", "/var",
    ];

    /// <summary>Stable identifier, used to correlate config with live mounts.</summary>
    public required string Id { get; set; }

    /// <summary>Optional display name. Falls back to <see cref="Id"/> in the UI.</summary>
    public string? Name { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Absolute path on the container filesystem to mount onto.</summary>
    public required string MountPoint { get; set; }

    /// <summary>Path within the WebDAV remote to expose. "/" is the whole tree.</summary>
    public string RemotePath { get; set; } = "/";

    public RcloneVfsCacheMode VfsCacheMode { get; set; } = RcloneVfsCacheMode.Full;

    /// <summary>
    /// Lets other users read the mount. Media servers and the Arr apps run as a
    /// different user, usually in a different container, so this is what makes the
    /// mount useful to anything but InfiniDysk itself. Not surfaced in the UI:
    /// turning it off breaks the only reason the mount exists.
    /// </summary>
    public bool AllowOther { get; set; } = true;

    /// <summary>
    /// How long directory listings are cached.
    /// </summary>
    /// <remarks>
    /// A week, matching the sidecar configuration this project documents. That is
    /// only safe because InfiniDysk drops the cached directory itself whenever it
    /// adds or removes an item, and a failed drop is retried and then logged
    /// rather than swallowed. Lower it if listings ever look stale.
    /// </remarks>
    public TimeSpan DirCacheTime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How long cached file data is kept.</summary>
    /// <remarks>
    /// Also a week: re-reading a file from Usenet costs far more than the disk it
    /// occupies, and the cache is bounded by size regardless.
    /// </remarks>
    public TimeSpan VfsCacheMaxAge { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Cache size ceiling in bytes. Null means the ceiling is derived from the
    /// free space on the cache directory's volume rather than left unlimited;
    /// see <c>RcloneCacheBudget</c>.
    /// </summary>
    public long? VfsCacheMaxSizeBytes { get; set; }

    /// <summary>Read-ahead buffer in bytes; null leaves rclone's default of none.</summary>
    /// <remarks>
    /// 512 MiB by default, because the per-transfer memory buffer is switched off
    /// at the daemon: the read-ahead on disk is what keeps seeking smooth, and
    /// without it playback stutters.
    /// </remarks>
    public long? ReadAheadBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>
    /// Turn <c>*.rclonelink</c> files into real symlinks. Symlink-based imports
    /// depend on this.
    /// </summary>
    public bool Links { get; set; } = true;

    /// <summary>
    /// Checks a configured mount list for problems that would leave the daemon
    /// unable to mount, or able to mount somewhere harmful. Returns one message
    /// per problem, each naming the offending mount so the UI can point at it.
    /// An empty result means the list is safe to apply.
    /// </summary>
    /// <param name="mounts">The configured mounts.</param>
    /// <param name="configPath">
    /// The container's config directory. Mounting inside it would put a FUSE
    /// filesystem underneath the databases the backend is writing to.
    /// </param>
    public static IReadOnlyList<string> Validate(IEnumerable<RcloneMountConfig> mounts, string configPath)
    {
        var errors = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenMountPoints = new Dictionary<string, string>(StringComparer.Ordinal);

        // Disabled mounts are not applied, so a half-finished one must not block
        // the mounts that are enabled.
        foreach (var mount in mounts.Where(mount => mount.Enabled))
        {
            var label = string.IsNullOrWhiteSpace(mount.Id) ? "<unnamed mount>" : mount.Id;

            if (string.IsNullOrWhiteSpace(mount.Id))
                errors.Add("Every mount needs an id.");
            else if (!seenIds.Add(mount.Id))
                errors.Add($"Mount id '{mount.Id}' is used more than once.");

            if (mount.VfsCacheMaxSizeBytes is < 0)
                errors.Add($"Mount '{label}': the cache size limit cannot be negative.");

            if (mount.ReadAheadBytes is < 0)
                errors.Add($"Mount '{label}': the read-ahead size cannot be negative.");

            if (mount.VfsCacheMaxAge < TimeSpan.Zero)
                errors.Add($"Mount '{label}': the cache age limit cannot be negative.");

            if (!Path.IsPathRooted(mount.MountPoint))
            {
                errors.Add($"Mount '{label}': mount point '{mount.MountPoint}' must be an absolute path.");
                continue;
            }

            if (ProtectedRootFor(mount.MountPoint) is { } protectedRoot)
            {
                errors.Add(
                    $"Mount '{label}': mount point '{mount.MountPoint}' would take over '{protectedRoot}', " +
                    "which the container needs to keep running. Choose a path under a data volume such as /mnt.");
                continue;
            }

            if (IsWithin(mount.MountPoint, configPath) || IsWithin(configPath, mount.MountPoint))
            {
                errors.Add(
                    $"Mount '{label}': mount point '{mount.MountPoint}' overlaps the config directory " +
                    $"'{configPath}'. Mounting there would place a FUSE filesystem over or underneath the " +
                    "databases InfiniDysk is writing to. Choose a path outside it.");
                continue;
            }

            // Nesting one mount inside another makes rclone serve a filesystem
            // through itself, so containment in either direction is a conflict,
            // not just an identical path.
            var normalized = Normalize(mount.MountPoint);
            var conflict = seenMountPoints
                .FirstOrDefault(seen => IsWithin(normalized, seen.Key) || IsWithin(seen.Key, normalized));

            if (conflict.Value is not null)
            {
                errors.Add(
                    $"Mount '{label}': mount point '{mount.MountPoint}' overlaps mount '{conflict.Value}' " +
                    $"at '{conflict.Key}'. Mounts cannot contain one another.");
            }
            else
            {
                seenMountPoints[normalized] = label;
            }
        }

        return errors;
    }

    /// <summary>
    /// The protected path a mount point would shadow, or null when it is safe.
    /// A mount point is unsafe when it is a system directory, sits inside one, or
    /// contains one.
    /// </summary>
    internal static string? ProtectedRootFor(string mountPoint)
    {
        var normalized = Normalize(mountPoint);
        if (normalized == "/" || normalized.Length == 0) return "/";

        return ProtectedRoots.FirstOrDefault(
            root => IsWithin(normalized, root) || IsWithin(root, normalized));
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is <paramref name="ancestor"/> itself
    /// or sits below it, after both paths are normalized so that "/config/../config"
    /// cannot be used to slip past the check.
    /// </summary>
    private static bool IsWithin(string candidate, string ancestor)
    {
        if (string.IsNullOrWhiteSpace(ancestor)) return false;

        var normalizedCandidate = Normalize(candidate);
        var normalizedAncestor = Normalize(ancestor);

        if (string.Equals(normalizedCandidate, normalizedAncestor, StringComparison.Ordinal))
            return true;

        return normalizedCandidate.StartsWith(normalizedAncestor + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
