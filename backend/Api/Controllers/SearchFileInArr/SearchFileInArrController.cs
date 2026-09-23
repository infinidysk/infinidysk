using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Controllers.RecheckFile;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Api.Controllers.SearchFileInArr;

[ApiController]
[Route("api/search-file-in-arr")]
[ProducesResponseType(typeof(SearchFileInArrResponse), 200)]
public sealed class SearchFileInArrController(DavDatabaseClient dbClient, ConfigManager configManager, FilesLibraryIndex libraryIndex) : PostOnlyApiController
{
    private static readonly ConcurrentDictionary<Guid, byte> InFlight = new();
    internal static readonly TimeSpan ResolutionTimeout = TimeSpan.FromSeconds(30);
    internal const int MaximumTargets = 32;
    internal Func<string, ArrConfig.ConnectionDetails, ArrClient> ClientFactory { get; set; } =
        static (appType, details) => appType == "radarr" ? new RadarrClient(details.Host, details.ApiKey) : new SonarrClient(details.Host, details.ApiKey);
    internal sealed record SearchTarget(ArrClient Client, string AppType, string InstanceKey, string DisplayName, string LinkPath, ArrMediaFileMatch Match);

    internal async Task<IReadOnlyList<SearchTarget>> ResolveTargetsAsync(DavItem item, IReadOnlyList<string> linkPaths, CancellationToken cancellationToken)
    {
        var targets = new Dictionary<string, SearchTarget>(StringComparer.Ordinal);
        foreach (var (appType, details) in configManager.GetArrConfig().GetEnabledInstances())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var client = ClientFactory(appType, details);
            var roots = await client.GetRootFolders(cancellationToken).ConfigureAwait(false);
            if (roots.Any(root => root is null || string.IsNullOrWhiteSpace(root.Path))) throw new InvalidDataException("Arr returned an invalid root folder.");
            foreach (var linkPath in linkPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!OrganizedLinksUtil.PathStillTargets(linkPath, item.Id, configManager)) continue;
                if (!roots.Any(root => HealthCheckService.IsPathWithinRoot(linkPath, root.Path!))) continue;
                var match = await client.FindMediaFileAsync(linkPath, cancellationToken).ConfigureAwait(false);
                if (match is null) continue;
                if (match.MediaIds is null) throw new InvalidDataException("Arr returned an invalid media identity.");
                var ids = match.MediaIds.Distinct().Order().ToArray();
                if (match.FileId <= 0 || ids.Length == 0 || ids.Any(id => id <= 0) ||
                    (match.Kind == ArrMediaKind.Movie && ids.Length != 1) ||
                    (appType == "radarr" && match.Kind != ArrMediaKind.Movie) ||
                    (appType == "sonarr" && match.Kind != ArrMediaKind.Episode)) throw new InvalidDataException("Arr returned an invalid media identity.");
                var instanceKey = ArrConfig.MakeInstanceKey(appType, details.Host);
                var identity = instanceKey + "|" + match.Kind + "|" + string.Join(",", ids);
                var displayName = string.IsNullOrWhiteSpace(details.Name) ? details.Host : details.Name;
                targets.TryAdd(identity, new(client, appType, instanceKey, displayName, linkPath, match with { MediaIds = ids }));
                if (targets.Count > MaximumTargets) throw new InvalidDataException("Too many Arr targets for one file search.");
            }
        }
        return targets.Values.OrderBy(target => target.InstanceKey, StringComparer.Ordinal).ThenBy(target => target.Match.FileId).ToArray();
    }

    internal static Task<ArrCommand> RequestSearchAsync(SearchTarget target, CancellationToken cancellationToken) => target.Match.Kind switch
    {
        ArrMediaKind.Movie => target.Client.CommandAsync(new { name = "MoviesSearch", movieIds = target.Match.MediaIds }, cancellationToken),
        ArrMediaKind.Episode => target.Client.CommandAsync(new { name = "EpisodeSearch", episodeIds = target.Match.MediaIds }, cancellationToken),
        _ => throw new InvalidDataException("Unsupported Arr media kind."),
    };

    private static bool Known(Exception exception) => exception is HttpRequestException or OperationCanceledException or JsonException
        or InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException;

    private static async Task<ArrCommand> RequestWithTimeoutAsync(SearchTarget target, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await RequestSearchAsync(target, timeout.Token).ConfigureAwait(false);
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var id = await RecheckFileController.ReadItemIdAsync(HttpContext).ConfigureAwait(false);
        if (!InFlight.TryAdd(id, 0)) return Conflict(new BaseApiResponse { Status = false, Error = "A search for this file is already in progress." });
        var token = HttpContext.RequestAborted;
        var commandMayHaveBeenSent = false;
        try
        {
            var item = await dbClient.Ctx.Items.AsNoTracking().SingleOrDefaultAsync(file => file.Id == id, token).ConfigureAwait(false);
            if (item is null || !item.Path.StartsWith("/content/", StringComparison.Ordinal))
                return NotFound(new BaseApiResponse { Status = false, Error = "File no longer exists." });
            if (item.Type != DavItem.ItemType.UsenetFile || !FilenameUtil.IsHealthCheckCandidate(item.Name))
                return Conflict(new BaseApiResponse { Status = false, Error = "Select a media file to search in Arr." });
            if (string.IsNullOrWhiteSpace(configManager.GetLibraryDir()))
                return Conflict(new BaseApiResponse { Status = false, Error = "Configure the Library Directory before searching in Arr." });
            if (!configManager.GetArrConfig().GetEnabledInstances().Any())
                return Conflict(new BaseApiResponse { Status = false, Error = "Configure an enabled Arr instance before searching." });
            var library = libraryIndex.GetSnapshot();
            if (library.State != "ready") library = await libraryIndex.RefreshAsync(token).ConfigureAwait(false);
            if (library.State != "ready")
                return Conflict(new BaseApiResponse { Status = false, Error = "The library scan is not ready. Refresh Files and try again." });
            if (!library.Links.TryGetValue(id, out var paths) || paths.Length == 0)
                return Conflict(new BaseApiResponse { Status = false, Error = "No recognized link for this file exists in the Library Directory." });
            IReadOnlyList<SearchTarget> targets;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(ResolutionTimeout);
                try { targets = await ResolveTargetsAsync(item, paths, timeout.Token).ConfigureAwait(false); }
                catch (Exception exception) when (Known(exception) && !token.IsCancellationRequested)
                {
                    Log.Warning("Files Arr search preflight failed for {DavItemId}. Reason: {Reason}", id, "Could not verify all Arr search targets.");
                    return StatusCode(502, new BaseApiResponse { Status = false, Error = "Could not verify all Arr search targets. No search was requested." });
                }
            }
            if (targets.Count == 0) return Conflict(new BaseApiResponse { Status = false, Error = "No enabled Arr instance owns this file at its current library path." });
            var results = new List<SearchFileInArrResponse.Result>();
            foreach (var target in targets)
            {
                token.ThrowIfCancellationRequested();
                var state = "not-requested";
                string? error = "The file, library link, or enabled instance changed. Refresh Files.";
                int? commandId = null;
                try
                {
                    var exists = await dbClient.Ctx.Items.AnyAsync(file => file.Id == id && file.Type == DavItem.ItemType.UsenetFile, token).ConfigureAwait(false);
                    var enabled = configManager.GetArrConfig().GetEnabledInstances().Any(instance => ArrConfig.MakeInstanceKey(instance.AppType, instance.Details.Host) == target.InstanceKey);
                    if (exists && enabled && OrganizedLinksUtil.PathStillTargets(target.LinkPath, id, configManager))
                    {
                        commandMayHaveBeenSent = true;
                        var receipt = await RequestWithTimeoutAsync(target, token).ConfigureAwait(false);
                        if (receipt.Id <= 0) throw new InvalidDataException("Missing command receipt.");
                        commandId = receipt.Id;
                        state = "requested";
                        error = null;
                    }
                }
                catch (Exception exception) when (Known(exception) && !token.IsCancellationRequested)
                {
                    state = exception is HttpRequestException { StatusCode: not null } ? "failed" : "unconfirmed";
                    error = state == "failed" ? "Arr rejected the search request." : "Command receipt is unknown. Check Arr before trying again.";
                    Log.Warning("Files Arr search for {DavItemId} was {State}. Reason: {Reason}", id, state, error);
                }
                results.Add(new(target.AppType, target.DisplayName, target.Match.MediaIds, commandId, state, error));
            }
            var requested = results.Count(result => result.State == "requested");
            var outcome = requested == results.Count ? "requested" : requested > 0 ? "partial"
                : results.Any(result => result.State == "unconfirmed") ? "unconfirmed" : "failed";
            return Ok(new SearchFileInArrResponse { DavItemId = id, Outcome = outcome, Results = results });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (commandMayHaveBeenSent) Log.Warning("Files Arr search cancelled for {DavItemId}. Reason: {Reason}", id, "Command receipt may be unknown; no retry was sent.");
            throw;
        }
        catch (Exception exception) when (Known(exception))
        {
            Log.Warning("Files Arr search unavailable for {DavItemId}. Reason: {Reason}", id, "Library or Arr lookup is unavailable.");
            return StatusCode(502, new BaseApiResponse { Status = false, Error = "Could not verify all Arr search targets. No search was requested." });
        }
        finally { InFlight.TryRemove(id, out _); }
    }
}