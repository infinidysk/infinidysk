using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Queue.PostProcessors;

/// <summary>
/// Reads the opening and closing bytes of direct media outputs before SAB reports
/// completion. This intentionally runs only for categories that already opted into
/// article-health checks: it is an additional BODY-level signal, not a global delay.
/// </summary>
internal sealed class FinalMediaReadinessValidator(
    DavDatabaseClient dbClient,
    INntpClient usenetClient,
    ConfigManager configManager)
{
    private const int ProbeBytes = VideoSignatureUtil.First16KBLength;

    public async Task ValidateAsync(CancellationToken ct)
    {
        var items = dbClient.Ctx.ChangeTracker.Entries<DavItem>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity)
            .Where(item => item.SubType == DavItem.ItemSubType.NzbFile && FilenameUtil.IsMediaFile(item.Name))
            .ToList();

        foreach (var item in items)
        {
            var payload = dbClient.Ctx.NzbFiles.Local.FirstOrDefault(file => file.Id == item.Id);
            if (payload is null)
                throw new NonRetryableDownloadException(
                    $"Import readiness check could not load media payload for {item.Name}.");

            try
            {
                await using var stream = usenetClient.GetFileStream(
                    payload.SegmentIds,
                    item.FileSize ?? 0,
                    configManager.GetArticleBufferSize(),
                    payload.SegmentByteRanges,
                    configManager.IsPipelinedBodyRequestsEnabled(),
                    item.Path,
                    payload.SegmentFallbackIds,
                    useContainerAwareFill: configManager.IsContainerAwareFillEnabled(),
                    streamingBodyBatchWidth: configManager.GetStreamingBodyBatchWidth());

                var head = await ReadExactlyAtAsync(stream, 0, Math.Min(ProbeBytes, item.FileSize ?? 0), ct)
                    .ConfigureAwait(false);
                ValidateContainerSignature(item, head);

                var tailStart = Math.Max(0, (item.FileSize ?? 0) - ProbeBytes);
                _ = await ReadExactlyAtAsync(stream, tailStart, (item.FileSize ?? 0) - tailStart, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e.IsNonRetryableDownloadException())
            {
                throw new NonRetryableDownloadException(
                    $"Import readiness check found unreadable media bytes for {item.Name}.", e);
            }
            catch (Exception e) when (e is not OutOfMemoryException && !e.IsCancellationException(ct))
            {
                throw new RetryableDownloadException(
                    $"Import readiness check could not read media bytes for {item.Name}.", e);
            }
        }
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

    private static void ValidateContainerSignature(DavItem item, byte[] head)
    {
        var extension = Path.GetExtension(item.Name);
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
                $"Import readiness check found an invalid media container header for {item.Name}.");
        }
    }
}
