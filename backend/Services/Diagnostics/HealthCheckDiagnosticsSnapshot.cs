namespace NzbWebDAV.Services.Diagnostics;

public sealed record HealthCheckDiagnosticsSnapshot(
    DateTimeOffset CapturedAtUtc,
    int ConfiguredWorkers,
    long StartedAttempts,
    long FinishedAttempts,
    int RecentCapacity,
    IReadOnlyList<HealthCheckAttemptSnapshot> Active,
    IReadOnlyList<HealthCheckAttemptSnapshot> Recent);

public sealed record HealthCheckAttemptSnapshot(
    Guid DavItemId,
    string? FileName,
    string? Path,
    string Phase,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset PhaseStartedAtUtc,
    DateTimeOffset? LastProgressAtUtc,
    int? ProgressPercent,
    DateTimeOffset? FinishedAtUtc,
    string? Outcome,
    double ElapsedSeconds,
    double PhaseElapsedSeconds,
    double? SecondsSinceProgress);