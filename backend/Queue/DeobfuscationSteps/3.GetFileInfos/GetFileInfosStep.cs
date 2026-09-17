using System.Security.Cryptography;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Par2Recovery.Packets;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;

public static class GetFileInfosStep
{
    public static List<FileInfo> GetFileInfos
    (
        List<FetchFirstSegmentsStep.NzbFileWithFirstSegment> files,
        List<FileDesc> par2FileDescriptors
    )
    {
#pragma warning disable CA5351 // MD5 here is content hashing for the NZB/PAR2 ecosystem (dedup/integrity per format conventions), not security
        using var md5 = MD5.Create();
#pragma warning restore CA5351
        var hashToFileDescMap = GetHashToFileDescMap(par2FileDescriptors);
        var picks = files.Select(x =>
        {
            var fileDesc = GetMatchingFileDescriptor(x, hashToFileDescMap, md5);
            if (fileDesc?.VerificationProof is { } proof && proof.IsValidFor((long)fileDesc.FileLength)
                && x.Header is { } header
                && ((header.TotalParts > 0 && header.TotalParts != x.NzbFile.Segments.Count)
                    || (header.FileSize > 0 && header.FileSize != proof.FileLength)))
                x.NzbFile.VerificationProof = proof;
            return new NamePick
            {
                Info = GetFileInfo(x, fileDesc),
                HeaderName = x.Header?.FileName ?? "",
                // Usable PAR2 name only — null descriptor or empty FileName does not count.
                HasPar2Name = !string.IsNullOrWhiteSpace(fileDesc?.FileName),
            };
        }).ToList();

        RepairRarGroupNames(picks);
        return picks.Select(p => p.Info).ToList();
    }

    private static Dictionary<string, LinkedList<FileDesc>> GetHashToFileDescMap(List<FileDesc> par2FileDescriptors)
    {
        var hashToFileDescMap = new Dictionary<string, LinkedList<FileDesc>>();
        foreach (var descriptor in par2FileDescriptors)
        {
            var hash = BitConverter.ToString(descriptor.File16kHash);
            if (!hashToFileDescMap.TryGetValue(hash, out var list))
            {
                list = new LinkedList<FileDesc>();
                hashToFileDescMap[hash] = list;
            }
            list.AddLast(descriptor);
        }

        return hashToFileDescMap;
    }

    private static FileInfo GetFileInfo(
        FetchFirstSegmentsStep.NzbFileWithFirstSegment file,
        FileDesc? fileDesc
    )
    {
        var subjectFileName = file.NzbFile.GetSubjectFileName();
        var headerFileName = file.Header?.FileName ?? "";
        var par2FileName = fileDesc?.FileName ?? "";
        var filename = new List<(string? FileName, int Priority)>
        {
            (FileName: par2FileName, Priority: GetFilenamePriority(par2FileName, 3)),
            (FileName: subjectFileName, Priority: GetFilenamePriority(subjectFileName, 2)),
            (FileName: headerFileName, Priority: GetFilenamePriority(headerFileName, 1)),
        }.Where(x => x.FileName is not null).MaxBy(x => x.Priority).FileName ?? "";

        var isRar = file.HasRar4Magic() || file.HasRar5Magic();
        string? sniffedVideoExtension = null;
        if (!file.MissingFirstSegment
            && file.First16KB is not null
            && !isRar
            && !FilenameUtil.Is7zFile(filename))
        {
            sniffedVideoExtension = VideoSignatureUtil.GuessVideoExtension(file.First16KB);
        }

        return new FileInfo()
        {
            NzbFile = file.NzbFile,
            FileName = filename,
            ReleaseDate = file.ReleaseDate,
            FileSize = (long?)fileDesc?.FileLength,
            IsRar = isRar,
            SniffedVideoExtension = sniffedVideoExtension,
            First16KB = file.First16KB,
            MissingEvidenceGeneration = file.MissingEvidenceGeneration,
        };
    }

    private static int GetFilenamePriority(string? filename, int startingPriority)
    {
        var priority = startingPriority;
        if (string.IsNullOrWhiteSpace(filename)) return priority - 5000;
        if (ObfuscationUtil.IsProbablyObfuscated(filename)) priority -= 1000;
        if (FilenameUtil.IsImportantFileType(filename)) priority += 50;
        if (Path.GetExtension(filename).TrimStart('.').Length is >= 2 and <= 4) priority += 10;
        return priority;
    }

    private static FileDesc? GetMatchingFileDescriptor
    (
        FetchFirstSegmentsStep.NzbFileWithFirstSegment file,
        Dictionary<string, LinkedList<FileDesc>> hashToFiledescMap,
        MD5 md5
    )
    {
        var hash = !file.MissingFirstSegment ? BitConverter.ToString(md5.ComputeHash(file.First16KB!)) : "";
        if (!hashToFiledescMap.TryGetValue(hash, out var fileDescs)) return null;
        var fileDesc = fileDescs.First!.Value;
        if (fileDescs.Count > 1) fileDescs.RemoveFirst();
        return IsCloseToYencodedSize((long)fileDesc.FileLength, file.NzbFile.GetTotalYencodedSize())
            ? fileDesc
            : null;
    }

    private static bool IsCloseToYencodedSize(long fileSize, long totalYencodedSize)
    {
        var range = new LongRange(95 * totalYencodedSize / 100, totalYencodedSize);
        return range.Contains(fileSize);
    }

    /// <summary>
    /// When RAR volumes share colliding subject-derived names (no distinct volume
    /// identity) but yEnc headers restore distinct identities, prefer the header names.
    /// Identity is (case-insensitive base, scheme, normalized ordinal) — the same key
    /// ArchiveSetGrouping uses — so independent sets in one NZB may repeat ordinals.
    /// Skipped when any volume already has a PAR2 name.
    /// </summary>
    internal static void RepairRarGroupNames(List<NamePick> picks)
    {
        // Magic-based group: every real RAR volume carries the signature, so this
        // does not depend on the (possibly colliding) FileName.
        var group = picks.Where(x => x.Info.IsRar).ToList();
        if (group.Count < 2) return;
        // Keep PAR2 authoritative; never mix PAR2 + header repairs in one group.
        if (group.Any(x => x.HasPar2Name)) return;
        if (HasDistinctRarVolumeIdentities(group.Select(x => x.Info.FileName))) return;
        if (!HasDistinctRarVolumeIdentities(group.Select(x => x.HeaderName))) return;

        Log.Information(
            "Repairing {Count} RAR volume names without distinct volume identity using yEnc header names",
            group.Count);
        foreach (var pick in group)
            pick.Info = pick.Info with { FileName = pick.HeaderName };
    }

    internal static bool HasDistinctRarVolumeIdentities(IEnumerable<string> names)
    {
        var identities = new HashSet<(string BaseName, FilenameUtil.RarVolumeScheme Scheme, int Ordinal)>();
        var count = 0;
        foreach (var name in names)
        {
            count++;
            if (FilenameUtil.GetRarVolumeName(name) is not { } volume) return false;
            if (!identities.Add((volume.BaseName.ToLowerInvariant(), volume.Scheme, volume.Ordinal))) return false;
        }

        return count > 0;
    }

    internal sealed class NamePick
    {
        public required FileInfo Info { get; set; }
        public required string HeaderName { get; init; }
        public required bool HasPar2Name { get; init; }
    }

    public record FileInfo
    {
        public required NzbFile NzbFile { get; init; }
        public required string FileName { get; init; }
        public required DateTimeOffset ReleaseDate { get; init; }
        public long? FileSize { get; init; }
        public bool IsRar { get; init; }
        public string? SniffedVideoExtension { get; init; }
        public byte[]? First16KB { get; init; }
        public long? MissingEvidenceGeneration { get; init; }
    }
}
