using NzbWebDAV.Services.Diagnostics;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Api.Controllers.GcDiagnostics;

public sealed class GcDiagnosticsResponse : BaseApiResponse
{
    public required DateTimeOffset RunAtUtc { get; init; }
    public required GcSnapshot Before { get; init; }
    public required GcSnapshot After { get; init; }
    public required long PauseMs { get; init; }
    public required GcBufferRetention Retention { get; init; }
    public SegmentBufferPoolSnapshot? SegmentBufferPool { get; init; }
    public required string Warning { get; init; }

    internal static GcDiagnosticsResponse FromResult(GcDiagnosticsResult result) => new()
    {
        RunAtUtc = result.RunAtUtc,
        Before = result.Before,
        After = result.After,
        PauseMs = result.PauseMs,
        Retention = result.Retention,
        SegmentBufferPool = result.SegmentBufferPool,
        Warning = "Forced a blocking compacting GC; managed threads were paused. Do not poll this endpoint.",
    };
}
