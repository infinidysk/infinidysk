using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Queue.FileProcessors;

public class MultipartMkvProcessor : BaseProcessor
{
    private readonly List<GetFileInfosStep.FileInfo> _fileInfos;
    private readonly INntpClient _usenetClient;
    private readonly CancellationToken _ct;

    public MultipartMkvProcessor
    (
        List<GetFileInfosStep.FileInfo> fileInfos,
        INntpClient usenetClient,
        CancellationToken ct
    )
    {
        _fileInfos = fileInfos;
        _usenetClient = usenetClient;
        _ct = ct;
    }

    public override async Task<BaseProcessor.Result?> ProcessAsync()
    {
        var sortedFileInfos = _fileInfos
            .OrderBy(f => FilenameUtil.GetSplitVideoPartNumber(f.FileName) ?? int.MaxValue)
            .ToList();

        var partNumbers = sortedFileInfos
            .Select(f => FilenameUtil.GetSplitVideoPartNumber(f.FileName))
            .ToList();
        if (partNumbers.Any(n => n is null)
            || partNumbers[0] != 1
            || partNumbers.Select((n, i) => n == i + 1).Any(ok => !ok))
        {
            Log.Warning(
                "Split video set `{FileName}` has non-contiguous part numbers ({Parts}); mounting anyway",
                FilenameUtil.GetSplitVideoBaseName(sortedFileInfos.First().FileName)
                    ?? sortedFileInfos.First().FileName,
                string.Join(",", partNumbers.Select(n => n?.ToString() ?? "?")));
        }

        var fileParts = new List<DavMultipartFile.FilePart>();
        foreach (var fileInfo in sortedFileInfos)
        {
            var partSize = fileInfo.FileSize ?? await _usenetClient
                .GetFileSizeAsync(fileInfo.NzbFile, _ct)
                .ConfigureAwait(false);

            // Validate the uniform-size inference before persisting this part's
            // segment byte ranges.
            await fileInfo.NzbFile.ProbeSecondSegmentRangeAsync(_usenetClient, partSize, _ct)
                .ConfigureAwait(false);
            var rangeIndex = fileInfo.NzbFile.GetSegmentByteRangeIndex();

            fileParts.Add(new DavMultipartFile.FilePart
            {
                SegmentIds = fileInfo.NzbFile.GetSegmentIds(),
                SegmentIdByteRange = LongRange.FromStartAndSize(0, partSize),
                FilePartByteRange = LongRange.FromStartAndSize(0, partSize),
                SegmentByteRanges = rangeIndex.Ranges,
                SegmentByteRangesTrusted = rangeIndex.IsTrusted,
                SegmentFallbackIds = fileInfo.NzbFile.GetSegmentFallbackIds(),
            });
        }

        return new Result
        {
            Filename = FilenameUtil.GetSplitVideoBaseName(sortedFileInfos.First().FileName)
                       ?? sortedFileInfos.First().FileName,
            Parts = fileParts,
            ReleaseDate = sortedFileInfos.First().ReleaseDate,
        };
    }

    public new class Result : BaseProcessor.Result
    {
        public required string Filename { get; init; }
        public required List<DavMultipartFile.FilePart> Parts { get; init; } = [];
        public required DateTimeOffset ReleaseDate { get; init; }
    }
}
