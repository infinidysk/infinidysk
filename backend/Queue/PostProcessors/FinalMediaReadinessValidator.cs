using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.FileAggregators;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Queue.PostProcessors;

/// <summary>
/// Reads the opening and closing bytes of direct media outputs before SAB reports
/// completion. Probes run unbuffered and unpipelined — one segment at a time — and
/// under a non-rooted label, so they neither hold playback-scale prefetch windows
/// nor consult or seed the process-wide playback-hole tracker.
/// </summary>
internal sealed class FinalMediaReadinessValidator(
    INntpClient usenetClient,
    ConfigManager configManager)
{
    private const int ProbeBytes = VideoSignatureUtil.First16KBLength;

    /// <summary>A direct media output to probe: the mounted name and its NZB source.</summary>
    internal sealed record ProbeTarget(string Name, NzbFile NzbFile, long FileSize);

    /// <summary>
    /// Plans the same direct media files the import will mount, minus the files output
    /// filtering will remove (blocklist globs and the sample heuristic), so probes only
    /// ever run for files that will actually be served.
    /// </summary>
    internal static IReadOnlyList<ProbeTarget> PlanTargets(
        List<BaseProcessor.Result> processorResults,
        string category,
        string mountName,
        ConfigManager configManager)
    {
        var directFiles = FileAggregator.PlanDirectFiles(processorResults, mountName);
        var largestVideoFileSize = PlannedImportOutputs.GetLargestVideoFileSize(processorResults, mountName);
        var blocklistedFilenames = configManager.GetBlocklistedFiles();
        var sampleFilterEnabled = configManager.IsSampleFilterEnabled();

        var targets = new List<ProbeTarget>();
        foreach (var file in directFiles)
        {
            if (!FilenameUtil.IsMediaFile(file.Name)) continue;
            var davPath = PlannedDavPath(category, mountName, file);
            if (FileFilterUtil.GetRemovalReason(
                    file.Name,
                    file.FileSize,
                    davPath,
                    largestVideoFileSize,
                    blocklistedFilenames,
                    sampleFilterEnabled) is not null)
                continue;

            targets.Add(new ProbeTarget(file.Name, file.NzbFile, file.FileSize));
        }

        return targets;
    }

    public async Task ValidateAsync(IReadOnlyList<ProbeTarget> targets, CancellationToken ct)
    {
        foreach (var target in targets)
        {
            try
            {
                await using var stream = usenetClient.GetFileStream(
                    target.NzbFile,
                    target.FileSize,
                    articleBufferSize: 0,
                    usePipelinedBodyRequests: false,
                    fileName: $"import-readiness {target.Name}",
                    useContainerAwareFill: configManager.IsContainerAwareFillEnabled(),
                    streamingBodyBatchWidth: 1);

                var head = await ReadExactlyAtAsync(stream, 0, Math.Min(ProbeBytes, target.FileSize), ct)
                    .ConfigureAwait(false);
                ValidateContainerSignature(target.Name, head);

                var tailStart = Math.Max(0, target.FileSize - ProbeBytes);
                _ = await ReadExactlyAtAsync(stream, tailStart, target.FileSize - tailStart, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e.IsNonRetryableDownloadException())
            {
                throw new NonRetryableDownloadException(
                    $"Import readiness check found unreadable media bytes for {target.Name}.", e);
            }
            catch (Exception e) when (e is not OutOfMemoryException && !e.IsCancellationException(ct))
            {
                throw new RetryableDownloadException(
                    $"Import readiness check could not read media bytes for {target.Name}.", e);
            }
        }
    }

    private static string PlannedDavPath(
        string category,
        string mountName,
        FileAggregator.PlannedDirectFile file)
    {
        // Mirrors the mounted path shape (/content/<category>/<job>/<dirs>/<name>) so
        // FileFilterUtil.HasSampleDirectory sees the same release subfolders. That check
        // skips the category and job segments, so the planned (pre-increment) mount name
        // is sufficient here.
        var directorySegments = file.RelativePath
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/'],
                StringSplitOptions.RemoveEmptyEntries)
            .SkipLast(1)
            .Select(segment => PathSanitizer.SanitizeComponent(segment));
        return string.Join(
            '/',
            new[] { DavItem.ContentFolder.Path, category, mountName }
                .Concat(directorySegments)
                .Append(file.Name));
    }

    private static async Task<byte[]> ReadExactlyAtAsync(Stream stream, long position, long length, CancellationToken ct)
    {
        if (length <= 0)
            throw new NonRetryableDownloadException("Import readiness check found an empty media file.");

        stream.Position = position;
        var buffer = new byte[length];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (count == 0)
                throw new NonRetryableDownloadException(
                    $"Import readiness check received {read} of {buffer.Length} expected bytes.");
            read += count;
        }

        return buffer;
    }

    private static void ValidateContainerSignature(string name, byte[] head)
    {
        var extension = Path.GetExtension(name);
        var expected = extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webm", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".avi", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".wmv", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".flv", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ts", StringComparison.OrdinalIgnoreCase);
        if (expected && VideoSignatureUtil.GuessVideoExtension(head) is null)
        {
            throw new NonRetryableDownloadException(
                $"Import readiness check found an invalid media container header for {name}.");
        }
    }
}
