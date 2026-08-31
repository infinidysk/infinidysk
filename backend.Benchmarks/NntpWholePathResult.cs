namespace NzbWebDAV.Benchmarks;

internal sealed record NntpWholePathResult(
    NntpWholePathScenario Scenario,
    NntpWholePathDeterministic Deterministic,
    NntpWholePathTiming Timing);

internal sealed record NntpWholePathDeterministic(
    long ExpectedBytes,
    long ActualBytes,
    long Sha256Match,
    long BodyCommands,
    long Responses,
    long RetrievedCallbacks,
    long CancelledCallbacks,
    long NotFoundCallbacks,
    long NotRetrievedCallbacks,
    long FinalArticleBudgetBytes,
    long FinalPipeBufferedBytes,
    long OutstandingPermits,
    long PeakActiveConnections);

internal sealed record NntpWholePathTiming(
    double WallSeconds,
    double ClientCpuSeconds,
    double ServerCpuSeconds,
    double ClientCpuSecondsPerGb,
    double ThroughputMbps,
    long ClientAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections);
