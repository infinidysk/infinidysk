using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Exceptions;
using Serilog;
using UsenetSharp.Models;

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
}
