using System.Buffers.Binary;
using System.Text;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Extensions;
using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Par2Recovery.Packets;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;
using Serilog;

namespace NzbWebDAV.Queue.DeobfuscationSteps._2.GetPar2FileDescriptors;

public static class GetPar2FileDescriptorsStep
{
    private const int MaxArticleBytes = 2 * 1024 * 1024;
    private const int MaxPrefixBytes = 32 * 1024 * 1024;
    private const int MaxArticles = 64;

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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var remainingBytes = MaxPrefixBytes;
        var remainingArticles = MaxArticles;
        var fallbackCandidates = par2Candidates.Except(par2Indexes).ToList();
        foreach (var par2Index in par2Indexes.Concat(fallbackCandidates))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fallbackCandidates.Contains(par2Index) && fileDescriptors.Count > 0)
                break;
            try
            {
                using var prefix = new MemoryStream();
                var packetEnd = 0;
                var candidateDescriptors = new List<FileDesc>();
                var complete = false;
                foreach (var segment in par2Index.NzbFile.Segments)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (remainingArticles-- <= 0 || remainingBytes <= 0)
                        throw new InvalidDataException("PAR2 metadata download budget exhausted.");

                    var response = await usenetClient.DecodedBodyAsync(segment.MessageId, deadline.Token)
                        .ConfigureAwait(false);
                    await using (var article = response.Stream
                                               ?? throw new InvalidDataException("PAR2 article body is missing."))
                    {
                        var buffer = new byte[64 * 1024];
                        var articleBytes = 0;
                        while (true)
                        {
                            var read = await article.ReadAsync(buffer.AsMemory(0,
                                    Math.Min(buffer.Length, Math.Min(MaxArticleBytes - articleBytes, remainingBytes) + 1)),
                                deadline.Token).ConfigureAwait(false);
                            if (read == 0)
                                break;
                            articleBytes += read;
                            remainingBytes -= read;
                            if (articleBytes > MaxArticleBytes || remainingBytes < 0)
                                throw new InvalidDataException("PAR2 decoded article or metadata prefix exceeds its download limit.");
                            await prefix.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                        }
                    }

                    var recovery = FindCompletePacketEnd(prefix, ref packetEnd);
                    candidateDescriptors.Clear();
                    using (var metadata = new MemoryStream(prefix.GetBuffer(), 0, packetEnd, writable: false))
                    {
                        await foreach (var descriptor in Par2.ReadVerifiedFileDescriptions(metadata, ct: deadline.Token)
                                           .ConfigureAwait(false))
                            candidateDescriptors.Add(descriptor);
                    }
                    if (recovery || (ReferenceEquals(segment, par2Index.NzbFile.Segments[^1])
                        && packetEnd == prefix.Length && candidateDescriptors.Count > 0))
                    {
                        complete = true;
                        break;
                    }
                }
                if (!complete)
                    throw new InvalidDataException("PAR2 metadata is missing or truncated.");
                foreach (var descriptor in candidateDescriptors)
                {
                    if (seenFileIds.Add(Convert.ToHexString(descriptor.FileID)))
                        fileDescriptors.Add(descriptor);
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested
                                               && exception is not OutOfMemoryException)
            {
                Log.Warning("Skipping PAR2 metadata for {FileName}. Reason: {Reason}",
                    par2Index.NzbFile.GetSubjectFileName(),
                    deadline.IsCancellationRequested ? "Metadata download deadline exceeded." : exception.Message);
            }
            if (completed < total)
                progress?.Report(++completed * 100 / total);
            if (deadline.IsCancellationRequested || remainingArticles <= 0 || remainingBytes <= 0)
                break;
        }

        return fileDescriptors;
    }

    private static bool FindCompletePacketEnd(MemoryStream prefix, ref int packetEnd)
    {
        while (prefix.Length - packetEnd >= 64)
        {
            var header = prefix.GetBuffer().AsSpan(packetEnd, 64);
            if (!header[..8].SequenceEqual("PAR2\0PKT"u8))
                throw new InvalidDataException("Invalid PAR2 magic constant.");
            var length = BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
            if (length < 64 || length > int.MaxValue || length % 4 != 0)
                throw new InvalidDataException("Invalid PAR2 packet length.");
            if (Encoding.ASCII.GetString(header[48..]).TrimEnd('\0') == RecvSlic.PacketType)
            {
                if (length < 68 || length - 68 > MainPacket.MaxSliceSize)
                    throw new InvalidDataException("Invalid PAR2 recovery packet length.");
                return true;
            }
            if (length > (ulong)(MaxPrefixBytes - packetEnd))
                throw new InvalidDataException("PAR2 metadata packet exceeds the prefix limit.");
            if (length > (ulong)(prefix.Length - packetEnd))
                break;
            packetEnd += (int)length;
        }
        return false;
    }
}
