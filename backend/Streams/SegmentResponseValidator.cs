using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Exceptions;
using Serilog;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

/// <summary>
/// Guards against a BODY response being paired with the wrong request. Responses arrive
/// in request order, so a mismatch means this offset of the file would silently carry
/// another segment's bytes — indistinguishable from corruption once it reaches a player.
/// </summary>
internal static class SegmentResponseValidator
{
    public static async Task ThrowOnSegmentIdMismatchAsync(
        string segmentId,
        UsenetDecodedBodyResponse response)
    {
        if (!NntpClient.HasSegmentIdMismatch(
                segmentId, response.SegmentId, response.ResponseMessage, out var actualId))
            return;

        if (response.Stream is not null)
        {
            try
            {
                await response.Stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.Debug(e, "Failed to dispose mismatched BODY stream");
            }
        }

        throw new UsenetUnexpectedResponseException(
            segmentId,
            $"Response carried segment {actualId} instead of {segmentId}.");
    }

    public static async ValueTask<bool> IsFallbackPartSizeCompatibleAsync(
        Stream bodyStream, SegmentSizes segmentSizes, int segmentIndex, CancellationToken ct)
    {
        if (!segmentSizes.TryGetExactSize(segmentIndex, out var exact)) return true;
        if (bodyStream is not YencStream yenc) return true;

        try
        {
            var header = await yenc.GetYencHeadersAsync(ct).ConfigureAwait(false);
            return header is null || header.PartSize <= 0 || header.PartSize == exact;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return true;
        }
    }
}
