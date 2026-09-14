using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.RcloneMounts;

/// <summary>
/// Runs one reconcile pass now, instead of waiting for the supervisor's next
/// cycle, so applying a settings change gives immediate feedback.
/// </summary>
[ApiController]
[Route("api/rclone-mounts/apply")]
public class ApplyRcloneMountsController(
    ConfigManager configManager,
    RcloneDaemonService daemonService) : PostOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        if (daemonService.BuiltinClient is not { } client)
        {
            var daemon = daemonService.GetStatus();
            return Ok(new RcloneMountsApplyResponse
            {
                Status = true,
                Errors = daemon.BlockingReasons.Count > 0
                    ? [.. daemon.BlockingReasons]
                    : ["The built-in rclone daemon is not running."],
            });
        }

        // Deliberately not HttpContext.RequestAborted: a browser that navigates
        // away mid-pass would otherwise abandon the mounts between the unmount and
        // the remount, leaving the library offline until the next pass.
        //
        // Through the daemon's mount gate so this cannot interleave with the
        // supervisor's own pass: both would read "nothing is mounted" and both
        // would mount, and the loser reports "already mounted".
        var result = await daemonService.WithMountGateAsync(
                token => RcloneMountReconciler
                    .ForBuiltinDaemon(client, configManager)
                    .ReconcileAsync(configManager.GetRcloneBuiltinMounts(), token),
                SigtermUtil.GetCancellationToken())
            .ConfigureAwait(false);

        // The supervisor did not run this pass, so its record of what it applied
        // no longer describes the daemon. Clearing it lets the next pass
        // reconcile from what is actually mounted, which is what recovers a
        // replacement mount that failed here.
        daemonService.InvalidateAppliedMounts();

        return Ok(new RcloneMountsApplyResponse
        {
            Status = true,
            Mounted = [.. result.Mounted],
            Unmounted = [.. result.Unmounted],
            Errors = [.. result.Errors],
        });
    }
}
