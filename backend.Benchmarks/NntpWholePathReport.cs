using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using UsenetSharp.Clients;
using UsenetSharp.Models;

namespace NzbWebDAV.Benchmarks;

/// <summary>
/// Provider-independent whole-path NNTP report. The socket server is a child process
/// so yEnc encoding, TLS (when introduced), and server writes do not affect client CPU.
/// It is intentionally a measurement harness; it does not change the product path.
/// </summary>
internal static class NntpWholePathReport
{
    internal const string ReportName = "nntp-whole-path";
    private const int CorpusSeed = 1025;
    private const int TimedRepetitions = 3;

    public static async Task RunAsync(
        string? jsonPath,
        string scenarioSet,
        string? scenarioName = null)
    {
        var selected = NntpWholePathScenario.ForSet(scenarioSet)
            .Where(scenario => scenarioName is null ||
                               scenario.Name.Equals(scenarioName, StringComparison.Ordinal))
            .ToArray();
        if (selected.Length == 0)
            throw new ArgumentException($"No scenario named '{scenarioName}' exists in set '{scenarioSet}'.");

        var snapshots = new Dictionary<string, ScenarioSnapshot>(StringComparer.Ordinal);
        foreach (var scenario in selected)
        {
            // Tiered JIT and native dispatch setup are intentionally discarded.
            await ExecuteAsync(scenario, verifyHash: false, httpLike: scenario.Layer == NntpWholePathLayer.HttpLike)
                .ConfigureAwait(false);
            var correctness = await ExecuteAsync(scenario, verifyHash: true, httpLike: scenario.Layer == NntpWholePathLayer.HttpLike)
                .ConfigureAwait(false);

            NntpWholePathTiming? total = null;
            for (var repetition = 0; repetition < TimedRepetitions; repetition++)
            {
                var timed = await ExecuteAsync(scenario, verifyHash: false, httpLike: scenario.Layer == NntpWholePathLayer.HttpLike)
                    .ConfigureAwait(false);
                total = total is null ? timed.Timing : Add(total, timed.Timing);
            }

            var timing = Divide(total!, TimedRepetitions);
            var deterministic = correctness.Deterministic;
            snapshots[scenario.Name] = new ScenarioSnapshot(
                new Dictionary<string, long>(StringComparer.Ordinal)
                {
                    ["expectedBytes"] = deterministic.ExpectedBytes,
                    ["actualBytes"] = deterministic.ActualBytes,
                    ["sha256Match"] = deterministic.Sha256Match,
                    ["bodyCommands"] = deterministic.BodyCommands,
                    ["responses"] = deterministic.Responses,
                    ["retrievedCallbacks"] = deterministic.RetrievedCallbacks,
                    ["cancelledCallbacks"] = deterministic.CancelledCallbacks,
                    ["notFoundCallbacks"] = deterministic.NotFoundCallbacks,
                    ["notRetrievedCallbacks"] = deterministic.NotRetrievedCallbacks,
                    ["finalArticleBudgetBytes"] = deterministic.FinalArticleBudgetBytes,
                    ["finalPipeBufferedBytes"] = deterministic.FinalPipeBufferedBytes,
                    ["outstandingPermits"] = deterministic.OutstandingPermits,
                },
                PerformanceReportJson.WholePathTiming(
                    timing.WallSeconds,
                    timing.ClientCpuSeconds,
                    timing.ServerCpuSeconds,
                    timing.ClientCpuSecondsPerGb,
                    timing.ThroughputMbps,
                    timing.ClientAllocatedBytes,
                    timing.Gen0Collections,
                    timing.Gen1Collections,
                    timing.Gen2Collections));

            Console.WriteLine(
                $"{scenario.Name} bytes={deterministic.ActualBytes} sha256_match={deterministic.Sha256Match} " +
                $"body_commands={deterministic.BodyCommands} peak_connections={deterministic.PeakActiveConnections} " +
                $"wall_s={timing.WallSeconds:F3} " +
                $"throughput_mb_s={timing.ThroughputMbps:F3} client_cpu_s={timing.ClientCpuSeconds:F3}");
        }

        if (jsonPath is not null)
            PerformanceReportJson.Write(jsonPath, ReportName, snapshots);
    }

    public static async Task RunChildServerAsync(LoopbackServerArguments arguments)
    {
        var corpus = NntpLoopbackCorpus.Create(arguments.ArticleCount, arguments.DecodedArticleBytes, arguments.Seed);
        await using var server = await NntpLoopbackServer.StartAsync(
                corpus,
                arguments.RoundTripDelayMs,
                arguments.BandwidthBytesPerSecond,
                arguments.MissingIds)
            .ConfigureAwait(false);
        Console.WriteLine($"READY {server.Port}");
        await Console.Out.FlushAsync().ConfigureAwait(false);
        await Console.In.ReadLineAsync().ConfigureAwait(false);
        await server.WriteSnapshotAsync(arguments.CountersPath, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<NntpWholePathResult> ExecuteAsync(
        NntpWholePathScenario scenario,
        bool verifyHash,
        bool httpLike)
    {
        if (scenario.UseTls)
            throw new NotSupportedException(
                "Validated TLS loopback scenarios are deferred until a test-CA trust mechanism is available.");

        var corpus = NntpLoopbackCorpus.Create(
            scenario.ArticleCount,
            scenario.DecodedArticleBytes,
            CorpusSeed);
        var countersPath = Path.Combine(Path.GetTempPath(), $"nntp-loopback-{Guid.NewGuid():N}.json");
        await using var server = await LoopbackServerProcess.StartAsync(scenario, countersPath).ConfigureAwait(false);
        var callbackCounts = new CallbackCounts();
        var budget = new InFlightArticleBudget(corpus.ExpectedBytes + scenario.DecodedArticleBytes);
        var oldBudget = InFlightArticleBudget.Current;
        InFlightArticleBudget.Current = budget;
        try
        {
            var process = Process.GetCurrentProcess();
            process.Refresh();
            var cpuBefore = process.TotalProcessorTime;
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
            var collectionCounts = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
            var started = Stopwatch.StartNew();
            var bytes = scenario.Layer switch
            {
                NntpWholePathLayer.Transport => await ReadTransportAsync(
                    scenario, server.Port, corpus, callbackCounts, verifyHash).ConfigureAwait(false),
                NntpWholePathLayer.Provider => await ReadProviderAsync(
                    scenario, server.Port, corpus, callbackCounts, verifyHash).ConfigureAwait(false),
                NntpWholePathLayer.BufferedStream or NntpWholePathLayer.HttpLike =>
                    await ReadBufferedStreamAsync(
                        scenario, server.Port, corpus, callbackCounts, budget, verifyHash, httpLike)
                    .ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
            };
            started.Stop();
            process.Refresh();

            var serverSnapshot = await server.StopAndGetSnapshotAsync().ConfigureAwait(false);
            var shaMatches = bytes.Sha256 is null || bytes.Sha256.Equals(corpus.ExpectedSha256, StringComparison.Ordinal);
            if (verifyHash && (!shaMatches || bytes.Count != corpus.ExpectedBytes))
                throw new InvalidOperationException(
                    $"Whole-path output did not match corpus: {bytes.Count}/{corpus.ExpectedBytes}, hash={bytes.Sha256}.");
            if (budget.LeasedBytes != 0)
                throw new InvalidOperationException($"Article budget leaked {budget.LeasedBytes} bytes.");

            var elapsed = Math.Max(started.Elapsed.TotalSeconds, double.Epsilon);
            var clientCpu = (process.TotalProcessorTime - cpuBefore).TotalSeconds;
            var serverCpu = server.ProcessCpuSeconds;
            return new NntpWholePathResult(
                scenario,
                new NntpWholePathDeterministic(
                    corpus.ExpectedBytes,
                    bytes.Count,
                    shaMatches ? 1 : 0,
                    serverSnapshot.BodyCommands,
                    serverSnapshot.Responses,
                    callbackCounts.Retrieved,
                    callbackCounts.Cancelled,
                    callbackCounts.NotFound,
                    callbackCounts.NotRetrieved,
                    budget.LeasedBytes,
                    0,
                    0,
                    serverSnapshot.PeakActiveConnections),
                new NntpWholePathTiming(
                    started.Elapsed.TotalSeconds,
                    clientCpu,
                    serverCpu,
                    clientCpu / (bytes.Count / 1_000_000_000d),
                    bytes.Count / elapsed / 1_000_000d,
                    GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore,
                    GC.CollectionCount(0) - collectionCounts[0],
                    GC.CollectionCount(1) - collectionCounts[1],
                    GC.CollectionCount(2) - collectionCounts[2]));
        }
        finally
        {
            InFlightArticleBudget.Current = oldBudget;
            File.Delete(countersPath);
        }
    }

    private static async Task<ReadResult> ReadTransportAsync(
        NntpWholePathScenario scenario,
        int port,
        NntpLoopbackCorpus corpus,
        CallbackCounts counts,
        bool verifyHash)
    {
        await using var client = new UsenetClient(CreateOptions(scenario));
        await client.ConnectAsync("127.0.0.1", port, useSsl: false, CancellationToken.None).ConfigureAwait(false);
        await client.AuthenticateAsync("benchmark", "benchmark", CancellationToken.None).ConfigureAwait(false);
        return await ReadBatchesAsync(
            corpus.Articles.Select(article => new SegmentId(article.SegmentId)).ToArray(),
            scenario.BatchWidth,
            (ids, callback) => client.DecodedBodiesAsync(ids, callback, CancellationToken.None),
            counts,
            verifyHash).ConfigureAwait(false);
    }

    private static async Task<ReadResult> ReadProviderAsync(
        NntpWholePathScenario scenario,
        int port,
        NntpLoopbackCorpus corpus,
        CallbackCounts counts,
        bool verifyHash)
    {
        using var provider = CreateProvider(scenario, port);
        return await ReadBatchesAsync(
            corpus.Articles.Select(article => new SegmentId(article.SegmentId)).ToArray(),
            scenario.BatchWidth,
            (ids, callback) => provider.DecodedBodiesAsync(ids, callback, CancellationToken.None),
            counts,
            verifyHash).ConfigureAwait(false);
    }

    private static async Task<ReadResult> ReadBufferedStreamAsync(
        NntpWholePathScenario scenario,
        int port,
        NntpLoopbackCorpus corpus,
        CallbackCounts counts,
        InFlightArticleBudget budget,
        bool verifyHash,
        bool httpLike)
    {
        using var provider = CreateProvider(scenario, port);
        var ids = corpus.Articles.Select(article => article.SegmentId).ToArray();
        var sizes = Enumerable.Repeat((long)scenario.DecodedArticleBytes, scenario.ArticleCount).ToArray();
        await using var stream = MultiSegmentStream.Create(
            ids.AsMemory(),
            provider,
            articleBufferSize: Math.Max(scenario.BatchWidth * 2, 4),
            estimatedSegmentSize: scenario.DecodedArticleBytes,
            failFastOnFirstSegment: true,
            usePipelinedBodyRequests: true,
            cancellationToken: CancellationToken.None,
            fileName: "loopback.bin",
            exactSegmentSizes: sizes,
            inFlightArticleBudget: budget,
            bodyPipelineBatchWidth: scenario.BatchWidth);

        if (verifyHash)
            return await CopyAndHashAsync(stream, CancellationToken.None).ConfigureAwait(false);
        if (httpLike)
        {
            var sink = new HttpLikeCountingSink();
            await sink.CopyFromAsync(stream, CancellationToken.None).ConfigureAwait(false);
            return new ReadResult(sink.BytesWritten, null);
        }

        await stream.CopyToAsync(Stream.Null, CancellationToken.None).ConfigureAwait(false);
        return new ReadResult(corpus.ExpectedBytes, null);
    }

#pragma warning disable CA2000 // MultiProviderNntpClient owns and disposes its MultiConnectionNntpClient and pool.
    private static MultiProviderNntpClient CreateProvider(NntpWholePathScenario scenario, int port)
    {
        var pool = new ConnectionPool<INntpClient>(
            scenario.ConnectionCount,
            async cancellationToken =>
            {
                var client = new BaseNntpClient(new UsenetClient(CreateOptions(scenario)));
                await client.ConnectAsync("127.0.0.1", port, useSsl: false, cancellationToken).ConfigureAwait(false);
                await client.AuthenticateAsync("benchmark", "benchmark", cancellationToken).ConfigureAwait(false);
                return client;
            },
            diagnosticName: "loopback");
        var connection = new MultiConnectionNntpClient(
            pool,
            ProviderType.Pooled,
            new ProviderCircuitBreaker("loopback"),
            "loopback",
            pipeliningDepth: scenario.BatchWidth,
            maxTransferConnections: scenario.ConnectionCount);
        return new MultiProviderNntpClient([connection]);
    }
#pragma warning restore CA2000

    private static UsenetClientOptions CreateOptions(NntpWholePathScenario scenario) => new()
    {
        CrcValidation = scenario.CrcValidation,
        ReadTimeout = TimeSpan.FromSeconds(30),
        MaxPipelineDepth = Math.Max(scenario.BatchWidth, 8),
    };

    private static async Task<ReadResult> ReadBatchesAsync(
        SegmentId[] ids,
        int width,
        Func<IReadOnlyList<SegmentId>, ArticleBodyCompletionHandler, Task<UsenetDecodedBodyBatch>> readBatch,
        CallbackCounts counts,
        bool verifyHash)
    {
        using var hash = verifyHash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        long total = 0;
        for (var start = 0; start < ids.Length; start += width)
        {
            var batchIds = ids.Skip(start).Take(Math.Min(width, ids.Length - start)).ToArray();
            var batch = await readBatch(batchIds, counts.Record).ConfigureAwait(false);
            foreach (var responseTask in batch.Responses)
            {
                var response = await responseTask.ConfigureAwait(false);
                if (!response.Success || response.Stream is null)
                    throw new InvalidOperationException($"Loopback BODY failed with {response.ResponseCode}.");
                await using var stream = response.Stream;
                total += await DrainAsync(stream, hash, CancellationToken.None).ConfigureAwait(false);
            }
            await batch.Completion.ConfigureAwait(false);
        }
        return new ReadResult(total, hash is null ? null : Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static async Task<ReadResult> CopyAndHashAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            hash.AppendData(buffer, 0, read);
            total += read;
        }
        return new ReadResult(total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static async Task<int> DrainAsync(Stream stream, IncrementalHash? hash, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return total;
            hash?.AppendData(buffer, 0, read);
            total += read;
        }
    }

    private static NntpWholePathTiming Add(NntpWholePathTiming left, NntpWholePathTiming right) => new(
        left.WallSeconds + right.WallSeconds,
        left.ClientCpuSeconds + right.ClientCpuSeconds,
        left.ServerCpuSeconds + right.ServerCpuSeconds,
        left.ClientCpuSecondsPerGb + right.ClientCpuSecondsPerGb,
        left.ThroughputMbps + right.ThroughputMbps,
        left.ClientAllocatedBytes + right.ClientAllocatedBytes,
        left.Gen0Collections + right.Gen0Collections,
        left.Gen1Collections + right.Gen1Collections,
        left.Gen2Collections + right.Gen2Collections);

    private static NntpWholePathTiming Divide(NntpWholePathTiming value, int divisor) => new(
        value.WallSeconds / divisor,
        value.ClientCpuSeconds / divisor,
        value.ServerCpuSeconds / divisor,
        value.ClientCpuSecondsPerGb / divisor,
        value.ThroughputMbps / divisor,
        value.ClientAllocatedBytes / divisor,
        value.Gen0Collections / divisor,
        value.Gen1Collections / divisor,
        value.Gen2Collections / divisor);

    private readonly record struct ReadResult(long Count, string? Sha256);

    private sealed class CallbackCounts
    {
        private long _retrieved;
        private long _cancelled;
        private long _notFound;
        private long _notRetrieved;

        public long Retrieved => Interlocked.Read(ref _retrieved);
        public long Cancelled => Interlocked.Read(ref _cancelled);
        public long NotFound => Interlocked.Read(ref _notFound);
        public long NotRetrieved => Interlocked.Read(ref _notRetrieved);

        public void Record(ArticleBodyResult result, string? failureReason)
        {
            _ = failureReason;
            switch (result)
            {
                case ArticleBodyResult.Retrieved:
                    Interlocked.Increment(ref _retrieved);
                    break;
                case ArticleBodyResult.Cancelled:
                    Interlocked.Increment(ref _cancelled);
                    break;
                case ArticleBodyResult.NotFound:
                    Interlocked.Increment(ref _notFound);
                    break;
                case ArticleBodyResult.NotRetrieved:
                    Interlocked.Increment(ref _notRetrieved);
                    break;
            }
        }
    }
}

internal sealed record LoopbackServerArguments(
    int ArticleCount,
    int DecodedArticleBytes,
    int Seed,
    int RoundTripDelayMs,
    long? BandwidthBytesPerSecond,
    string CountersPath,
    IReadOnlyList<string> MissingIds);

internal sealed class LoopbackServerProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _countersPath;
    private readonly TimeSpan _cpuBefore;

    private LoopbackServerProcess(Process process, string countersPath)
    {
        _process = process;
        _countersPath = countersPath;
        _cpuBefore = process.TotalProcessorTime;
    }

    public int Port { get; private init; }
    public double ProcessCpuSeconds { get; private set; }

    public static async Task<LoopbackServerProcess> StartAsync(
        NntpWholePathScenario scenario,
        string countersPath)
    {
        var processPath = Environment.ProcessPath ?? "dotnet";
        var startInfo = new ProcessStartInfo(processPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            startInfo.ArgumentList.Add(typeof(NntpWholePathReport).Assembly.Location);
        startInfo.ArgumentList.Add("--nntp-loopback-server");
        startInfo.ArgumentList.Add("--articles");
        startInfo.ArgumentList.Add(scenario.ArticleCount.ToString());
        startInfo.ArgumentList.Add("--article-bytes");
        startInfo.ArgumentList.Add(scenario.DecodedArticleBytes.ToString());
        startInfo.ArgumentList.Add("--seed");
        startInfo.ArgumentList.Add("1025");
        startInfo.ArgumentList.Add("--rtt-ms");
        startInfo.ArgumentList.Add(scenario.RoundTripDelayMs.ToString());
        if (scenario.BandwidthBytesPerSecond is { } bandwidth)
        {
            startInfo.ArgumentList.Add("--bandwidth-bps");
            startInfo.ArgumentList.Add(bandwidth.ToString());
        }
        startInfo.ArgumentList.Add("--counters-out");
        startInfo.ArgumentList.Add(countersPath);

        var process = Process.Start(startInfo) ??
                      throw new InvalidOperationException("Could not start loopback NNTP server process.");
        var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        if (line is null || !line.StartsWith("READY ", StringComparison.Ordinal) ||
            !int.TryParse(line[6..], out var port))
        {
            var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"Loopback NNTP server did not become ready: {error}");
        }
        return new LoopbackServerProcess(process, countersPath) { Port = port };
    }

    public async Task<NntpLoopbackServerSnapshot> StopAndGetSnapshotAsync()
    {
        var cpuAfter = _cpuBefore;
        if (!_process.HasExited)
        {
            _process.Refresh();
            cpuAfter = _process.TotalProcessorTime;
        }
        if (!_process.HasExited)
        {
            await _process.StandardInput.WriteLineAsync("STOP").ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        ProcessCpuSeconds = Math.Max(0, (cpuAfter - _cpuBefore).TotalSeconds);
        if (_process.ExitCode != 0)
        {
            var error = await _process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"Loopback NNTP server exited with {_process.ExitCode}: {error}");
        }
        var json = await File.ReadAllTextAsync(_countersPath).ConfigureAwait(false);
        return JsonSerializer.Deserialize<NntpLoopbackServerSnapshot>(json) ??
               throw new InvalidDataException("Loopback server did not write counters.");
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }
        _process.Dispose();
    }
}
