using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.RcloneMounts;

/// <summary>
/// Unmounts one mount and lets the next reconcile pass recreate it. Useful when
/// a mount is wedged but the configuration is correct, which otherwise needs a
/// container restart.
/// </summary>
[ApiController]
[Route("api/rclone-mounts/remount")]
public class RemountRcloneMountController(
    ConfigManager configManager,
    RcloneDaemonService daemonService) : PostOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var id = HttpContext.Request.Query["id"].ToString();
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("An 'id' query parameter is required.");

        var mount = configManager.GetRcloneBuiltinMounts()
            .FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

        if (mount is null)
            throw new ArgumentException($"No configured mount has the id '{id}'.");

        if (daemonService.BuiltinClient is not { } client)
        {
            return Ok(new RcloneMountsApplyResponse
            {
                Status = true,
                Errors = ["The built-in rclone daemon is not running."],
            });
        }

        // The unmount and the remount that follows it are one transition; a
        // cancelled request must not leave the mount point empty, and the mount
        // gate keeps the supervisor's own pass from remounting in between.
        var lifetime = SigtermUtil.GetCancellationToken();
        var outcome = await daemonService.WithMountGateAsync<(string? Error, RcloneReconcileResult? Result)>(
            async token =>
            {
                var unmount = await client.UnmountFs(mount.MountPoint, token).ConfigureAwait(false);
                if (!unmount.Success)
                    return ($"Could not unmount '{mount.MountPoint}': {unmount.Error ?? "unknown error"}", null);

                var reconciled = await RcloneMountReconciler
                    .ForBuiltinDaemon(client, configManager)
                    .ReconcileAsync(configManager.GetRcloneBuiltinMounts(), token)
                    .ConfigureAwait(false);

                return (null, reconciled);
            },
            lifetime).ConfigureAwait(false);

        // The supervisor did not run this pass, so its record of what it applied
        // no longer describes the daemon. Clearing it lets the next pass
        // reconcile from what is actually mounted, which is what recovers a
        // replacement mount that failed here.
        daemonService.InvalidateAppliedMounts();

        if (outcome.Result is not { } result)
        {
            return Ok(new RcloneMountsApplyResponse
            {
                Status = true,
                Errors = [outcome.Error!],
            });
        }

        return Ok(new RcloneMountsApplyResponse
        {
            Status = true,
            Mounted = [.. result.Mounted],
            Unmounted = [mount.MountPoint, .. result.Unmounted],
            Errors = [.. result.Errors],
        });
    }
}
