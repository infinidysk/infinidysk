using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>What one reconcile pass did, and anything it could not do.</summary>
/// <param name="Mounted">Mount points newly mounted this pass.</param>
/// <param name="Unmounted">Mount points removed this pass.</param>
/// <param name="Errors">
/// One message per mount that could not be brought into the configured state.
/// A failure on one mount never stops the others: a single busy mount point
/// should not leave the rest of a library offline.
/// </param>
public sealed record RcloneReconcileResult(
    IReadOnlyList<string> Mounted,
    IReadOnlyList<string> Unmounted,
    IReadOnlyList<string> Errors);

/// <summary>
/// Makes rclone's live mounts match the configured ones.
///
/// The built-in daemon serves only InfiniDysk, so every mount on it is ours and
/// anything mounted that is no longer configured is removed. The pass is
/// idempotent: running it again with unchanged configuration does nothing.
///
/// Run by <see cref="RcloneDaemonService"/> whenever the daemon starts or the
/// mount list changes, and on demand from POST /api/rclone-mounts/apply.
/// </summary>
public sealed class RcloneMountReconciler(
    IRcloneClient client,
    RcloneCacheBudget? cacheBudget = null,
    string? cacheDir = null,
    string? databaseDir = null,
    long? cacheSizeLimitBytes = null)
{
    /// <summary>The name of the remote the built-in daemon serves.</summary>
    public const string RemoteName = "infinidysk";

    /// <summary>
    /// A reconciler wired to derive cache limits from the volume the daemon
    /// actually writes its cache to.
    /// </summary>
    public static RcloneMountReconciler ForBuiltinDaemon(IRcloneClient client, ConfigManager configManager) =>
        new(
            client,
            new RcloneCacheBudget(),
            configManager.GetRcloneBuiltinCacheDir(),
            DavDatabaseContext.ConfigPath,
            configManager.GetRcloneBuiltinCacheSizeLimit());

    public async Task<RcloneReconcileResult> ReconcileAsync(
        IReadOnlyList<RcloneMountConfig> configured,
        CancellationToken cancellationToken)
    {
        var mounted = new List<string>();
        var unmounted = new List<string>();
        var errors = new List<string>();

        var live = await client.ListMounts(cancellationToken).ConfigureAwait(false);
        if (!live.Success)
        {
            // Treating a failed listing as "nothing is mounted" would mount every
            // configured path a second time, so the pass stops here instead.
            errors.Add(
                $"Could not read the built-in rclone's mounts: {live.Error ?? "unknown error"}. " +
                "No mounts were changed.");
            return new RcloneReconcileResult(mounted, unmounted, errors);
        }

        var livePoints = (live.MountPoints ?? [])
            .Where(m => m.MountPoint is not null)
            .ToDictionary(m => m.MountPoint!, m => m.Fs, StringComparer.Ordinal);

        var wanted = configured
            .Where(m => m.Enabled)
            .ToDictionary(m => m.MountPoint, m => m, StringComparer.Ordinal);

        // Remove first, so a mount whose remote path changed frees its mount point
        // before the replacement is attempted.
        foreach (var (mountPoint, liveFs) in livePoints)
        {
            var keep = wanted.TryGetValue(mountPoint, out var config)
                       && string.Equals(NormalizeFs(liveFs), NormalizeFs(BuildFs(config)), StringComparison.Ordinal);
            if (keep) continue;

            var response = await client.UnmountFs(mountPoint, cancellationToken).ConfigureAwait(false);
            if (response.Success)
            {
                unmounted.Add(mountPoint);
                livePoints.Remove(mountPoint);
            }
            else
            {
                errors.Add($"Could not unmount '{mountPoint}': {response.Error ?? "unknown error"}");
            }
        }

        var toMount = wanted.Values.Where(m => !livePoints.ContainsKey(m.MountPoint)).ToList();
        if (toMount.Count == 0)
            return new RcloneReconcileResult(mounted, unmounted, errors);

        var remotes = await client.ListRemotes(cancellationToken).ConfigureAwait(false);
        if (!remotes.Success)
        {
            // Same reasoning as the mount listing above: a question we could not
            // ask is not an answer. Reporting the remote as missing here would
            // send the operator to re-enter a password that is already correct.
            errors.Add(
                $"Could not read the built-in rclone's remotes: {remotes.Error ?? "unknown error"}. " +
                "No mounts were changed.");
            return new RcloneReconcileResult(mounted, unmounted, errors);
        }

        if (!(remotes.Remotes?.Contains(RemoteName, StringComparer.Ordinal) ?? false))
        {
            // The WebDAV password is never stored in plaintext, so the remote can
            // only be created from a password the user supplies once. Until then,
            // mounting would produce authentication failures that look like a
            // broken mount rather than missing setup.
            errors.Add(
                $"The '{RemoteName}' rclone remote does not exist yet. Provide the WebDAV password " +
                "for the built-in mount before mounting.");
            return new RcloneReconcileResult(mounted, unmounted, errors);
        }

        foreach (var config in toMount)
        {
            var response = await client
                .MountFs(BuildFs(config), config.MountPoint, BuildMountOptions(config), BuildVfsOptions(config), cancellationToken)
                .ConfigureAwait(false);

            if (response.Success)
            {
                Log.Information("Mounted {Fs} on {MountPoint}.", BuildFs(config), config.MountPoint);
                mounted.Add(config.MountPoint);
            }
            else
            {
                errors.Add(DescribeMountFailure(config.MountPoint, response.Error));
            }
        }

        return new RcloneReconcileResult(mounted, unmounted, errors);
    }

    /// <summary>
    /// Turns rclone's refusal to mount into something an operator can act on.
    ///
    /// rclone has two separate guards here and they need different answers: a
    /// path that already carries a filesystem, and a path that merely has files
    /// in it. The first is the one migrations and hard restarts hit, because a
    /// FUSE mount outlives the process that made it.
    /// </summary>
    internal static string DescribeMountFailure(string mountPoint, string? rcloneError)
    {
        var error = rcloneError ?? "unknown error";

        if (error.Contains("already mounted", StringComparison.OrdinalIgnoreCase))
        {
            return
                $"Could not mount '{mountPoint}': something is already mounted there, so rclone will " +
                "not mount over it. This is usually a mount left behind by an rclone that was killed " +
                "rather than stopped, or an external rclone container still serving the same path. " +
                $"Check it with 'mount | grep {mountPoint}', stop any external rclone still using it, " +
                $"and clear a leftover mount with 'fusermount3 -uz {mountPoint}' (on the host, if the " +
                "path is bind-mounted in). InfiniDysk will not unmount it for you, because it cannot " +
                "tell a stale mount from one that is still serving your library.";
        }

        if (error.Contains("not empty", StringComparison.OrdinalIgnoreCase))
        {
            return
                $"Could not mount '{mountPoint}': the directory is not empty, and mounting over it " +
                "would hide what is already there. Move or remove its contents, or point the mount at " +
                "an empty directory.";
        }

        return $"Could not mount '{mountPoint}': {error}";
    }

    /// <summary>
    /// Canonical form for comparing what is mounted with what is configured.
    /// rclone reports a root mount of <c>infinidysk:/</c> back as <c>infinidysk:</c>,
    /// so a literal comparison would unmount and remount a healthy root mount on
    /// every pass. Verified against rclone v1.75.1.
    /// </summary>
    internal static string NormalizeFs(string? fs)
    {
        if (string.IsNullOrEmpty(fs)) return string.Empty;
        return fs.Length > 1 && fs.EndsWith('/') ? fs.TrimEnd('/') : fs;
    }

    internal static string BuildFs(RcloneMountConfig config)
    {
        var path = string.IsNullOrWhiteSpace(config.RemotePath) ? "/" : config.RemotePath;
        if (!path.StartsWith('/')) path = "/" + path;
        return $"{RemoteName}:{path}";
    }

    private static Dictionary<string, object?> BuildMountOptions(RcloneMountConfig config) => new()
    {
        ["AllowOther"] = config.AllowOther,
    };

    // rclone accepts the cache mode by name, which is also what the user sees in
    // the UI and in rclone's own --vfs-cache-mode flag, and durations as strings
    // in its own duration format.
    private Dictionary<string, object?> BuildVfsOptions(RcloneMountConfig config)
    {
        var options = new Dictionary<string, object?>
        {
            ["CacheMode"] = config.VfsCacheMode.ToString().ToLowerInvariant(),
            ["DirCacheTime"] = FormatDuration(config.DirCacheTime),

            // The remote is InfiniDysk's own WebDAV server, which cannot report
            // changes, so rclone's periodic poll asks a question nothing answers.
            ["PollInterval"] = "0s",
            ["CacheMaxAge"] = FormatDuration(config.VfsCacheMaxAge),
            ["Links"] = config.Links,
        };

        if (ResolveCacheMaxSize(config) is { } maxSize) options["CacheMaxSize"] = maxSize;
        if (config.ReadAheadBytes is { } readAhead) options["ReadAhead"] = readAhead;

        return options;
    }

    /// <summary>
    /// The cache ceiling to send. rclone's own default is unlimited, which fills
    /// whichever volume the cache directory sits on, so a budget derived from the
    /// free space there is used when nothing explicit is configured.
    /// </summary>
    private long? ResolveCacheMaxSize(RcloneMountConfig config)
    {
        // The install-wide limit wins over a per-mount one. The per-mount value
        // only ever arrives by importing an external rclone, and it is not shown
        // anywhere in the UI; leaving it to override the box the operator can
        // actually see would make that box look broken.
        var configured = cacheSizeLimitBytes ?? config.VfsCacheMaxSizeBytes;

        if (cacheBudget is null || cacheDir is null || databaseDir is null)
            return configured;

        var resolved = cacheBudget.ResolveFor(cacheDir, databaseDir, configured);

        // Logged here rather than inside the budget: this runs when a mount is
        // created, while the status endpoint resolves the same number on every
        // poll and would repeat the warning every few seconds.
        if (configured is null && resolved <= RcloneCacheBudget.FloorBytes)
        {
            Log.Warning(
                "The rclone cache directory {CacheDir} has little free space, so the VFS cache for " +
                "{MountPoint} is capped at {LimitMB:N0} MB. Move rclone.builtin.cache-dir to a larger " +
                "volume for smoother playback.",
                cacheDir,
                config.MountPoint,
                resolved / (1024 * 1024));
        }

        return resolved;
    }

    /// <summary>
    /// rclone's duration format: "20s", "24h0m0s". <see cref="TimeSpan"/>'s own
    /// formatting produces "00:00:20", which rclone rejects.
    /// </summary>
    internal static string FormatDuration(TimeSpan value)
    {
        if (value < TimeSpan.Zero) return "0s";
        if (value.TotalSeconds < 60) return $"{(long)value.TotalSeconds}s";

        var hours = (long)value.TotalHours;
        return $"{hours}h{value.Minutes}m{value.Seconds}s";
    }
}
