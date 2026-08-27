namespace NzbWebDAV.Services.Benchmark;

/// <summary>
/// How aggressive the benchmark is. The trade-off is accuracy vs. data used:
/// faster connections need more transferred bytes to measure reliably, so
/// "Thorough" simply moves more data per step.
/// </summary>
public enum BenchmarkIntensity
{
    Quick,
    Thorough,
}

/// <summary>
/// Tunable knobs for a single benchmark run, derived from the chosen intensity.
/// All byte values are decimal megabytes (1 MB = 1,000,000 bytes) so the numbers
/// line up with what download clients display.
/// </summary>
public sealed class BenchmarkProfile
{
    public required int LatencySamples { get; init; }
    public required int MaxCorpusSegments { get; init; }

    /// <summary>Floor for the adaptive per-level byte target (bootstrap before we have a speed estimate).</summary>
    public required long PerLevelBytes { get; init; }

    /// <summary>Hard wall-clock cap per level so a slow/stalled line can't hang the run.</summary>
    public required TimeSpan PerLevelMaxDuration { get; init; }

    /// <summary>Ramp time excluded from measurement (TCP slow-start, first-command RTT).</summary>
    public required TimeSpan WarmupDuration { get; init; }

    /// <summary>Steady-state window we aim to measure after warm-up.</summary>
    public required TimeSpan MeasureWindow { get; init; }

    /// <summary>Default backstop on total data moved; the request can override it.</summary>
    public required long HardTotalBytes { get; init; }

    public required int[] SweepLevels { get; init; }
    public required int PipelineTestConnections { get; init; }
    public required int[] PipelineDepths { get; init; }

    public static BenchmarkProfile For(BenchmarkIntensity intensity) => intensity switch
    {
        BenchmarkIntensity.Thorough => new BenchmarkProfile
        {
            LatencySamples = 8,
            MaxCorpusSegments = 8000,
            PerLevelBytes = 20_000_000,
            PerLevelMaxDuration = TimeSpan.FromSeconds(15),
            WarmupDuration = TimeSpan.FromSeconds(1.5),
            MeasureWindow = TimeSpan.FromSeconds(6),
            HardTotalBytes = 2_000_000_000,
            SweepLevels = [1, 2, 4, 6, 8, 12, 16, 20, 24, 32, 40, 50],
            PipelineTestConnections = 6,
            PipelineDepths = [4, 8, 16],
        },
        _ => new BenchmarkProfile
        {
            LatencySamples = 5,
            MaxCorpusSegments = 2500,
            PerLevelBytes = 8_000_000,
            PerLevelMaxDuration = TimeSpan.FromSeconds(10),
            WarmupDuration = TimeSpan.FromSeconds(1),
            MeasureWindow = TimeSpan.FromSeconds(3),
            HardTotalBytes = 500_000_000,
            SweepLevels = [1, 2, 4, 8, 16, 24],
            PipelineTestConnections = 4,
            PipelineDepths = [8, 16],
        },
    };
}

public sealed class BenchmarkLatency
{
    public double MinMs { get; set; }
    public double AvgMs { get; set; }
    public int Samples { get; set; }
}

public sealed class BenchmarkSweepPoint
{
    public int Connections { get; set; }
    public double MegaBytesPerSec { get; set; }

    /// <summary>Coefficient of variation of throughput across sampling buckets (0 = perfectly steady).</summary>
    public double Cv { get; set; }
}

public sealed class BenchmarkPipeliningPoint
{
    public int Depth { get; set; }
    public double MegaBytesPerSec { get; set; }
}

public sealed class BenchmarkPipelining
{
    public int TestedAtConnections { get; set; }
    public double BaselineMegaBytesPerSec { get; set; }
    public List<BenchmarkPipeliningPoint> Tested { get; set; } = [];
    public bool RecommendEnabled { get; set; }
    public int RecommendedDepth { get; set; }
}

/// <summary>
/// The full structured outcome of a benchmark run. Returned from the POST and
/// mirrored (in part) over the websocket progress topic while running.
/// </summary>
public sealed class BenchmarkResult
{
    public BenchmarkLatency? Latency { get; set; }
    public bool ThroughputTested { get; set; }

    /// <summary>True when the run only measured pipelining depth (connection count left untouched).</summary>
    public bool PipeliningOnly { get; set; }
    public List<BenchmarkSweepPoint> Sweep { get; set; } = [];

    /// <summary>
    /// Recommended BODY/ARTICLE transfer concurrency. The property name is retained
    /// for wire compatibility; applying it must not rewrite the provider-wide limit.
    /// </summary>
    public int? RecommendedConnections { get; set; }

    /// <summary>The connection count the provider refused to exceed, if we hit it.</summary>
    public int? ProviderConnectionCap { get; set; }

    public BenchmarkPipelining? Pipelining { get; set; }
    public long DataUsedBytes { get; set; }

    /// <summary>Wall-clock duration of the full run, for UI context next to data used.</summary>
    public double ElapsedSeconds { get; set; }

    /// <summary>Effective data budget for this run (profile default or user override).</summary>
    public long DataBudgetBytes { get; set; }

    /// <summary>True when the budget stopped the run before all planned levels.</summary>
    public bool BudgetLimited { get; set; }

    /// <summary>
    /// True when throughput was still rising at the highest connection level tested.
    /// Combined with <see cref="BudgetLimited"/>, this means untested upper levels mattered.
    /// </summary>
    public bool StillClimbing { get; set; }

    /// <summary>True when the run cycled through the article pool more than once (provider caching may inflate speeds).</summary>
    public bool WrappedPool { get; set; }

    /// <summary>True when this run only verified a single connection count.</summary>
    public bool VerificationRun { get; set; }

    /// <summary>
    /// Relative percent difference between the Thorough confirm window and the original
    /// knee measurement, if a confirm run was performed. Null when no confirm ran.
    /// </summary>
    public double? ConfirmDeltaPct { get; set; }

    /// <summary>"high" | "medium" | "low" — measurement quality signal for the UI.</summary>
    public string Confidence { get; set; } = "low";

    /// <summary>Warnings about concurrent activity (downloads/streams) that may depress the numbers. Rendered prominently.</summary>
    public List<string> ContentionWarnings { get; set; } = [];

    public List<string> Warnings { get; set; } = [];
}

/// <summary>Live progress payload pushed over the BenchmarkProgress websocket topic.</summary>
public sealed class BenchmarkProgressUpdate
{
    public required string Phase { get; init; } // latency | corpus | sweep | pipelining | done
    public required string Status { get; init; }
    public int Percent { get; init; }
    public int? CurrentConnections { get; init; }
    public long DataUsedBytes { get; init; }
    public long DataBudgetBytes { get; init; }
    public List<BenchmarkSweepPoint> Sweep { get; init; } = [];

    /// <summary>Set on the terminal <c>done</c> frame so the UI can finish even if the HTTP POST was dropped.</summary>
    public BenchmarkResult? Result { get; init; }

    /// <summary>Set on the terminal <c>done</c> frame when the run failed after starting.</summary>
    public string? Error { get; init; }
}
