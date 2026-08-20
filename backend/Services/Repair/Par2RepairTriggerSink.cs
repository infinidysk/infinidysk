using System.Collections.Concurrent;

namespace NzbWebDAV.Services.Repair;

/// <summary>
/// Entry point for streaming zero-fill events. Forwards synchronously to the
/// repair service, which gates cheaply (config + in-memory path dedup) and
/// hands the event to its single background consumer. No per-event tasks or
/// DB work happen on the caller's (playback) thread.
/// </summary>
public sealed class Par2RepairTriggerSink
{
    private readonly Par2RepairService _service;

    public Par2RepairTriggerSink(Par2RepairService service)
    {
        _service = service;
    }

    public static Par2RepairTriggerSink? Current { get; set; }

    /// <summary>
    /// Optional test observer. Production leaves this null so reports stay allocation-free
    /// on the playback path. Tests assign a bag and filter by unique segment IDs.
    /// </summary>
    internal static ConcurrentBag<(string Path, string SegmentId, bool IsCorruption)>? TestReports;

    public void ReportZeroFill(string path, string segmentId, int segmentIndex, long fillBytes)
    {
        TestReports?.Add((path, segmentId, false));
        _service.ReportZeroFill(path, segmentId);
    }

    /// <summary>
    /// Reports a streaming-confirmed corrupt article. Persistence and PAR2 enqueue
    /// are wired by the health-recording follow-up; playback reports here so tests
    /// and later consumers can observe the event without blocking the read.
    /// Static so the playback path can report even when <see cref="Current"/> is unset.
    /// </summary>
    public static void ReportCorruption(string path, string segmentId)
    {
        TestReports?.Add((path, segmentId, true));
        Current?.OnCorruptionReported(path, segmentId);
    }

    private void OnCorruptionReported(string path, string segmentId)
    {
        _service.ReportCorruption(path, segmentId);
    }
}
