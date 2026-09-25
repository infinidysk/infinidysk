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
                async token =>
                {
                    // Rewriting the remote does not reach a backend that is
                    // already mounted: it authenticated when it was created and
                    // keeps using what it authenticated with. This endpoint is
                    // how an operator recovers from a rotated WebDAV password,
                    // so the mounts have to be rebuilt, not kept.
                    var released = await client.UnmountAll(token).ConfigureAwait(false);

                    var reconciled = await RcloneMountReconciler
                        .ForBuiltinDaemon(client, configManager)
                        .ReconcileAsync(configManager.GetRcloneBuiltinMounts(), token)
                        .ConfigureAwait(false);

                    return (Released: released, Reconciled: reconciled);
                },
                SigtermUtil.GetCancellationToken())
            .ConfigureAwait(false);

        // The supervisor did not run this pass, so its record of what it applied
        // no longer describes the daemon.
        daemonService.InvalidateAppliedMounts();

        var errors = new List<string>(result.Reconciled.Errors);

        // Reported whenever the release fails, not only when the reconcile also
        // complains. A mount that could not be released is still serving through
        // the backend that authenticated with the old password, and the
        // reconciler keeps a mount whose point and remote still match -- so the
        // pass reports success while the thing the operator came here to fix is
        // untouched. Silence would be the worst of the three outcomes.
        if (!result.Released.Success)
        {
            errors.Insert(
                0,
                "Could not release the existing mounts before reconnecting: " +
                $"{result.Released.Error ?? "unknown error"}. Any mount still up is " +
                "using the previous password; remount it to pick up the new one.");
        }

        return Ok(new RcloneMountsApplyResponse
        {
            Status = true,
            Mounted = [.. result.Reconciled.Mounted],
            Unmounted = [.. result.Reconciled.Unmounted],
            Errors = [.. errors],
        });
    }
}
