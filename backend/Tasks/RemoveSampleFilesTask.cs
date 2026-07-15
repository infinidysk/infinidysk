using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Tasks;

public class RemoveSampleFilesTask(
    ConfigManager configManager,
    WebsocketManager websocketManager,
    bool isDryRun,
    bool triggerArrSearch
) : BaseTask
{
    private const int MaxAutoSearchCount = 10;
    private static List<string> _auditLines = [];

    private record VideoFileInfo(Guid Id, string Name, string Path);

    protected override async Task ExecuteInternal()
    {
        try
        {
            await RemoveSampleFiles().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Report($"Failed: {e.Message}");
            Log.Error(e, "Failed to remove sample files.");
        }
    }

    private async Task RemoveSampleFiles()
    {
        Report("Scanning for sample files...");
        await using var dbContext = new DavDatabaseContext();
        var dbClient = new DavDatabaseClient(dbContext);

        var usenetFiles = await dbContext.Items
            .Where(x => x.Type == DavItem.ItemType.UsenetFile)
            .Select(x => new { x.Id, x.Name, x.Path })
            .ToListAsync(CancellationToken).ConfigureAwait(false);

        var videoFiles = usenetFiles
            .Where(x => FilenameUtil.IsVideoFile(x.Name))
            .Select(x => new VideoFileInfo(x.Id, x.Name, x.Path))
            .ToList();

        var groups = videoFiles
            .Select(x => new { File = x, GroupKey = GetMountFolderPrefix(x.Path) })
            .Where(x => x.GroupKey != null)
            .GroupBy(x => x.GroupKey!)
            .ToList();

        var redundantSamples = new List<VideoFileInfo>();
        var sampleOnlyReleases = new List<VideoFileInfo>();

        foreach (var group in groups)
        {
            var files = group.Select(x => x.File).ToList();
            var samples = files.Where(f => IsSample(f.Path, group.Key)).ToList();
            if (samples.Count == 0) continue;

            var hasRealVideo = files.Count > samples.Count;
            if (hasRealVideo)
                redundantSamples.AddRange(samples);
            else
                sampleOnlyReleases.AddRange(samples);
        }

        var effectiveTriggerArrSearch = triggerArrSearch;
        if (sampleOnlyReleases.Count > MaxAutoSearchCount)
        {
            effectiveTriggerArrSearch = false;
            Report(
                $"Found {sampleOnlyReleases.Count} sample-only releases (over safety threshold of " +
                $"{MaxAutoSearchCount}). Skipping automatic Arr search for this run to avoid a search burst; " +
                "files will still be removed.");
        }

        _auditLines = [];
        Report($"Found {redundantSamples.Count} redundant sample(s) and {sampleOnlyReleases.Count} sample-only release(s).");

        if (isDryRun)
        {
            foreach (var sample in redundantSamples)
                _auditLines.Add($"Would remove redundant sample: {sample.Path}");
            foreach (var sample in sampleOnlyReleases)
                _auditLines.Add(effectiveTriggerArrSearch
                    ? $"Would remove sample-only release and trigger Arr search: {sample.Path}"
                    : $"Would remove sample-only release (Arr search skipped): {sample.Path}");
            Report($"Done. Identified {_auditLines.Count} sample file(s).");
            return;
        }

        foreach (var sample in redundantSamples)
            await RemoveRedundantSample(dbClient, sample).ConfigureAwait(false);

        foreach (var sample in sampleOnlyReleases)
            await RemoveSampleOnlyRelease(dbClient, sample, effectiveTriggerArrSearch).ConfigureAwait(false);

        Report($"Done. Removed {_auditLines.Count} sample file(s).");
    }

    private async Task RemoveRedundantSample(DavDatabaseClient dbClient, VideoFileInfo sample)
    {
        var davItem = await dbClient.Ctx.Items
            .FirstOrDefaultAsync(x => x.Id == sample.Id, CancellationToken).ConfigureAwait(false);
        if (davItem == null) return;

        dbClient.Ctx.Items.Remove(davItem);
        await dbClient.Ctx.SaveChangesAsync(CancellationToken).ConfigureAwait(false);
        var message = $"Removed redundant sample: {sample.Path}";
        _auditLines.Add(message);
        Report(message);
    }

    private async Task RemoveSampleOnlyRelease(DavDatabaseClient dbClient, VideoFileInfo sample, bool triggerSearch)
    {
        var davItem = await dbClient.Ctx.Items
            .FirstOrDefaultAsync(x => x.Id == sample.Id, CancellationToken).ConfigureAwait(false);
        if (davItem == null) return;

        if (triggerSearch && await TryRemoveAndSearch(dbClient, davItem, sample).ConfigureAwait(false))
            return;

        // fallback: no arr instance could be notified (or search was disabled) -- just remove locally
        dbClient.Ctx.Items.Remove(davItem);
        await dbClient.Ctx.SaveChangesAsync(CancellationToken).ConfigureAwait(false);
        var message = triggerSearch
            ? $"Removed sample-only release (no Arr match found): {sample.Path}"
            : $"Removed sample-only release (Arr search skipped): {sample.Path}";
        _auditLines.Add(message);
        Report(message);
    }

    private async Task<bool> TryRemoveAndSearch(DavDatabaseClient dbClient, DavItem davItem, VideoFileInfo sample)
    {
        var symlinkOrStrmPath = OrganizedLinksUtil.GetLink(davItem, configManager);
        if (symlinkOrStrmPath == null) return false;

        foreach (var arrClient in configManager.GetArrConfig().GetArrClients())
        {
            try
            {
                var rootFolders = await arrClient.GetRootFolders().ConfigureAwait(false);
                if (!rootFolders.Any(x => symlinkOrStrmPath.StartsWith(x.Path!))) continue;

                if (!await arrClient.RemoveAndSearch(symlinkOrStrmPath).ConfigureAwait(false)) return false;

                dbClient.Ctx.Items.Remove(davItem);
                await dbClient.Ctx.SaveChangesAsync(CancellationToken).ConfigureAwait(false);
                var message = $"Removed sample-only release and triggered Arr search: {sample.Path}";
                _auditLines.Add(message);
                Report(message);
                return true;
            }
            catch (Exception e) when (e is HttpRequestException { InnerException: System.Net.Sockets.SocketException })
            {
                Log.Debug($"Could not reach Arr instance `{arrClient.Host}` for sample cleanup: {e.Message}");
            }
        }

        return false;
    }

    private static string? GetMountFolderPrefix(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3 || segments[0] != "content") return null;
        return $"/{segments[0]}/{segments[1]}/{segments[2]}";
    }

    private static bool IsSample(string path, string mountFolderPrefix)
    {
        var relativePath = path.Length > mountFolderPrefix.Length ? path[mountFolderPrefix.Length..] : "";
        var relativeSegments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return relativeSegments.Any(FilenameUtil.IsSampleFile);
    }

    private void Report(string message)
    {
        var dryRun = isDryRun ? "Dry Run - " : string.Empty;
        _ = websocketManager.SendMessage(WebsocketTopic.SampleCleanupTaskProgress, $"{dryRun}{message}");
    }

    public static string GetAuditReport()
    {
        return _auditLines.Count > 0
            ? string.Join("\n", _auditLines)
            : "This list is Empty.\nYou must first run the task.";
    }
}
