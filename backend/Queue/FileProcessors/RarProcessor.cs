using System.Text.RegularExpressions;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;
using SharpCompress.Common.Rar.Headers;

namespace NzbWebDAV.Queue.FileProcessors;

public class RarProcessor(
    GetFileInfosStep.FileInfo fileInfo,
    INntpClient usenetClient,
    string? password,
    CancellationToken ct
) : BaseProcessor
{
    public override async Task<BaseProcessor.Result?> ProcessAsync()
    {
        await using var stream = await GetNzbFileStream().ConfigureAwait(false);
        var first16KB = await VideoSignatureUtil.ReadFirst16KBAsync(stream, ct).ConfigureAwait(false);
        var headers = await RarUtil.GetRarHeadersAsync(stream, password, ct).ConfigureAwait(false);
        // Validate the uniform-size inference before StoredFileSegments flow into
        // RarAggregator (or NestedRarExpansionStep), which persists the part's
        // segment byte ranges. Probing after header reading reuses any range the
        // article cache learned while parsing.
        await fileInfo.NzbFile.ProbeSecondSegmentRangeAsync(usenetClient, stream.Length, ct)
            .ConfigureAwait(false);
        var archiveName = GetArchiveName();
        var partNumber = GetPartNumber(headers);
        return new Result()
        {
            StoredFileSegments = headers
                .OfType<IRarFileHeader>()
                .Select(x => new StoredFileSegment()
                {
                    NzbFile = fileInfo.NzbFile,
                    PartSize = ResolvePartSize(
                        stream.Length,
                        x.DataStartPosition,
                        x.AdditionalDataSize),
                    ArchiveName = archiveName,
                    PartNumber = partNumber,
                    PathWithinArchive = x.FileName,
                    ByteRangeWithinPart = LongRange.FromStartAndSize(
                        x.DataStartPosition,
                        x.AdditionalDataSize
                    ),
                    AesParams = x.GetAesParams(password),
                    FileUncompressedSize = x.UncompressedSize,
                    IsUncompressedSizeUnknown = x.IsUncompressedSizeUnknown,
                    ReleaseDate = fileInfo.ReleaseDate,
                    SniffedVideoExtension = VideoSignatureUtil.SniffMemberFromFirst16KB(
                        first16KB,
                        x.DataStartPosition,
                        x.GetAesParams(password) is not null),
                }).ToArray(),
        };
    }

    internal static long ResolvePartSize(long declaredSize, long dataStart, long dataSize) =>
        Math.Max(declaredSize, checked(dataStart + dataSize));

    private string GetArchiveName()
    {
        // remove the .rar extension and remove the .partXX if it exists
        var sansExtension = Path.GetFileNameWithoutExtension(fileInfo.FileName);
        sansExtension = Regex.Replace(sansExtension, @"\.part\d+$", "");
        return sansExtension;
    }

    private PartNumber GetPartNumber(List<IRarHeader> rarHeaders)
    {
        var partNumber = new PartNumber()
        {
            PartNumberFromHeader = GetPartNumberFromHeaders(rarHeaders),
            PartNumberFromFilename = GetPartNumberFromFilename(fileInfo.FileName),
        };

        if (partNumber.PartNumberFromHeader == null && partNumber.PartNumberFromFilename == null)
            throw new InvalidOperationException("Could not determine part number for RAR file.");

        return partNumber;
    }

    private static int? GetPartNumberFromHeaders(List<IRarHeader> headers)
    {
        var archiveHeader = headers.OfType<IRarArchiveHeader>().FirstOrDefault();
        if (archiveHeader?.VolumeNumber is { } archiveVolume) return archiveVolume;

        var endHeader = headers.OfType<IRarEndArchiveHeader>().FirstOrDefault();
        if (endHeader?.VolumeNumber is { } endVolume) return endVolume;

        if (archiveHeader is not null && archiveHeader.IsFirstVolume) return -1;
        return null;
    }

    private static int? GetPartNumberFromFilename(string filename)
    {
        // handle the `.partXXX.rar` format
        var partMatch = Regex.Match(filename, @"\.part(\d+)\.rar$", RegexOptions.IgnoreCase);
        if (partMatch.Success)
            return int.Parse(partMatch.Groups[1].Value);

        // handle the `.rXXX` format
        var rMatch = Regex.Match(filename, @"\.r(\d+)$", RegexOptions.IgnoreCase);
        if (rMatch.Success)
            return int.Parse(rMatch.Groups[1].Value);

        // handle the `.rar` format.
        if (filename.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
            return -1;

        //  could not determine from filename
        return null;
    }

    private async Task<NzbFileStream> GetNzbFileStream()
    {
        var filesize = fileInfo.FileSize;
        filesize ??= await usenetClient.GetFileSizeAsync(fileInfo.NzbFile, ct).ConfigureAwait(false);
        return usenetClient.GetFileStream(fileInfo.NzbFile, filesize!.Value, articleBufferSize: 0);
    }

    public new class Result : BaseProcessor.Result
    {
        public required StoredFileSegment[] StoredFileSegments { get; init; }
    }

    public class StoredFileSegment
    {
        public required NzbFile NzbFile { get; init; }
        public required long PartSize { get; init; }
        public required string ArchiveName { get; init; }
        public required PartNumber PartNumber { get; init; }
        public required DateTimeOffset ReleaseDate { get; init; }

        public required string PathWithinArchive { get; init; }
        public required LongRange ByteRangeWithinPart { get; init; }
        public required AesParams? AesParams { get; init; }

        public required long FileUncompressedSize { get; init; }

        public bool IsUncompressedSizeUnknown { get; init; }

        public string? SniffedVideoExtension { get; init; }
    }

    public record PartNumber
    {
        public int? PartNumberFromHeader { get; init; }
        public int? PartNumberFromFilename { get; init; }
    }
}
