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

    public void ReportZeroFill(string path, string segmentId, int segmentIndex, long fillBytes)
    {
        _service.ReportZeroFill(path, segmentId);
    }
}
