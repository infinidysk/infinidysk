using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.RcloneMounts;

/// <summary>
/// Creates the rclone remote the built-in mount serves.
///
/// The WebDAV password cannot be taken from configuration, which stores only a
/// hash, so it is supplied once here. It is handed straight to rclone and stored
/// only in rclone's own config file, obscured; it is never written to
/// InfiniDysk's database and never returned by any endpoint.
///
/// Importing an existing external rclone covers the same ground without asking
/// for anything, so this is the path for an installation that has no rclone to
/// import from.
/// </summary>
[ApiController]
[Route("api/rclone-mounts/webdav-credentials")]
public class SetRcloneWebdavCredentialsController(
    ConfigManager configManager,
    RcloneDaemonService daemonService) : PostOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var password = HttpContext.Request.HasFormContentType
            ? HttpContext.Request.Form["password"].ToString()
            : string.Empty;

        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("A WebDAV password is required.");

        var user = configManager.GetWebdavUser();

        // Check the credentials against our own hash first. Creating a remote that
        // cannot authenticate would surface later as a mount that fails for
        // reasons the operator cannot see.
        var hash = configManager.GetWebdavPasswordHash();
        if (hash is null || !PasswordUtil.Verify(hash, password))
        {
            return Ok(new RcloneMountsApplyResponse
            {
                Status = true,
                Errors = ["That is not the WebDAV password. Check Settings, WebDAV for the current one."],
            });
        }

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

        var created = await client
            .CreateRemote(
                RcloneMountReconciler.RemoteName,
                "webdav",
                RcloneWebdavRemote.BuildParameters(user, password),
                SigtermUtil.GetCancellationToken())
            .ConfigureAwait(false);

        if (!created.Success)
        {
            return Ok(new RcloneMountsApplyResponse
            {
                Status = true,
                Errors = [$"Could not create the rclone remote: {created.Error ?? "unknown error"}"],
            });
        }

        // Mount straight away: the operator supplied the password to get a mount,
        // not to save a credential. Through the mount gate so it cannot
        // interleave with the supervisor's own pass.
        var result = await daemonService.WithMountGateAsync(
                token => RcloneMountReconciler
                    .ForBuiltinDaemon(client, configManager)
                    .ReconcileAsync(configManager.GetRcloneBuiltinMounts(), token),
                SigtermUtil.GetCancellationToken())
            .ConfigureAwait(false);

        return Ok(new RcloneMountsApplyResponse
        {
            Status = true,
            Mounted = [.. result.Mounted],
            Unmounted = [.. result.Unmounted],
            Errors = [.. result.Errors],
        });
    }
}
