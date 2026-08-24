using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Services.StreamTrace;

namespace NzbWebDAV.Streams;

/// <summary>
/// Shared reporting for a segment that decoded short of its recorded size: one
/// coalesced warning, one stream-trace event, one tracked playback hole, and one
/// PAR2 repair trigger, so the buffered and unbuffered streams cannot drift apart.
/// </summary>
internal static class SegmentHoleReporter
{
    public static UsenetArticleNotFoundException ReportShortDecode(
        string fileName,
        string segmentId,
        int segmentIndex,
        long shortfall)
    {
        var hole = new UsenetArticleNotFoundException(segmentId);
        ZeroFillLogLimiter.Write(
            "Segment {SegmentId} of {FileName} decoded {Bytes} bytes short of its recorded size. " +
            "Filling the gap to keep the rest of the file aligned.",
            segmentId,
            fileName,
            shortfall);
        if (MultiProviderNntpClient.CurrentReadSessionId is { } sessionId)
            StreamTrace.TryZeroFill(sessionId, segmentId, shortfall);

        PlaybackHoleTracker.RecordHole(fileName, segmentId, hole);
        Par2RepairTriggerSink.Current?.ReportZeroFill(fileName, segmentId, segmentIndex, shortfall);
        return hole;
    }
}
