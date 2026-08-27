using System.Diagnostics.CodeAnalysis;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Queue.FileAggregators;

public class RarAggregator(DavDatabaseClient dbClient, DavItem mountDirectory, bool checkedFullHealth) : BaseAggregator
{
    protected override DavDatabaseClient DBClient => dbClient;
    protected override DavItem MountDirectory => mountDirectory;

    public override void UpdateDatabase(List<BaseProcessor.Result> processorResults)
    {
        var fileSegments = processorResults
            .OfType<RarProcessor.Result>()
            .SelectMany(x => x.StoredFileSegments)
            .ToList();

        ProcessArchive(fileSegments);

        foreach (var lazy in processorResults.OfType<LazyRarProcessor.Result>())
            ProcessLazyArchive(lazy);
    }

    private void ProcessLazyArchive(LazyRarProcessor.Result result)
    {
        var pathInArchive = result.PathInArchive;
        var parentDirectory = EnsureParentDirectory(pathInArchive);
        var name = ImportableVideoNamer.Normalize(
            SanitizeDavName(Path.GetFileName(pathInArchive)),
            result.SniffedVideoExtension,
            mountDirectory.Name,
            allowBaseRename: true);

        var davMultipartFile = new DavMultipartFile
        {
            Id = Guid.NewGuid(),
            Metadata = new DavMultipartFile.Meta
            {
                AesParams = result.AesParams,
                FileParts = [result.FirstPart],
                IsLazy = true,
                PathInArchive = pathInArchive,
                ArchivePassword = result.Password,
                PendingParts = result.PendingParts,
                ExpectedFileSize = result.TotalFileSize,
            }
        };

        var davItem = DavItem.New(
            id: Guid.NewGuid(),
            parent: parentDirectory,
            name: name,
            fileSize: result.TotalFileSize,
            type: DavItem.ItemType.UsenetFile,
            subType: DavItem.ItemSubType.MultipartFile,
            releaseDate: result.ReleaseDate,
            // Lazy mounts skip the per-part health check entirely; mark
            // unchecked so the background health-check sweep covers them.
            lastHealthCheck: null,
            historyItemId: MountDirectory.HistoryItemId,
            fileBlobId: davMultipartFile.Id,
            nzbBlobId: MountDirectory.HistoryItemId
        );

        dbClient.Ctx.Items.Add(davItem);
        dbClient.Ctx.AddBlob(davMultipartFile);
    }

    private void ProcessArchive(List<RarProcessor.StoredFileSegment> fileSegments)
    {
        var archiveFiles = new Dictionary<string, List<RarProcessor.StoredFileSegment>>();
        foreach (var fileSegment in fileSegments)
        {
            if (!archiveFiles.ContainsKey(fileSegment.PathWithinArchive))
                archiveFiles.Add(fileSegment.PathWithinArchive, []);

            archiveFiles[fileSegment.PathWithinArchive].Add(fileSegment);
        }

        foreach (var archiveFile in archiveFiles)
        {
            // Ensure we have all volumes necessary for this file.
            ValidateVolumes(archiveFile.Value);

            // Initialize dav-item fields
            var pathWithinArchive = archiveFile.Key;
            var fileParts = SortByPartNumber(archiveFile.Value);
            var (fileSize, aesParams) = ResolvePublishedSizeAndAes(fileParts);
            var parentDirectory = EnsureParentDirectory(pathWithinArchive);
            var sniffedVideoExtension = archiveFile.Value
                .Select(x => x.SniffedVideoExtension)
                .FirstOrDefault(x => x is not null);
            var name = ImportableVideoNamer.Normalize(
                SanitizeDavName(Path.GetFileName(pathWithinArchive)),
                sniffedVideoExtension,
                mountDirectory.Name,
                allowBaseRename: archiveFiles.Count == 1);

            var davMultipartFile = new DavMultipartFile()
            {
                Id = Guid.NewGuid(),
                Metadata = new DavMultipartFile.Meta()
                {
                    AesParams = aesParams,
                    FileParts = fileParts.Select(x => new DavMultipartFile.FilePart()
                    {
                        SegmentIds = x.NzbFile.GetSegmentIds(),
                        SegmentIdByteRange = LongRange.FromStartAndSize(0, x.PartSize),
                        FilePartByteRange = x.ByteRangeWithinPart,
                        SegmentByteRanges = x.NzbFile.GetSegmentByteRanges(),
                        SegmentFallbackIds = x.NzbFile.GetSegmentFallbackIds(),
                    }).ToArray(),
                }
            };

            var davItem = DavItem.New(
                id: Guid.NewGuid(),
                parent: parentDirectory,
                name: name,
                fileSize: fileSize,
                type: DavItem.ItemType.UsenetFile,
                subType: DavItem.ItemSubType.MultipartFile,
                releaseDate: fileParts.First().ReleaseDate,
                lastHealthCheck: checkedFullHealth ? DateTimeOffset.UtcNow : null,
                historyItemId: MountDirectory.HistoryItemId,
                fileBlobId: davMultipartFile.Id,
                nzbBlobId: MountDirectory.HistoryItemId
            );

            dbClient.Ctx.Items.Add(davItem);
            dbClient.Ctx.AddBlob(davMultipartFile);
        }
    }

    internal enum PartNumberSource
    {
        Header,
        Filename,
        Mixed
    }

    internal static PartNumberSource SelectPartNumberSource(
        List<RarProcessor.StoredFileSegment> storedFileSegments)
    {
        var headerNumbers = storedFileSegments
            .Select(x => x.PartNumber.PartNumberFromHeader)
            .Where(n => n is >= 0)
            .Select(n => n!.Value)
            .ToList();
        var filenameNumbers = storedFileSegments
            .Select(x => x.PartNumber.PartNumberFromFilename)
            .Where(n => n is >= 0)
            .Select(n => n!.Value)
            .ToList();

        var headerPresentForAll = storedFileSegments.All(x => x.PartNumber.PartNumberFromHeader is >= 0);
        var filenamePresentForAll = storedFileSegments.All(x => x.PartNumber.PartNumberFromFilename is >= 0);
        var headerDistinct = headerNumbers.Count > 0
                             && headerNumbers.Distinct().Count() == headerNumbers.Count;
        var filenameDistinct = filenameNumbers.Count > 0
                               && filenameNumbers.Distinct().Count() == filenameNumbers.Count;

        if (headerPresentForAll && headerDistinct)
            return PartNumberSource.Header;

        if (filenamePresentForAll && filenameDistinct)
        {
            if (headerNumbers.Count > 0 && !headerDistinct)
            {
                Log.Debug(
                    "RAR header volume numbers are not distinct; using filename-derived part numbers instead");
            }

            return PartNumberSource.Filename;
        }

        return PartNumberSource.Mixed;
    }

    internal static RarProcessor.StoredFileSegment[] SortByPartNumber(
        List<RarProcessor.StoredFileSegment> storedFileSegments)
    {
        var source = SelectPartNumberSource(storedFileSegments);

        int? delta = null;
        if (source == PartNumberSource.Mixed)
        {
            // Find delta between part number from headers and filenames.
            delta = storedFileSegments
                .Select(x => x.PartNumber)
                .Where(x => x is { PartNumberFromHeader: >= 0, PartNumberFromFilename: >= 0 })
                .Select(x => x.PartNumberFromHeader - x.PartNumberFromFilename)
                .GroupBy(x => x)
                .MaxBy(x => x.Count())?.Key;
        }

        var withNumbers = storedFileSegments
            .Select(x => (Segment: x, Part: GetNormalizedPartNumber(x.PartNumber, delta, source)))
            .ToList();

        // Drop identical physical duplicates before uniqueness validation.
        var deduped = DeduplicateIdenticalVolumes(withNumbers);

        ValidatePartNumbers(deduped.Select(x => x.Part));

        return deduped
            .OrderBy(x => x.Part)
            .Select(x => x.Segment)
            .ToArray();
    }

    private static List<(RarProcessor.StoredFileSegment Segment, int Part)> DeduplicateIdenticalVolumes(
        List<(RarProcessor.StoredFileSegment Segment, int Part)> parts)
    {
        var result = new List<(RarProcessor.StoredFileSegment Segment, int Part)>();
        foreach (var group in parts.GroupBy(x => x.Part))
        {
            var entries = group.ToList();
            if (entries.Count == 1)
            {
                result.Add(entries[0]);
                continue;
            }

            var first = entries[0].Segment;
            var firstMessageId = first.NzbFile.Segments[0].MessageId;
            var allIdentical = entries.All(e =>
                e.Segment.PartSize == first.PartSize
                && e.Segment.ByteRangeWithinPart == first.ByteRangeWithinPart
                && e.Segment.PathWithinArchive == first.PathWithinArchive
                && e.Segment.FileUncompressedSize == first.FileUncompressedSize
                && string.Equals(
                    e.Segment.NzbFile.Segments[0].MessageId,
                    firstMessageId,
                    StringComparison.Ordinal));

            if (allIdentical)
            {
                Log.Warning(
                    "Dropping duplicate RAR volume {Part} for {Archive}",
                    group.Key, first.ArchiveName);
                result.Add(entries[0]);
            }
            else
            {
                // Leave ambiguous group intact so ValidatePartNumbers throws.
                result.AddRange(entries);
            }
        }

        return result;
    }

    internal static void ValidateVolumes(List<RarProcessor.StoredFileSegment> storedFileSegments)
    {
        if (storedFileSegments.Count == 0) return;

        var knownSizes = storedFileSegments
            .Where(x => !x.IsUncompressedSizeUnknown)
            .Select(x => x.FileUncompressedSize)
            .Distinct()
            .ToList();

        if (knownSizes.Count > 1)
            throw new InvalidDataException("Inconsistent rar file size detected.");

        var actual = storedFileSegments.Sum(x => x.ByteRangeWithinPart.Count);
        if (knownSizes.Count == 1)
        {
            var expected = knownSizes[0];
            if (Math.Abs(actual - expected) > 16)
                throw new InvalidDataException("Missing rar volumes detected.");
            return;
        }

        // All declarations unknown: stored unencrypted can use packed sum; encrypted cannot.
        var isEncrypted = storedFileSegments.Any(x => x.AesParams is not null);
        if (isEncrypted)
        {
            throw new UnsupportedRarUnknownSizeException(
                "Encrypted RAR with unknown uncompressed size is not supported.");
        }
    }

    internal static long ResolvePublishedFileSize(IReadOnlyList<RarProcessor.StoredFileSegment> fileParts)
        => ResolvePublishedSizeAndAes(fileParts).FileSize;

    internal static (long FileSize, AesParams? AesParams) ResolvePublishedSizeAndAes(
        IReadOnlyList<RarProcessor.StoredFileSegment> fileParts)
    {
        if (fileParts.Count == 0)
            throw new InvalidDataException("RAR archive has no file parts.");

        var packedSum = fileParts.Sum(x => x.ByteRangeWithinPart.Count);
        var knownSizes = fileParts
            .Where(x => !x.IsUncompressedSizeUnknown
                        && x.FileUncompressedSize > 0
                        && x.FileUncompressedSize != long.MaxValue)
            .Select(x => x.FileUncompressedSize)
            .Distinct()
            .ToList();

        var aesParams = fileParts.Select(x => x.AesParams).FirstOrDefault(x => x != null);
        if (aesParams is null)
            return (knownSizes.Count == 1 ? knownSizes[0] : packedSum, null);

        long decoded;
        if (aesParams.DecodedSize > 0 && aesParams.DecodedSize != long.MaxValue)
            decoded = aesParams.DecodedSize;
        else if (knownSizes.Count == 1)
            decoded = knownSizes[0];
        else
        {
            throw new UnsupportedRarUnknownSizeException(
                "Encrypted RAR with unknown uncompressed size is not supported.");
        }

        if (aesParams.DecodedSize != decoded)
        {
            aesParams = new AesParams
            {
                Key = aesParams.Key,
                Iv = aesParams.Iv,
                DecodedSize = decoded,
            };
        }

        return (decoded, aesParams);
    }

    private static void ValidatePartNumbers(IEnumerable<int> partNumbers)
    {
        var seen = new HashSet<int>();
        foreach (var partNumber in partNumbers)
        {
            if (!seen.Add(partNumber))
                throw new InvalidDataException("Rar archive has duplicate volume numbers.");
        }
    }

    private static int GetNormalizedPartNumber(
        RarProcessor.PartNumber partNumber, int? delta, PartNumberSource source)
    {
        return source switch
        {
            PartNumberSource.Header when partNumber.PartNumberFromHeader is >= 0 or -1
                => partNumber.PartNumberFromHeader!.Value,
            PartNumberSource.Filename when partNumber.PartNumberFromFilename is >= 0 or -1
                => partNumber.PartNumberFromFilename!.Value,
            PartNumberSource.Mixed when partNumber.PartNumberFromHeader >= 0
                => partNumber.PartNumberFromHeader!.Value,
            PartNumberSource.Mixed when partNumber.PartNumberFromFilename >= 0
                => partNumber.PartNumberFromFilename!.Value + (delta ?? 0),
            _ when partNumber.PartNumberFromHeader is -1 => -1,
            _ when partNumber.PartNumberFromFilename is -1 => -1,
            _ => -1
        };
    }
}
