using System.Text.RegularExpressions;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Queue.FileAggregators;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;
using Serilog;
using SharpCompress.Common.Rar.Headers;

namespace NzbWebDAV.Queue.NestedRarExpansion;

/// <summary>
/// Expands stored nested RAR members into their inner files before
/// <see cref="RarAggregator"/> runs. Failures fall back to mounting the
/// opaque <c>.rar</c> member exactly as before.
/// </summary>
public static class NestedRarExpansionStep
{
    public const int DefaultMaxDepth = 3;
    public const int MaxInnerFilesPerArchive = 500;

    public static async Task<List<BaseProcessor.Result>> ExpandAsync(
        List<BaseProcessor.Result> processorResults,
        INntpClient usenetClient,
        string? password,
        CancellationToken ct,
        int maxDepth = DefaultMaxDepth)
    {
        return await ExpandAsync(
            processorResults,
            (segments, token) => OpenComposedStreamAsync(segments, usenetClient, token),
            password,
            ct,
            maxDepth).ConfigureAwait(false);
    }

    /// <summary>Test seam that injects a composed-stream factory.</summary>
    internal static async Task<List<BaseProcessor.Result>> ExpandAsync(
        List<BaseProcessor.Result> processorResults,
        Func<RarProcessor.StoredFileSegment[], CancellationToken, Task<Stream>> openComposedStream,
        string? password,
        CancellationToken ct,
        int maxDepth = DefaultMaxDepth)
    {
        var passthrough = processorResults
            .Where(result => result is not RarProcessor.Result)
            .ToList();
        var segments = processorResults
            .OfType<RarProcessor.Result>()
            .SelectMany(result => result.StoredFileSegments)
            .ToList();

        if (segments.Count == 0)
            return processorResults;

        var expanded = await ExpandSegmentsAsync(
            segments, openComposedStream, password, ct, maxDepth).ConfigureAwait(false);

        var results = new List<BaseProcessor.Result>(passthrough.Count + 1);
        results.AddRange(passthrough);
        if (expanded.Count > 0)
        {
            results.Add(new RarProcessor.Result
            {
                StoredFileSegments = expanded.ToArray(),
            });
        }

        return results;
    }

    internal static async Task<List<RarProcessor.StoredFileSegment>> ExpandSegmentsAsync(
        List<RarProcessor.StoredFileSegment> segments,
        Func<RarProcessor.StoredFileSegment[], CancellationToken, Task<Stream>> openComposedStream,
        string? password,
        CancellationToken ct,
        int maxDepth = DefaultMaxDepth)
    {
        var current = segments;
        for (var depth = 0; depth < maxDepth; depth++)
        {
            var nestedGroups = current
                .GroupBy(segment => segment.PathWithinArchive, StringComparer.Ordinal)
                .Where(group => FilenameUtil.IsRarFile(Path.GetFileName(group.Key)))
                .ToList();
            if (nestedGroups.Count == 0)
                break;

            var next = new List<RarProcessor.StoredFileSegment>(current.Count);
            var expandedAny = false;

            foreach (var group in current.GroupBy(segment => segment.PathWithinArchive, StringComparer.Ordinal))
            {
                var members = group.ToList();
                if (!FilenameUtil.IsRarFile(Path.GetFileName(group.Key)))
                {
                    next.AddRange(members);
                    continue;
                }

                var expanded = await TryExpandGroupAsync(
                    members, openComposedStream, password, ct).ConfigureAwait(false);
                if (expanded is null)
                {
                    next.AddRange(members);
                    continue;
                }

                expandedAny = true;
                next.AddRange(expanded);
            }

            current = next;
            if (!expandedAny)
                break;
        }

        return current;
    }

    private static async Task<List<RarProcessor.StoredFileSegment>?> TryExpandGroupAsync(
        List<RarProcessor.StoredFileSegment> members,
        Func<RarProcessor.StoredFileSegment[], CancellationToken, Task<Stream>> openComposedStream,
        string? password,
        CancellationToken ct)
    {
        var path = members[0].PathWithinArchive;
        if (members.Any(member => member.AesParams is not null))
        {
            Log.Information(
                "NestedRarExpansion: skipping encrypted nested archive {Path}",
                path);
            return null;
        }

        RarProcessor.StoredFileSegment[] sorted;
        try
        {
            RarAggregator.ValidateVolumes(members);
            sorted = RarAggregator.SortByPartNumber(members);
        }
        catch (Exception e)
        {
            Log.Information(e,
                "NestedRarExpansion: outer volume set for {Path} is incomplete; keeping opaque",
                path);
            return null;
        }

        var outerSize = sorted.Sum(segment => segment.ByteRangeWithinPart.Count);
        try
        {
            await using var stream = await openComposedStream(sorted, ct).ConfigureAwait(false);
            var headers = await RarUtil.GetRarHeadersAsync(stream, password, ct).ConfigureAwait(false);
            var fileHeaders = headers
                .OfType<IRarFileHeader>()
                .Where(header => !header.IsDirectory)
                .ToList();

            if (fileHeaders.Count == 0)
            {
                Log.Information(
                    "NestedRarExpansion: no files in nested archive {Path}; keeping opaque",
                    path);
                return null;
            }

            if (fileHeaders.Count > MaxInnerFilesPerArchive)
            {
                Log.Information(
                    "NestedRarExpansion: nested archive {Path} has {Count} files (limit {Limit}); keeping opaque",
                    path, fileHeaders.Count, MaxInnerFilesPerArchive);
                return null;
            }

            if (fileHeaders.Any(header => !header.IsStored || header.IsSolid))
            {
                Log.Information(
                    "NestedRarExpansion: nested archive {Path} uses compression/solid; keeping opaque",
                    path);
                return null;
            }

            var declaredUncompressed = fileHeaders.Sum(header => header.UncompressedSize);
            if (declaredUncompressed > outerSize * 2)
            {
                Log.Information(
                    "NestedRarExpansion: nested archive {Path} declares {Declared} bytes (outer {Outer}); keeping opaque",
                    path, declaredUncompressed, outerSize);
                return null;
            }

            var archiveName = GetArchiveName(Path.GetFileName(path));
            var partNumber = new RarProcessor.PartNumber
            {
                PartNumberFromHeader = GetPartNumberFromHeaders(headers),
                PartNumberFromFilename = GetPartNumberFromFilename(Path.GetFileName(path)),
            };
            var releaseDate = sorted[0].ReleaseDate;
            var expanded = new List<RarProcessor.StoredFileSegment>();

            foreach (var header in fileHeaders)
            {
                var innerPath = NormalizeInnerPath(header.FileName);
                if (string.IsNullOrWhiteSpace(innerPath) ||
                    innerPath.Split('/', '\\').Any(segment => segment is ".." or "."))
                {
                    Log.Information(
                        "NestedRarExpansion: rejecting unsafe inner path {Inner} in {Path}",
                        header.FileName, path);
                    return null;
                }

                var range = LongRange.FromStartAndSize(
                    header.DataStartPosition,
                    header.AdditionalDataSize);
                var mapped = NestedRarRangeMapper.Map(
                    range,
                    sorted,
                    innerPath,
                    archiveName,
                    partNumber,
                    header.GetAesParams(password),
                    header.UncompressedSize,
                    releaseDate,
                    header.IsUncompressedSizeUnknown);
                expanded.AddRange(mapped);
            }

            if (expanded.Count == 0)
                return null;

            Log.Information(
                "NestedRarExpansion: expanded {Path} into {Count} inner file segment(s)",
                path, expanded.Count);
            return expanded;
        }
#pragma warning disable CA2016 // CA2016: classify cancellation regardless of the ambient token -- forwarding it would misclassify cancellations from internal timeout/child tokens
        catch (Exception e) when (!e.IsCancellationException())
#pragma warning restore CA2016
        {
            Log.Information(e,
                "NestedRarExpansion: failed to expand {Path}; keeping opaque",
                path);
            return null;
        }
    }

    private static async Task<Stream> OpenComposedStreamAsync(
        RarProcessor.StoredFileSegment[] sorted,
        INntpClient usenetClient,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var fileParts = sorted.Select(segment => new DavMultipartFile.FilePart
        {
            SegmentIds = segment.NzbFile.GetSegmentIds(),
            SegmentIdByteRange = LongRange.FromStartAndSize(0, segment.PartSize),
            FilePartByteRange = segment.ByteRangeWithinPart,
            SegmentByteRanges = segment.NzbFile.GetSegmentByteRanges(),
            SegmentFallbackIds = segment.NzbFile.GetSegmentFallbackIds(),
        }).ToArray();

        var multipart = new DavMultipartFile
        {
            Id = Guid.NewGuid(),
            Metadata = new DavMultipartFile.Meta
            {
                FileParts = fileParts,
            },
        };

        return new DavMultipartFileStream(
            multipart,
            usenetClient,
            articleBufferSize: 0,
            resolver: null,
            usePipelinedBodyRequests: false);
    }

    private static string GetArchiveName(string fileName)
    {
        var sansExtension = Path.GetFileNameWithoutExtension(fileName);
        return Regex.Replace(sansExtension, @"\.part\d+$", "", RegexOptions.IgnoreCase);
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
        var partMatch = Regex.Match(filename, @"\.part(\d+)\.rar$", RegexOptions.IgnoreCase);
        if (partMatch.Success)
            return int.Parse(partMatch.Groups[1].Value);

        var rMatch = Regex.Match(filename, @"\.r(\d+)$", RegexOptions.IgnoreCase);
        if (rMatch.Success)
            return int.Parse(rMatch.Groups[1].Value);

        if (filename.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
            return -1;

        return null;
    }

    private static string NormalizeInnerPath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }
}
