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
            };
        }

        // Ignore missing articles if it's not a video file (default).
        // In that case, simply skip the file altogether.
        catch (UsenetArticleNotFoundException) when (
            !FilenameUtil.IsVideoFile(fileInfo.FileName)
            && configManager.IsSkipNonVideoOnMissingArticlesEnabled())
        {
            Log.Warning(
                "File {FileName} has missing articles; skipping it because it is not a video",
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
    }
}
