namespace NzbWebDAV.Services.StreamTrace;

public enum StreamTraceKind
{
    RangeOpen = 0,
    Seek = 1,
    Segment = 2,
    ZeroFill = 3,
    Failover = 4,
    RangeEnd = 5,
    Retry = 6,
    PrefetchWidth = 7,
    StreamStartup = 8,
    BatchPlan = 9,
    RequestEnd = 10,
    HedgeIssued = 11,
    HedgeResolved = 12,
    HeadWait = 13,
    VolumeBoundary = 14,
    VolumePrepare = 15,
    HeadWaitSummary = 16,
    PipelineSample = 17,
    SharedAttach = 18,
    PumpSample = 19,
}
