using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Rclone.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.RcloneMounts;

/// <summary>
/// Empties rclone's VFS cache and puts the mounts back.
///
/// Lowering the cache ceiling does not return space already written, and rclone
/// evicts only on its own schedule, so without this the only way to reclaim the
/// disk was deleting files inside the container by hand.
/// </summary>
[ApiController]
[Route("api/rclone-mounts/clear-cache")]
public class ClearRcloneCacheController(
    ConfigManager configManager,
    RcloneDaemonService daemonService) : PostOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var cacheDir = configManager.GetRcloneBuiltinCacheDir();

        // Everything happens inside the mount gate, including deciding whether
        // there is a daemon at all. The supervisor starts one on its own poll, so
        // a check made out here can be stale by the time the files go: reading
        // "no daemon" and then deleting would pull the cache out from under
        // mounts that came up in between. Not HttpContext.RequestAborted either:
        // a browser that navigates away mid-purge must not leave the mounts down.
        var outcome = await daemonService.WithMountGateAsync(
            async token =>
            {
                // With no daemon running nothing holds the cache open, so the
                // files can go without touching mounts.
                if (daemonService.BuiltinClient is not { } client)
                {
                    return (
                        Unmount: new RcloneResponse { Success = true },
                        Purge: RcloneCachePurger.Purge(cacheDir),
                        Remounted: new RcloneReconcileResult([], [], []));
                }

                // rclone keeps open handles into the cache directory while a
                // mount is up. Releasing the mounts first means the files being
                // deleted are not the ones it is still serving from.
                var unmount = await client.UnmountAll(token).ConfigureAwait(false);
                if (!unmount.Success)
                {
                    // The mounts are still up and rclone still holds the cache
                    // files open. Deleting them now would pull the ground out
                    // from under a running mount.
                    return (
                        Unmount: unmount,
                        Purge: new RcloneCachePurgeResult(0, null),
                        Remounted: new RcloneReconcileResult([], [], []));
                }

                var purge = RcloneCachePurger.Purge(cacheDir);
                var remounted = await RcloneMountReconciler
                    .ForBuiltinDaemon(client, configManager)
                    .ReconcileAsync(configManager.GetRcloneBuiltinMounts(), token)
                    .ConfigureAwait(false);

                return (Unmount: unmount, Purge: purge, Remounted: remounted);
            },
            SigtermUtil.GetCancellationToken()).ConfigureAwait(false);

        // The supervisor did not run this pass, so its record of what it applied
        // no longer describes the daemon. Clearing it lets the next pass
        // reconcile from what is actually mounted, which is what recovers a
        // replacement mount that failed here.
        daemonService.InvalidateAppliedMounts();

        var errors = new List<string>();
        if (!outcome.Unmount.Success)
        {
            errors.Add(
                "Could not release the mounts before emptying the cache: " +
                $"{outcome.Unmount.Error ?? "unknown error"}. The cache was left alone.");
        }

        if (outcome.Purge.Error is { } purgeError) errors.Add(purgeError);
        errors.AddRange(outcome.Remounted.Errors);

        return Ok(new RcloneCacheClearedResponse
        {
            Status = true,
            FreedBytes = outcome.Purge.FreedBytes,
            Mounted = [.. outcome.Remounted.Mounted],
            Errors = errors,
        });
    }
}
