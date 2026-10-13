using System.Diagnostics;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using UsenetSharp.Clients;

namespace NzbWebDAV.Benchmarks;

/// <summary>
/// Mount-style seek latency over the loopback NNTP server: each seek is a fresh
/// <see cref="NzbFileStream"/> reading one bounded range, like an rclone chunk request.
/// </summary>
internal static class NntpSeekReport
{
    internal const string ReportName = "nntp-seek";
    private const int ReadBytes = 8 * 1024 * 1024;
    private const int TimedRepetitions = 3;
    private const int ArticleBufferSize = 40;
    // Keeps seek targets off segment boundaries.
    private const long SeekShiftBytes = 12_345;
    // Five forward seeks and one backward, the shape a player scrub produces.
    private static readonly double[] SeekFractions = [0.01, 0.25, 0.5, 0.75, 0.95, 0.4];

    internal sealed record SeekScenario(string Name, bool RecordedRanges, NntpWholePathScenario Server);

    internal static IReadOnlyList<SeekScenario> Scenarios =>
    [
        new("seek-bounded-8mib-recorded-ranges", RecordedRanges: true, Server()),
        new("seek-bounded-8mib-migrated", RecordedRanges: false, Server()),
    ];

    private static NntpWholePathScenario Server() =>
        new("seek", NntpWholePathLayer.HttpLike, false, 342, 768 * 1024, 20, 4, 40, 6_000_000, YencCrcValidationMode.Require)
        {
            ArticleBufferSize = ArticleBufferSize,
            WarmStart = true,
        };

    public static async Task RunAsync(string? jsonPath, string? scenarioName)
    {
        var selected = Scenarios
            .Where(scenario => scenarioName is null || scenario.Name.Equals(scenarioName, StringComparison.Ordinal))
            .ToArray();
        if (selected.Length == 0)
            throw new ArgumentException($"No scenario named '{scenarioName}' exists in set 'seek'.");

        var snapshots = new Dictionary<string, ScenarioSnapshot>(StringComparer.Ordinal);
        foreach (var scenario in selected)
        {
            // Discarded warm-up for JIT and native dispatch setup.
            await ExecuteAsync(scenario).ConfigureAwait(false);

            var samples = new List<SeekSample>();
            long bodyCommands = 0;
            for (var repetition = 0; repetition < TimedRepetitions; repetition++)
            {
                var run = await ExecuteAsync(scenario).ConfigureAwait(false);
                samples.AddRange(run.Samples);
                bodyCommands += run.BodyCommands;
            }

            var firstByte = samples.Select(sample => sample.FirstByteMs).Order().ToArray();
            var complete = samples.Select(sample => sample.CompleteMs).Order().ToArray();
            var seeks = samples.Count;
            var timing = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["medianFirstByteMs"] = PerformanceReportJson.Round(Percentile(firstByte, 0.5)),
                ["p90FirstByteMs"] = PerformanceReportJson.Round(Percentile(firstByte, 0.9)),
                ["maxFirstByteMs"] = PerformanceReportJson.Round(firstByte[^1]),
                ["medianCompleteMs"] = PerformanceReportJson.Round(Percentile(complete, 0.5)),
                ["p90CompleteMs"] = PerformanceReportJson.Round(Percentile(complete, 0.9)),
                ["maxCompleteMs"] = PerformanceReportJson.Round(complete[^1]),
                // Includes cancelled read-ahead, so it varies with timing.
                ["bodyCommandsPerSeek"] = PerformanceReportJson.Round((double)bodyCommands / seeks),
            };
            snapshots[scenario.Name] = new ScenarioSnapshot(
                new Dictionary<string, long>(StringComparer.Ordinal)
                {
                    ["seeks"] = seeks,
                    ["bytesPerSeek"] = ReadBytes,
                },
                timing);

            Console.WriteLine(
                $"{scenario.Name} seeks={seeks} " +
                $"first_byte_ms median={timing["medianFirstByteMs"]:F1} p90={timing["p90FirstByteMs"]:F1} max={timing["maxFirstByteMs"]:F1} " +
                $"complete_8mib_ms median={timing["medianCompleteMs"]:F1} p90={timing["p90CompleteMs"]:F1} max={timing["maxCompleteMs"]:F1} " +
                $"body_commands_per_seek={timing["bodyCommandsPerSeek"]:F1}");
        }

        if (jsonPath is not null)
            PerformanceReportJson.Write(jsonPath, ReportName, snapshots);
    }

    private static async Task<SeekRun> ExecuteAsync(SeekScenario scenario)
    {
        var server = scenario.Server;
        var corpus = NntpLoopbackCorpus.Create(server.ArticleCount, server.DecodedArticleBytes, NntpWholePathReport.CorpusSeed);
        var ids = corpus.Articles.Select(article => article.SegmentId).ToArray();
        var fileSize = corpus.ExpectedBytes;
        var ranges = scenario.RecordedRanges
            ? Enumerable.Range(0, ids.Length)
                .Select(index => LongRange.FromStartAndSize((long)index * server.DecodedArticleBytes, server.DecodedArticleBytes))
                .ToArray()
            : null;

        var countersPath = Path.Join(Path.GetTempPath(), $"nntp-seek-{Guid.NewGuid():N}.json");
        await using var process = await LoopbackServerProcess.StartAsync(server, countersPath).ConfigureAwait(false);
        var budget = new InFlightArticleBudget(fileSize + server.DecodedArticleBytes);
        var oldBudget = InFlightArticleBudget.Current;
        InFlightArticleBudget.Current = budget;
        var provider = NntpWholePathReport.CreateProvider(server, process.Port);
        try
        {
            await provider.PrewarmConnectionsAsync(server.ConnectionCount, CancellationToken.None).ConfigureAwait(false);
            var samples = new List<SeekSample>(SeekFractions.Length);
            var buffer = new byte[64 * 1024];
            var output = new byte[ReadBytes];
            foreach (var fraction in SeekFractions)
            {
                var offset = Math.Min((long)(fileSize * fraction) + SeekShiftBytes, fileSize - ReadBytes);
                samples.Add(await SeekOnceAsync(
                        ids, fileSize, ranges, provider, budget, server, offset, buffer, output, corpus)
                    .ConfigureAwait(false));
            }

            provider.Dispose();
            provider = null;
            var snapshot = await process.StopAndGetSnapshotAsync().ConfigureAwait(false);
            if (budget.LeasedBytes != 0)
                throw new InvalidOperationException($"Article budget leaked {budget.LeasedBytes} bytes.");
            return new SeekRun(samples, snapshot.BodyCommands);
        }
        finally
        {
            provider?.Dispose();
            InFlightArticleBudget.Current = oldBudget;
            File.Delete(countersPath);
        }
    }

    private static async Task<SeekSample> SeekOnceAsync(
        string[] ids,
        long fileSize,
        LongRange[]? ranges,
        MultiProviderNntpClient provider,
        InFlightArticleBudget budget,
        NntpWholePathScenario server,
        long offset,
        byte[] buffer,
        byte[] output,
        NntpLoopbackCorpus corpus)
    {
        // Same stream-open hint WebDAV playback attaches.
        using var requestCts = new CancellationTokenSource();
        using var stripeScope = requestCts.Token.SetContext(new StreamingStripeContext
        {
            StripeCount = server.ConnectionCount,
        });
        var stream = new NzbFileStream(
            ids,
            fileSize,
            provider,
            ArticleBufferSize,
            ranges,
            usePipelinedBodyRequests: true,
            fileName: "loopback-seek.bin",
            inFlightArticleBudget: budget,
            streamingBodyBatchWidth: server.BatchWidth,
            readBudgetOverride: ReadBytes,
            readStartWarmupEnabled: true);
        await using (stream.ConfigureAwait(false))
        {
            stream.Position = offset;
            var started = Stopwatch.StartNew();
            var firstByte = TimeSpan.Zero;
            var total = 0;
            while (total < ReadBytes)
            {
                var read = await stream.ReadAsync(
                        buffer.AsMemory(0, Math.Min(buffer.Length, ReadBytes - total)), requestCts.Token)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;
                if (total == 0)
                    firstByte = started.Elapsed;
                buffer.AsSpan(0, read).CopyTo(output.AsSpan(total));
                total += read;
            }

            var complete = started.Elapsed;
            if (total != ReadBytes || !output.AsSpan().SequenceEqual(corpus.DecodedFile.AsSpan((int)offset, ReadBytes)))
                throw new InvalidOperationException($"Seek read at {offset} returned {total} bytes that do not match the corpus.");
            return new SeekSample(firstByte.TotalMilliseconds, complete.TotalMilliseconds);
        }
    }

    private static double Percentile(double[] sorted, double fraction)
    {
        var rank = fraction * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (rank - lower);
    }

    private readonly record struct SeekSample(double FirstByteMs, double CompleteMs);

    private sealed record SeekRun(IReadOnlyList<SeekSample> Samples, long BodyCommands);
}
