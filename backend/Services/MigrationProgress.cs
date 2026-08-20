using System.Text.Json;
using System.Text.RegularExpressions;

namespace NzbWebDAV.Services;

/// <summary>
/// Thread-safe, in-memory snapshot of the blocking database-migration phase.
/// Written by the migration runner in <c>Program.cs</c> and read by the
/// <see cref="MigrationStatusServer"/> so the frontend can render live progress
/// while the real backend is not yet accepting requests.
/// </summary>
public sealed class MigrationProgress
{
    // Synthetic step ids for work that is not an EF migration.
    public const string MetricsStepId = "__metrics__";
    public const string VacuumStepId = "__vacuum__";

    // Migrations known to perform full-table rewrites/rebuilds. Flagged so the
    // UI can warn that they may take a long time on large databases.
    private static readonly HashSet<string> SlowMigrations = new()
    {
        "20250819221618_Add-Path-To-DavItem",
        "20250824100609_Add-IdPrefix-To-DavItems",
        "20260129182923_Update-DavItems-Type-And-SubType",
        "20260203071130_Add-DavCleanupItems-Table",
        "20260712000000_Fix-Empty-Categories",
        "20260713120000_Add-Path-Index-To-DavItems",
        "20260820160000_Normalize-Guid-Text-Casing",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly Regex MigrationPrefix = new(@"^\d{14}_", RegexOptions.Compiled);

    private readonly object _lock = new();
    private readonly long _startedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private readonly List<StepState> _steps = new();
    private string _state = "running";
    private string? _error;

    public sealed record MigrationStep(string Id, string Name, bool Slow);

    public void Initialize(IReadOnlyList<MigrationStep> steps)
    {
        lock (_lock)
        {
            _steps.Clear();
            foreach (var step in steps)
                _steps.Add(new StepState(step.Id, step.Name, step.Slow));
        }
    }

    public void BeginStep(string id)
    {
        lock (_lock)
        {
            var step = _steps.FirstOrDefault(x => x.Id == id);
            if (step is null) return;
            step.Status = "running";
            step.StartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }

    public void CompleteStep(string id)
    {
        lock (_lock)
        {
            var step = _steps.FirstOrDefault(x => x.Id == id);
            if (step is null) return;
            step.Status = "completed";
            step.FinishedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }

    public void Complete()
    {
        lock (_lock) _state = "completed";
    }

    /// <summary>Marks the currently running step and the overall run as failed.</summary>
    public void Fail(string error)
    {
        lock (_lock)
        {
            _state = "failed";
            _error = error;
            var running = _steps.FirstOrDefault(x => x.Status == "running");
            if (running is not null)
            {
                running.Status = "failed";
                running.FinishedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
        }
    }

    public string ToJson()
    {
        lock (_lock)
        {
            var snapshot = new Snapshot(
                State: _state,
                StartedAt: _startedAtMs,
                Completed: _steps.Count(x => x.Status == "completed"),
                Total: _steps.Count,
                CurrentStep: _steps.FirstOrDefault(x => x.Status == "running")?.Name,
                Error: _error,
                Steps: _steps
                    .Select(x => new StepSnapshot(x.Id, x.Name, x.Status, x.Slow, x.StartedAtMs, x.FinishedAtMs))
                    .ToList());
            return JsonSerializer.Serialize(snapshot, JsonOptions);
        }
    }

    /// <summary>Turns "20250819221618_Add-Path-To-DavItem" into "Add Path To DavItem".</summary>
    public static string FriendlyName(string migrationId)
    {
        var withoutPrefix = MigrationPrefix.Replace(migrationId, string.Empty);
        return withoutPrefix.Replace('-', ' ').Replace('_', ' ').Trim();
    }

    public static bool IsSlow(string migrationId) => SlowMigrations.Contains(migrationId);

    /// <summary>
    /// True when there is nothing user-visible to report: no pending main or
    /// metrics EF migrations and vacuum is disabled. The migration runner skips
    /// the status server in this case.
    /// </summary>
    public static bool IsIdleMaintenance(
        int pendingMigrationCount,
        int pendingMetricsMigrationCount,
        bool vacuumEnabled) =>
        pendingMigrationCount == 0
        && pendingMetricsMigrationCount == 0
        && !vacuumEnabled;

    private sealed class StepState(string id, string name, bool slow)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public bool Slow { get; } = slow;
        public string Status { get; set; } = "pending";
        public long? StartedAtMs { get; set; }
        public long? FinishedAtMs { get; set; }
    }

    private sealed record Snapshot(
        string State,
        long StartedAt,
        int Completed,
        int Total,
        string? CurrentStep,
        string? Error,
        IReadOnlyList<StepSnapshot> Steps);

    private sealed record StepSnapshot(
        string Id,
        string Name,
        string Status,
        bool Slow,
        long? StartedAt,
        long? FinishedAt);
}
