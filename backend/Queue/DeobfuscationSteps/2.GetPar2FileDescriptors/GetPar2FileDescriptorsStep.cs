using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Extensions;
using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Par2Recovery.Packets;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;

namespace NzbWebDAV.Queue.DeobfuscationSteps._2.GetPar2FileDescriptors;

public static class GetPar2FileDescriptorsStep
{
    public static async Task<List<FileDesc>> GetPar2FileDescriptors
    (
        List<FetchFirstSegmentsStep.NzbFileWithFirstSegment> files,
        INntpClient usenetClient,
        CancellationToken cancellationToken = default
    )
    {
        // Find the par2 index files. Most NZBs carry a single par2 set, but
        // some (e.g. per-episode season packs) carry one par2 set per content
        // file, each with its own index holding FileDesc packets we need.
        // Recovery volumes duplicate the index descriptors, so they are only
        // used as a fallback when no index file can be identified.
        var par2Candidates = files
            .Where(x => !x.MissingFirstSegment)
            .Where(x => Par2.HasPar2MagicBytes(x.First16KB!))
            .ToList();
        var par2Indexes = par2Candidates
            .Where(x => !Par2.ParVolume.IsMatch(x.NzbFile.GetSubjectFileName()))
            .ToList();
        if (par2Indexes.Count == 0
            && par2Candidates.MinBy(x => x.NzbFile.Segments.Count) is { } fallback)
        {
            par2Indexes.Add(fallback);
        }

        // return all file descriptors, deduplicated by FileID
        var fileDescriptors = new List<FileDesc>();
        var seenFileIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var par2Index in par2Indexes)
        {
            var segments = par2Index.NzbFile.GetSegmentIds();
            var filesize = par2Index.NzbFile.Segments.Count == 1
                ? par2Index.Header!.PartOffset + par2Index.Header!.PartSize
                : await usenetClient.GetFileSizeAsync(par2Index.NzbFile, cancellationToken).ConfigureAwait(false);
            await using var stream = usenetClient.GetFileStream(segments, filesize, articleBufferSize: 0);
            await foreach (var fileDescriptor in Par2.ReadFileDescriptions(stream, cancellationToken).ConfigureAwait(false))
            {
                if (seenFileIds.Add(Convert.ToHexString(fileDescriptor.FileID)))
                    fileDescriptors.Add(fileDescriptor);
            }
        }

        return fileDescriptors;
    }
}
