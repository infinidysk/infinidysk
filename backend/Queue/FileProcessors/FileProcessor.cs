using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Queue.FileProcessors;

public class FileProcessor(
    GetFileInfosStep.FileInfo fileInfo,
    INntpClient usenetClient,
    ConfigManager configManager,
    CancellationToken ct
) : BaseProcessor
{
    public override async Task<BaseProcessor.Result?> ProcessAsync()
    {
        try
        {
            var fileSize = fileInfo.FileSize ?? await usenetClient
                .GetFileSizeAsync(fileInfo.NzbFile, ct)
                .ConfigureAwait(false);

            await fileInfo.NzbFile.ProbeSecondSegmentRangeAsync(
                usenetClient, fileSize, ct).ConfigureAwait(false);

            return new Result()
            {
                NzbFile = fileInfo.NzbFile,
                FileName = fileInfo.FileName,
                FileSize = fileSize,
                ReleaseDate = fileInfo.ReleaseDate,
                SniffedVideoExtension = fileInfo.SniffedVideoExtension,
            };
        }

        // Ignore missing articles if it's not a media file (default).
        // In that case, simply skip the file altogether.
        // Accepted limitation: this check uses the original filename, not a
        // sniffed extension applied later at mount time.
        catch (UsenetArticleNotFoundException) when (
            !FilenameUtil.IsMediaFile(fileInfo.FileName)
            && configManager.IsSkipNonMediaOnMissingArticlesEnabled())
        {
            Log.Warning(
                "File {FileName} has missing articles; skipping it because it is not a media file",
                fileInfo.FileName);
            return null;
        }
    }

    public new class Result : BaseProcessor.Result
    {
        public required NzbFile NzbFile { get; init; }
        public required string FileName { get; init; }
        public required long FileSize { get; init; }
        public required DateTimeOffset ReleaseDate { get; init; }
        public string? SniffedVideoExtension { get; init; }
    }
}
