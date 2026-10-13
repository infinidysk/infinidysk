using System.Security.Cryptography;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;
using SharpCompress.Common.Rar.Headers;

namespace NzbWebDAV.Queue.PostProcessors;

internal static class ArchiveMediaReadinessProbe
{
    // Probe the final member bytes, not RAR/7z headers. Do not open playback streams:
    // their prefetch and lazy-resolution persistence are inappropriate before commit.
    internal static async Task<byte[]> ReadHeadAsync(
        INntpClient client, DavMultipartFile.Meta meta, string name, long fileSize, CancellationToken ct)
    {
        if (fileSize <= 0)
            throw new NonRetryableDownloadException("Import readiness check found an empty media file.");
        var decodedLength = (int)Math.Min(VideoSignatureUtil.First16KBLength, fileSize);
        var packedLength = meta.AesParams is null ? decodedLength : (decodedLength + 15) / 16 * 16;
        var buffer = new byte[packedLength];
        var written = 0;
        var parts = new Queue<DavMultipartFile.FilePart>(meta.FileParts);
        var pending = new Queue<DavMultipartFile.PendingPart>(meta.PendingParts);
        DavMultipartFile.FilePart? previous = null;
        while (written < buffer.Length)
        {
            ct.ThrowIfCancellationRequested();
            if (parts.Count == 0)
            {
                if (pending.Count == 0 || previous?.IsSplitAfter is false)
                    throw new NonRetryableDownloadException("Archive media ended before its opening bytes could be read.");
                parts.Enqueue(await ResolveContinuationAsync(client, meta, pending.Dequeue(), name, ct)
                    .ConfigureAwait(false));
            }

            var part = parts.Dequeue();
            previous = part;
            var count = (int)Math.Min(buffer.Length - written, part.FilePartByteRange.Count);
            if (count <= 0)
                throw new NonRetryableDownloadException("Archive media has an empty leading part.");
            var dataStart = part.FilePartByteRange.StartInclusive;
            var drainHeader = dataStart <= VideoSignatureUtil.First16KBLength;
            await using var stream = client.GetFileStream(
                part.SegmentIds,
                Math.Max(part.SegmentIdByteRange.Count, part.FilePartByteRange.EndExclusive),
                articleBufferSize: 0, segmentByteRanges: part.SegmentByteRanges,
                usePipelinedBodyRequests: false, fileName: $"import-readiness {name}",
                segmentFallbacks: part.SegmentFallbackIds, streamingBodyBatchWidth: 1,
                segmentByteRangesTrusted: part.SegmentByteRangesTrusted == true,
                readBudgetOverride: count + (drainHeader ? dataStart : 0),
                verificationProof: part.VerificationProof);
            // Starting ordinary RAR volumes at zero preserves first-article fail-fast
            // instead of turning a definitive miss into playback gap-fill bytes.
            // Large member offsets still seek, rather than downloading their prefix.
            if (drainHeader) await stream.DiscardExactBytesAsync(dataStart, ct).ConfigureAwait(false);
            else stream.Position = dataStart;
            var end = written + count;
            while (written < end)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(written, end - written), ct).ConfigureAwait(false);
                if (read == 0)
                    throw new NonRetryableDownloadException("Archive media has unreadable opening bytes.");
                written += read;
            }
            await stream.ValidateDeliveredAsync(ct).ConfigureAwait(false);
        }

        if (meta.AesParams is not { } parameters) return buffer;
        using var aes = Aes.Create();
        aes.Key = parameters.Key;
        var decoded = aes.DecryptCbc(buffer, parameters.Iv, PaddingMode.None);
        return decoded[..decodedLength];
    }

    // An unusually small first volume can split the media header. Resolve only the
    // continuation needed for the bounded prefix, without rewriting import metadata.
    private static async Task<DavMultipartFile.FilePart> ResolveContinuationAsync(
        INntpClient client, DavMultipartFile.Meta meta, DavMultipartFile.PendingPart pending,
        string name, CancellationToken ct)
    {
        await using var stream = client.GetFileStream(
            pending.SegmentIds, pending.SegmentIdByteRange.Count, articleBufferSize: 0,
            usePipelinedBodyRequests: false, fileName: $"import-readiness {name}",
            segmentFallbacks: pending.SegmentFallbackIds, streamingBodyBatchWidth: 1,
            verificationProof: pending.VerificationProof);
        var headers = await RarUtil.ReadHeadersUntilFirstFileAsync(stream, meta.ArchivePassword, ct)
            .ConfigureAwait(false);
        var header = headers.OfType<IRarFileHeader>().LastOrDefault(h => !h.IsDirectory);
        if (header is null || !header.IsSplitBefore || header.IsSolid
            || header.IsEncrypted != (meta.AesParams is not null)
            || !string.Equals(header.FileName, meta.PathInArchive, StringComparison.Ordinal))
            throw new NonRetryableDownloadException("Archive media has an invalid continuation before its header ends.");
        await stream.ValidateDeliveredAsync(ct).ConfigureAwait(false);
        return new DavMultipartFile.FilePart
        {
            SegmentIds = pending.SegmentIds,
            SegmentFallbackIds = pending.SegmentFallbackIds,
            VerificationProof = pending.VerificationProof,
            SegmentIdByteRange = LongRange.FromStartAndSize(0,
                Math.Max(pending.SegmentIdByteRange.Count, header.DataStartPosition + header.AdditionalDataSize)),
            FilePartByteRange = LongRange.FromStartAndSize(header.DataStartPosition, header.AdditionalDataSize),
            IsSplitAfter = header.IsSplitAfter,
        };
    }
}
