namespace NzbWebDAV.Services.StreamTrace;

/// <summary>
/// Snapshot of the process-local stream-tracing state. Tracing is ephemeral:
/// UI enablement never persists past a restart, and a TTL auto-disables it.
/// </summary>
public sealed record StreamTraceStatus(
    bool Enabled,
    string Source,
    long ExpiresAtUnixMs,
    int Capacity,
    long EventCount,
    int SessionCount);
