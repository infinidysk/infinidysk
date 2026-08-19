using Serilog;

namespace NzbWebDAV.Services.Repair;

/// <summary>
/// Fire-and-forget entry point for streaming zero-fill events. Resolves the DavItem by path
/// and enqueues background PAR2 repair without blocking readers.
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
        _ = Task.Run(async () =>
        {
            try
            {
                await _service.EnqueueZeroFillAsync(path, segmentId, segmentIndex, fillBytes)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.Debug(e, "PAR2 zero-fill trigger failed for {Path}", path);
            }
        });
    }
}
