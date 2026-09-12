using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.RcloneMounts;

/// <summary>
/// Previews importing the user's existing external rclone into built-in mode.
///
/// Read-only in both directions: it never modifies the external instance, and it
/// never writes InfiniDysk's configuration. The operator applies the result
/// explicitly after reading the warnings, because the cutover has an order that
/// matters — the sidecar has to stop before the built-in mount can take the same
/// mount point.
/// </summary>
[ApiController]
[Route("api/rclone-mounts/import-external")]
public class ImportExternalRcloneController(IRcloneClient externalClient) : PostOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var command = HttpContext.Request.HasFormContentType
            ? HttpContext.Request.Form["command"].ToString()
            : string.Empty;

        return string.IsNullOrWhiteSpace(command)
            ? Ok(await ImportFromLiveInstanceAsync().ConfigureAwait(false))
            : Ok(ImportFromCommandLine(command));
    }

    private async Task<RcloneImportPreviewResponse> ImportFromLiveInstanceAsync()
    {
        if (string.IsNullOrWhiteSpace(externalClient.Host))
        {
            return new RcloneImportPreviewResponse
            {
                Status = true,
                Imported = false,
                Warnings =
                [
                    "No external rclone server is configured, so there is nothing to read. " +
                    "Set the Rclone Server host on this page, or paste the mount command instead.",
                ],
            };
        }

        var preview = await new RcloneImportService()
            .PreviewAsync(externalClient, DavDatabaseContext.ConfigPath, SigtermUtil.GetCancellationToken())
            .ConfigureAwait(false);

        return ToResponse(preview.Success, preview.Mounts, preview.Warnings);
    }

    private static RcloneImportPreviewResponse ImportFromCommandLine(string command)
    {
        var parsed = RcloneMountCommandParser.Parse(command);
        if (!parsed.Success)
        {
            return new RcloneImportPreviewResponse
            {
                Status = true,
                Imported = false,
                Warnings = [parsed.Error ?? "The command could not be parsed."],
            };
        }

        var mounts = new[] { parsed.Mount! };
        var warnings = new List<string>(RcloneMountConfig.Validate(mounts, DavDatabaseContext.ConfigPath));

        if (parsed.UnsupportedFlags.Count > 0)
        {
            warnings.Add(
                "These flags are not supported by the built-in mount and will not carry across: " +
                string.Join(' ', parsed.UnsupportedFlags));
        }

        if (parsed.Mount!.VfsCacheMode == RcloneVfsCacheMode.Off)
        {
            warnings.Add(
                "The command did not set --vfs-cache-mode, so this mount is imported without a disk " +
                "cache, matching what your sidecar does today. Switch it to Full afterwards if you " +
                "want smoother seeking.");
        }

        // A pasted command carries no credentials, so the remote still has to be
        // created before this mount can authenticate.
        warnings.Add(
            "The command does not contain the WebDAV password. Enter it once above after applying.");

        warnings.Add(
            "Stop the external rclone container before applying this import. Two rclone " +
            "instances cannot serve the same mount point.");

        return ToResponse(true, mounts, warnings);
    }

    private static RcloneImportPreviewResponse ToResponse(
        bool success,
        IReadOnlyList<RcloneMountConfig> mounts,
        IReadOnlyList<string> warnings) => new()
    {
        Status = true,
        Imported = success && mounts.Count > 0,
        Mounts = [.. mounts.Select(RcloneMountRowFactory.FromConfig)],
        Warnings = [.. warnings],
    };
}
