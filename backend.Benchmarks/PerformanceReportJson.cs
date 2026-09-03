using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NzbWebDAV.Benchmarks;

internal sealed record ScenarioSnapshot(
    Dictionary<string, long> Deterministic,
    Dictionary<string, double> Timing);

internal static class PerformanceReportJson
{
    internal const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static void Write(
        string path,
        string report,
        IReadOnlyDictionary<string, ScenarioSnapshot> scenarios)
    {
        var document = new PerformanceReportDocument
        {
            SchemaVersion = SchemaVersion,
            Report = report,
            Meta = CreateMeta(),
            Scenarios = scenarios.ToDictionary(
                pair => pair.Key,
                pair => new ScenarioPayload
                {
                    Deterministic = pair.Value.Deterministic,
                    Timing = pair.Value.Timing,
                },
                StringComparer.Ordinal),
        };

        var json = JsonSerializer.Serialize(document, Options);
        File.WriteAllText(path, json + Environment.NewLine);
    }

    public static Dictionary<string, double> StreamingTiming(
        double firstByteMs,
        double elapsedMs,
        double throughputMiBs,
        double cpuSeconds) =>
        new(StringComparer.Ordinal)
        {
            ["firstByteMs"] = Round(firstByteMs),
            ["elapsedMs"] = Round(elapsedMs),
            ["throughputMiBs"] = Round(throughputMiBs),
            ["cpuSeconds"] = Round(cpuSeconds),
        };

    public static Dictionary<string, double> ApiTiming(double elapsedMs, double cpuSeconds) =>
        new(StringComparer.Ordinal)
        {
            ["elapsedMs"] = Round(elapsedMs),
            ["cpuSeconds"] = Round(cpuSeconds),
        };

    public static Dictionary<string, double> WholePathTiming(
        double wallSeconds,
        double clientCpuSeconds,
        double serverCpuSeconds,
        double clientCpuSecondsPerGb,
        double throughputMbps,
        double timeToPeakActiveMs,
        long clientAllocatedBytes,
        int gen0Collections,
        int gen1Collections,
        int gen2Collections) =>
        new(StringComparer.Ordinal)
        {
            ["wallSeconds"] = Round(wallSeconds),
            ["clientCpuSeconds"] = Round(clientCpuSeconds),
            ["serverCpuSeconds"] = Round(serverCpuSeconds),
            ["clientCpuSecondsPerGb"] = Round(clientCpuSecondsPerGb),
            ["throughputMbps"] = Round(throughputMbps),
            ["timeToPeakActiveMs"] = Round(timeToPeakActiveMs),
            ["clientAllocatedBytes"] = Round(clientAllocatedBytes),
            ["gen0Collections"] = Round(gen0Collections),
            ["gen1Collections"] = Round(gen1Collections),
            ["gen2Collections"] = Round(gen2Collections),
        };

    public static double Round(double value) =>
        Math.Round(value, 3, MidpointRounding.AwayFromZero);

    private static Dictionary<string, string> CreateMeta() =>
        new(StringComparer.Ordinal)
        {
            ["commit"] = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "unknown",
            ["generatedAt"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["os"] = RuntimeInformation.OSDescription,
            ["dotnet"] = RuntimeInformation.FrameworkDescription,
        };

    private sealed class PerformanceReportDocument
    {
        public int SchemaVersion { get; init; }
        public required string Report { get; init; }
        public required Dictionary<string, string> Meta { get; init; }
        public required Dictionary<string, ScenarioPayload> Scenarios { get; init; }
    }

    private sealed class ScenarioPayload
    {
        public required Dictionary<string, long> Deterministic { get; init; }
        public required Dictionary<string, double> Timing { get; init; }
    }
}
