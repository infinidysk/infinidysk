using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Services.StreamTrace;

namespace NzbWebDAV.Streams;

internal enum StreamStartupPhase
{
    ExactIndexDirect,
    LegacyBuffered,
    LegacyProbedUnbuffered,
    HandoffNotNeeded,
    HandoffEager,
    HandoffLegacyLazy,
    HandoffScheduled,
    HandoffActivated,
    RemainderFactoryFailed,
    PrefixDiscard,
    RemainderWait,
}

/// <summary>
/// Range-attributed, bounded startup-path evidence. The enum-to-code mapping is
/// the only string boundary so paths, message IDs, and exception text cannot
/// become startup phase labels.
/// </summary>
internal static class StreamStartupTrace
{
    internal static void TryRecord(
        StreamStartupPhase phase,
        long? bytes = null,
        TimeSpan? elapsed = null)
    {
        try
        {
            var range = MultiProviderNntpClient.CurrentStreamTraceRange;
            var sessionId = range?.SessionId ?? MultiProviderNntpClient.CurrentReadSessionId;
            if (sessionId is { } value)
            {
                StreamTrace.TryStreamStartup(
                    value,
                    range?.Generation,
                    ToCode(phase),
                    bytes,
                    elapsed);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Tracing must never affect playback control flow.
        }
    }

    internal static string ToCode(StreamStartupPhase phase) => phase switch
    {
        StreamStartupPhase.ExactIndexDirect => "exact-index-direct",
        StreamStartupPhase.LegacyBuffered => "legacy-buffered",
        StreamStartupPhase.LegacyProbedUnbuffered => "legacy-probed-unbuffered",
        StreamStartupPhase.HandoffNotNeeded => "handoff-not-needed",
        StreamStartupPhase.HandoffEager => "handoff-eager",
        StreamStartupPhase.HandoffLegacyLazy => "handoff-legacy-lazy",
        StreamStartupPhase.HandoffScheduled => "handoff-scheduled",
        StreamStartupPhase.HandoffActivated => "handoff-activated",
        StreamStartupPhase.RemainderFactoryFailed => "remainder-factory-failed",
        StreamStartupPhase.PrefixDiscard => "prefix-discard",
        StreamStartupPhase.RemainderWait => "remainder-wait",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown startup phase."),
    };
}
