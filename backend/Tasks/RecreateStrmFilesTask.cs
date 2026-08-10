using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Queue.PostProcessors;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Tasks;

/// <summary>
/// Writes missing STRM sidecars for mounted video DavItems, or rewrites all when
/// <paramref name="rewriteAll"/> is true (e.g. after a base-url change).
/// </summary>
public class RecreateStrmFilesTask(
    ConfigManager configManager,
    DavDatabaseClient dbClient,
    WebsocketManager websocketManager,
    bool rewriteAll = false
) : BaseTask
{
    private const int BatchSize = 100;

    protected override async Task ExecuteInternal()
    {
        try
        {
            var ct = SigtermUtil.GetCancellationToken();
            await RecreateAllAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Report($"Failed: {e.Message}");
            e.LogWarningKnownOrStack("Failed to recreate STRM files.");
        }
    }

    private async Task RecreateAllAsync(CancellationToken ct)
    {
        var written = 0;
        var skipped = 0;
        var debounce = DebounceUtil.CreateDebounce(TimeSpan.FromMilliseconds(200));
        ReportProgress("Scanning mounted videos...", written, skipped);

        var offset = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await dbClient.Ctx.Items
                .AsNoTracking()
                .Where(x => x.Type == DavItem.ItemType.UsenetFile)
                .OrderBy(x => x.Path)
                .Skip(offset)
                .Take(BatchSize)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (batch.Count == 0)
                break;

            foreach (var item in batch)
            {
                if (!FilenameUtil.IsVideoFile(item.Name)
                    || item.Name.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                var path = CreateStrmFilesPostProcessor.GetStrmFilePath(configManager, item);
                var target = CreateStrmFilesPostProcessor.GetStrmTargetUrl(configManager, item);
                if (!rewriteAll && File.Exists(path))
                {
                    var existing = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                    if (existing == target)
                    {
                        skipped++;
                        debounce(() => ReportProgress("Writing STRM files...", written, skipped));
                        continue;
                    }
                }

                await CreateStrmFilesPostProcessor
                    .WriteStrmFileAsync(configManager, item, rewriteAll, ct)
                    .ConfigureAwait(false);
                written++;
                debounce(() => ReportProgress("Writing STRM files...", written, skipped));
            }

            offset += batch.Count;
            if (batch.Count < BatchSize)
                break;
        }

        ReportProgress("Done!", written, skipped);
    }

    private void Report(string message)
    {
        _ = websocketManager.SendMessage(WebsocketTopic.RecreateStrmTaskProgress, message);
    }

    private void ReportProgress(string message, int written, int skipped)
    {
        var mode = rewriteAll ? "rewrite-all" : "missing-or-changed";
        Report($"{message}\nMode: {mode}\nWritten: {written}\nSkipped: {skipped}");
    }
}
