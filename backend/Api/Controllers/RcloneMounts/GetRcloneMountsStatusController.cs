using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Rclone.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.RcloneMounts;

[ApiController]
[Route("api/rclone-mounts/status")]
public class GetRcloneMountsStatusController(
    ConfigManager configManager,
    RcloneDaemonService daemonService) : GetOnlyApiController
{
    /// <summary>
    /// How long the whole status read may take, across all three RC calls.
    ///
    /// Each call carries its own 30-second transport timeout, so a wedged daemon
    /// could otherwise hold this endpoint for 90 seconds while the settings tab
    /// polls it every 10, piling requests up behind it. The budget is shared, not
    /// per call, and the RC client already turns a cancelled call into an
    /// unsuccessful response, so exceeding it degrades to a partial status rather
    /// than an error.
    /// </summary>
    internal static readonly TimeSpan StatusBudget = TimeSpan.FromSeconds(8);

    /// <summary>
    /// A deadline that fires at <see cref="StatusBudget"/> or when the caller
    /// disconnects, whichever comes first.
    /// </summary>
    internal static CancellationTokenSource CreateDeadline(CancellationToken requestAborted)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        deadline.CancelAfter(StatusBudget);
        return deadline;
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var daemon = daemonService.GetStatus();
        var configured = configManager.GetRcloneBuiltinMounts();

        // Only ask the daemon what is mounted when it is actually up; otherwise a
        // failed call would add a confusing error on top of "not running".
        var live = new List<RcloneMountPoint>();
        string? version = null;
        var remoteConfigured = false;
        if (daemon.Running && daemonService.BuiltinClient is { } client)
        {
            using var deadline = CreateDeadline(HttpContext.RequestAborted);

            var mounts = await client.ListMounts(deadline.Token).ConfigureAwait(false);
            if (mounts.Success) live = mounts.MountPoints ?? [];

            var coreVersion = await client.GetVersion(deadline.Token).ConfigureAwait(false);
            if (coreVersion.Success) version = coreVersion.Version;

            var remotes = await client.ListRemotes(deadline.Token).ConfigureAwait(false);
            remoteConfigured = remotes.Success
                && (remotes.Remotes?.Contains(RcloneMountReconciler.RemoteName, StringComparer.Ordinal) ?? false);
        }

        var status = RcloneMountsStatusBuilder.Build(daemon, configured, live, remoteConfigured);

        // One filesystem sample per request, reused for both the reported figure
        // and the ceiling derived from it.
        var cacheDir = configManager.GetRcloneBuiltinCacheDir();
        var budget = new RcloneCacheBudget();
        var freeBytes = budget.AvailableFreeSpace(cacheDir);
        var sharesDatabaseVolume = budget.SharesVolumeWith(cacheDir, DavDatabaseContext.ConfigPath);
        // The install-wide limit is what the page lets an operator set, so it is
        // what the page reports. Falling back to a per-mount ceiling covers the
        // installs that imported one: disabled mounts never apply, and with
        // several the figure worth showing is the largest in force.
        var explicitLimit = configManager.GetRcloneBuiltinCacheSizeLimit()
                            ?? configured
                                .Where(m => m.Enabled && m.VfsCacheMaxSizeBytes is > 0)
                                .Max(m => m.VfsCacheMaxSizeBytes);

        return Ok(new RcloneMountsStatusResponse
        {
            Status = true,
            Enabled = status.Enabled,
            Running = status.Running,
            RcloneVersion = version,
            RemoteConfigured = status.RemoteConfigured,
            SymlinkMountDir = configManager.GetRcloneMountDir(),
            CacheDir = cacheDir,
            CacheDirFreeBytes = freeBytes,
            CacheSizeLimitBytes = RcloneCacheBudget.Resolve(freeBytes, sharesDatabaseVolume, explicitLimit),
            BlockingReasons = [.. status.BlockingReasons],
            Mounts = [.. status.Mounts.Select(RcloneMountRowFactory.FromStatus)],
        });
    }
}
