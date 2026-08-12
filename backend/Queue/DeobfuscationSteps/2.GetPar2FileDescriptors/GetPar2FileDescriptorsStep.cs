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
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        // Find the par2 index files. Most NZBs carry a single par2 set, but
        // some (e.g. per-episode season packs) carry one par2 set per content
        // file, each with its own index holding FileDesc packets we need.
        // Recovery volumes duplicate the index descriptors, so they are only
        // used as a fallback when no index file can be identified.
        // Sort by segment count then first message-id so dedup/fallback
        // selection is deterministic regardless of network completion
        // order. Smallest files first preserves the original preference
        // for index files over larger recovery volumes in the fallback.
        var par2Candidates = files
            .Where(x => !x.MissingFirstSegment)
            .Where(x => Par2.HasPar2MagicBytes(x.First16KB!))
            .OrderBy(x => x.NzbFile.Segments.Count)
            .ThenBy(x => x.NzbFile.Segments[0].MessageId, StringComparer.Ordinal)
            .ToList();
        var par2Indexes = par2Candidates
            .Where(x => !Par2.ParVolume.IsMatch(x.NzbFile.GetSubjectFileName()))
            .ToList();
        if (par2Indexes.Count == 0
            && par2Candidates.Count > 0)
        {
            par2Indexes.Add(par2Candidates[0]);
        }

        // return all file descriptors, deduplicated by FileID
        var fileDescriptors = new List<FileDesc>();
        var seenFileIds = new HashSet<string>(StringComparer.Ordinal);
        // Report a 0-100 percentage of index files processed so callers can
        // scale it into their band without count/percentage mismatch.
        var total = Math.Max(1, par2Indexes.Count);
        var completed = 0;
        foreach (var par2Index in par2Indexes)
        {
            var segments = par2Index.NzbFile.GetSegmentIds();
            var filesize = par2Index.NzbFile.Segments.Count == 1
                ? par2Index.Header!.PartOffset + par2Index.Header!.PartSize
                : await usenetClient.GetFileSizeAsync(par2Index.NzbFile, cancellationToken).ConfigureAwait(false);
            await using var stream = usenetClient.GetFileStream(segments, filesize, articleBufferSize: 0);
            // stopAtRecoverySlice: volumes repeat the index packets before the
            // recovery data, so the first RecvSlic ends descriptor extraction.
            await foreach (var fileDescriptor in Par2.ReadFileDescriptions(stream, stopAtRecoverySlice: true, cancellationToken)
                .ConfigureAwait(false))
            {
                if (seenFileIds.Add(Convert.ToHexString(fileDescriptor.FileID)))
                    fileDescriptors.Add(fileDescriptor);
            }
            progress?.Report(++completed * 100 / total);
        }

        return fileDescriptors;
    }
}
