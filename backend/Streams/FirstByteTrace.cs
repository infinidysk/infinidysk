using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Services.StreamTrace;

namespace NzbWebDAV.Streams;

/// <summary>
/// Bounded first-byte path labels. New aggregate events must use these fixed strings
/// so traces never grow labels from paths, message IDs, or exception text.
/// </summary>
internal static class FirstByteTrace
{
    internal const string ExactIndexDirect = "exact-index-direct";
    internal const string LegacyBuffered = "legacy-buffered";
    internal const string LegacyProbedUnbuffered = "legacy-probed-unbuffered";
    internal const string HandoffNotNeeded = "handoff-not-needed";
    internal const string HandoffEager = "handoff-eager";
    internal const string HandoffLegacyLazy = "handoff-legacy-lazy";
    internal const string HandoffStarted = "handoff-started";
    internal const string HandoffActivated = "handoff-activated";
    internal const string RemainderFactoryFailed = "remainder-factory-failed";
    internal const string PrefixDiscard = "prefix-discard";
    internal const string RemainderWait = "remainder-wait";

    internal static void TryRecord(string status, long? bytes = null, TimeSpan? elapsed = null)
    {
        try
        {
            if (MultiProviderNntpClient.CurrentReadSessionId is { } sessionId)
                StreamTrace.TryFirstByte(sessionId, status, bytes, elapsed);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Tracing must never affect playback control flow.
        }
    }
}
