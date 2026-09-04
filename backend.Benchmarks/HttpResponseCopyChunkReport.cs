using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace NzbWebDAV.Benchmarks;

internal static class HttpResponseCopyChunkReport
{
    private static readonly int[] ChunkBytes = [64 * 1024, 128 * 1024, 256 * 1024];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task RunAsync(string? jsonPath, string? scenarioName, int repetitions)
    {
        var scenario = NntpWholePathScenario.Profile.SingleOrDefault(candidate =>
            scenarioName is null || candidate.Name.Equals(scenarioName, StringComparison.Ordinal));
        if (scenario is null)
            throw new ArgumentException($"No HTTP-like profile scenario named '{scenarioName}'.");

        foreach (var chunkBytes in ChunkBytes)
        {
            await NntpWholePathReport.ExecuteForCopyChunkAsync(
                    scenario, verifyHash: false, chunkBytes)
                .ConfigureAwait(false);
            var correctness = await NntpWholePathReport.ExecuteForCopyChunkAsync(
                    scenario, verifyHash: true, chunkBytes)
                .ConfigureAwait(false);
            if (correctness.Deterministic.ActualBytes != correctness.Deterministic.ExpectedBytes ||
                correctness.Deterministic.Sha256Match != 1)
            {
                throw new InvalidOperationException(
                    $"{chunkBytes / 1024} KiB copy failed correctness verification.");
            }
        }

        var samples = new List<HttpCopyChunkSample>(ChunkBytes.Length * repetitions);
        for (var repetition = 1; repetition <= repetitions; repetition++)
        {
            for (var offset = 0; offset < ChunkBytes.Length; offset++)
            {
                var chunkBytes = ChunkBytes[(offset + repetition - 1) % ChunkBytes.Length];
                var result = await NntpWholePathReport.ExecuteForCopyChunkAsync(
                        scenario, verifyHash: false, chunkBytes)
                    .ConfigureAwait(false);
                var timing = result.Timing;
                var sample = new HttpCopyChunkSample(
                    chunkBytes / 1024,
                    repetition,
                    timing.WallSeconds,
                    timing.ThroughputMbps,
                    timing.ClientCpuSeconds,
                    timing.ClientCpuSecondsPerGb,
                    timing.ClientAllocatedBytes,
                    timing.FirstByteMs);
                samples.Add(sample);
                Console.WriteLine(
                    $"chunk_kib={sample.ChunkKiB} repetition={sample.Repetition} " +
                    $"wall_s={sample.WallSeconds:F6} throughput_mb_s={sample.ThroughputMbps:F3} " +
                    $"client_cpu_s={sample.ClientCpuSeconds:F6} " +
                    $"client_cpu_s_per_gb={sample.ClientCpuSecondsPerGb:F6} " +
                    $"allocated_bytes={sample.ClientAllocatedBytes} first_byte_ms={sample.FirstByteMs:F3}");
            }
        }

        var summaries = samples
            .GroupBy(sample => sample.ChunkKiB)
            .OrderBy(group => group.Key)
            .Select(group => Summarize(group.Key, group.ToArray()))
            .ToArray();
        foreach (var summary in summaries)
        {
            Console.WriteLine(
                $"summary chunk_kib={summary.ChunkKiB} " +
                $"throughput_median_mb_s={summary.ThroughputMbps.Median:F3} " +
                $"throughput_mad={summary.ThroughputMbps.Mad:F3} " +
                $"cpu_per_gb_median={summary.ClientCpuSecondsPerGb.Median:F6} " +
                $"cpu_per_gb_mad={summary.ClientCpuSecondsPerGb.Mad:F6} " +
                $"first_byte_median_ms={summary.FirstByteMs.Median:F3} " +
                $"first_byte_mad={summary.FirstByteMs.Mad:F3}");
        }

        if (jsonPath is not null)
            WriteJson(jsonPath, scenario.Name, samples, summaries);
    }

    private static HttpCopyChunkSummary Summarize(int chunkKiB, HttpCopyChunkSample[] samples) => new(
        chunkKiB,
        Summarize(samples.Select(sample => sample.WallSeconds)),
        Summarize(samples.Select(sample => sample.ThroughputMbps)),
        Summarize(samples.Select(sample => sample.ClientCpuSeconds)),
        Summarize(samples.Select(sample => sample.ClientCpuSecondsPerGb)),
        Summarize(samples.Select(sample => (double)sample.ClientAllocatedBytes)),
        Summarize(samples.Select(sample => sample.FirstByteMs)));

    internal static MedianAndMad Summarize(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
            throw new ArgumentException("At least one sample is required.", nameof(values));
        var median = Median(sorted);
        var deviations = sorted.Select(value => Math.Abs(value - median)).Order().ToArray();
        return new MedianAndMad(median, Median(deviations));
    }

    private static double Median(double[] sorted) =>
        sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;

    private static void WriteJson(
        string path,
        string scenario,
        IReadOnlyList<HttpCopyChunkSample> samples,
        IReadOnlyList<HttpCopyChunkSummary> summaries)
    {
        var document = new
        {
            schemaVersion = 1,
            report = "http-response-copy-chunks",
            meta = new
            {
                commit = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "unknown",
                generatedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                os = RuntimeInformation.OSDescription,
                dotnet = RuntimeInformation.FrameworkDescription,
            },
            scenario,
            samples,
            summaries,
        };
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(document, JsonOptions) +
            Environment.NewLine);
    }
}

internal sealed record HttpCopyChunkSample(
    int ChunkKiB,
    int Repetition,
    double WallSeconds,
    double ThroughputMbps,
    double ClientCpuSeconds,
    double ClientCpuSecondsPerGb,
    long ClientAllocatedBytes,
    double FirstByteMs);

internal sealed record HttpCopyChunkSummary(
    int ChunkKiB,
    MedianAndMad WallSeconds,
    MedianAndMad ThroughputMbps,
    MedianAndMad ClientCpuSeconds,
    MedianAndMad ClientCpuSecondsPerGb,
    MedianAndMad ClientAllocatedBytes,
    MedianAndMad FirstByteMs);

internal sealed record MedianAndMad(double Median, double Mad);
