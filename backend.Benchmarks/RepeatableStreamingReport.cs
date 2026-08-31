using System.Diagnostics;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Benchmarks;

internal static class RepeatableStreamingReport
{
    private const int SegmentSize = 256 * 1024;
    private const int SegmentCount = 12;
    private const int ProbeSize = 64 * 1024;
    private const int ProductionArticleBufferSize = 40;
    private const int ProductionBodyBatchWidth = 4;
    private const string ReportName = "streaming";

    public static async Task RunAsync(string? jsonPath = null)
    {
        // Discarded warm-up: JIT/tiering must not pollute cold-sequential CPU/timing.
        await RunOnceAsync(print: false).ConfigureAwait(false);

        var scenarios = await RunOnceAsync(print: true).ConfigureAwait(false);
        if (jsonPath is not null)
            PerformanceReportJson.Write(jsonPath, ReportName, scenarios);
    }

    private static async Task<Dictionary<string, ScenarioSnapshot>> RunOnceAsync(bool print)
    {
        var fixture = CreateFixture();
        var cacheDir = Path.Join(Path.GetTempPath(), "nzbdav-repeatable-streaming-" + Guid.NewGuid().ToString("N"));
        var pipelinedCacheDir = Path.Join(
            Path.GetTempPath(),
            "nzbdav-repeatable-streaming-pipelined-" + Guid.NewGuid().ToString("N"));
        var scenarios = new Dictionary<string, ScenarioSnapshot>(StringComparer.Ordinal);

        try
        {
            using var transport = fixture.CreateTransport();
            using var cached = new SegmentCacheNntpClient(
                transport,
                cacheDir,
                maxBytes: fixture.Source.Length * 2L);
            await WaitForCatalogAsync(cached).ConfigureAwait(false);

            await RecordAsync(
                scenarios, print, transport, "cold-sequential",
                async () =>
                {
                    var metrics = await ReadAllAsync(transport, fixture).ConfigureAwait(false);
                    return (metrics, fixture.Source.Length);
                }).ConfigureAwait(false);

            await RecordAsync(
                scenarios, print, transport, "cache-prime",
                async () =>
                {
                    var metrics = await PrimeCacheAsync(cached, fixture).ConfigureAwait(false);
                    return (metrics, fixture.Source.Length);
                }).ConfigureAwait(false);

            await RecordAsync(
                scenarios, print, transport, "warm-reread",
                async () =>
                {
                    var metrics = await ReadAllAsync(cached, fixture).ConfigureAwait(false);
                    return (metrics, fixture.Source.Length);
                }).ConfigureAwait(false);

            await RecordAsync(
                scenarios, print, transport, "range-probe",
                async () =>
                {
                    var metrics = await ProbeAsync(cached, fixture, SegmentSize * 4L + 137, ProbeSize)
                        .ConfigureAwait(false);
                    return (metrics, ProbeSize);
                }).ConfigureAwait(false);

            await RecordAsync(
                scenarios, print, transport, "tail-probe",
                async () =>
                {
                    var metrics = await ProbeAsync(
                            cached, fixture, fixture.Source.Length - ProbeSize, ProbeSize)
                        .ConfigureAwait(false);
                    return (metrics, ProbeSize);
                }).ConfigureAwait(false);

            await RecordAsync(
                scenarios, print, transport, "seeks",
                async () =>
                {
                    var metrics = await SeekAsync(cached, fixture).ConfigureAwait(false);
                    return (metrics, metrics.BytesRead);
                }, extra: static _ => "seek_count=4").ConfigureAwait(false);

            var dead = await MeasureDeadArticleAsync(fixture).ConfigureAwait(false);
            scenarios["dead-article"] = dead.Snapshot;
            if (print)
            {
                Print(
                    "dead-article",
                    dead.Snapshot,
                    extra: $"zero_filled_bytes={dead.Snapshot.Deterministic["bytes"]}");
            }

            var pipelinedStatistics = new SegmentCacheStatistics();
            using var pipelinedTransport = fixture.CreateTransport();
            using var pipelinedCache = new SegmentCacheNntpClient(
                pipelinedTransport,
                pipelinedCacheDir,
                maxBytes: fixture.Source.Length * 2L,
                usageTracker: null,
                metricsWriter: null,
                enumerateCacheFiles: null,
                pipelinedStatistics);
            await WaitForCatalogAsync(pipelinedCache).ConfigureAwait(false);

            await RecordPipelinedAsync(
                scenarios,
                print,
                pipelinedTransport,
                pipelinedStatistics,
                "pipelined-cold-read",
                () => ReadAllVerifiedAsync(
                    fixture.CreateProductionShapedStream(pipelinedCache),
                    fixture.Source)).ConfigureAwait(false);

            await RecordPipelinedAsync(
                scenarios,
                print,
                pipelinedTransport,
                pipelinedStatistics,
                "pipelined-warm-reread",
                () => ReadAllVerifiedAsync(
                    fixture.CreateProductionShapedStream(pipelinedCache),
                    fixture.Source)).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
            if (Directory.Exists(pipelinedCacheDir))
                Directory.Delete(pipelinedCacheDir, recursive: true);
        }

        return scenarios;
    }

    private static async Task RecordAsync(
        Dictionary<string, ScenarioSnapshot> scenarios,
        bool print,
        BenchmarkNntpClient transport,
        string name,
        Func<Task<(StreamingMetrics Metrics, long Bytes)>> action,
        Func<ScenarioSnapshot, string>? extra = null)
    {
        var requestsBefore = transport.BodyRequestCount;
        var bytesBefore = transport.BodyBytesRequested;
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var (metrics, bytes) = await action().ConfigureAwait(false);
        process.Refresh();
        var cpuSeconds = (process.TotalProcessorTime - cpuBefore).TotalSeconds;
        var snapshot = ToSnapshot(
            metrics,
            bytes,
            transport.BodyRequestCount - requestsBefore,
            transport.BodyBytesRequested - bytesBefore,
            cpuSeconds);
        scenarios[name] = snapshot;
        if (print)
            Print(name, snapshot, extra: extra?.Invoke(snapshot));
    }

    private static async Task RecordPipelinedAsync(
        Dictionary<string, ScenarioSnapshot> scenarios,
        bool print,
        BenchmarkNntpClient transport,
        SegmentCacheStatistics statistics,
        string name,
        Func<Task<StreamingMetrics>> action)
    {
        var requestsBefore = transport.BodyRequestCount;
        var bytesBefore = transport.BodyBytesRequested;
        var batchesBefore = transport.BatchRequestCount;
        var cacheBefore = statistics.GetSnapshot();
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var metrics = await action().ConfigureAwait(false);
        process.Refresh();
        var cpuSeconds = (process.TotalProcessorTime - cpuBefore).TotalSeconds;
        var cacheAfter = statistics.GetSnapshot();
        var snapshot = ToPipelinedSnapshot(
            metrics,
            transport.BodyRequestCount - requestsBefore,
            transport.BodyBytesRequested - bytesBefore,
            transport.BatchRequestCount - batchesBefore,
            cacheBefore,
            cacheAfter,
            cpuSeconds);
        scenarios[name] = snapshot;
        if (print)
            Print(name, snapshot);
    }

    private static async Task<DeadArticleCapture> MeasureDeadArticleAsync(Fixture fixture)
    {
        var missingSegment = fixture.SegmentIds[5];
        using var transport = new BenchmarkNntpClient(
            fixture.Segments.Where(pair => pair.Key != missingSegment).ToDictionary(),
            useCachedYencStreams: true,
            fixture.RangesById);
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        await using var stream = fixture.CreateStream(transport);
        stream.Seek(SegmentSize * 5L, SeekOrigin.Begin);
        var buffer = new byte[SegmentSize];
        var stopwatch = Stopwatch.StartNew();
        var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: true).ConfigureAwait(false);
        stopwatch.Stop();
        process.Refresh();
        var cpuSeconds = (process.TotalProcessorTime - cpuBefore).TotalSeconds;

        if (buffer.AsSpan(0, read).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidOperationException("Dead-article probe did not return an all-zero gap.");

        var metrics = new StreamingMetrics(stopwatch.Elapsed, stopwatch.Elapsed, read, OperationCount: 1);
        return new DeadArticleCapture(ToSnapshot(
            metrics,
            read,
            transport.BodyRequestCount,
            transport.BodyBytesRequested,
            cpuSeconds));
    }

    private static ScenarioSnapshot ToSnapshot(
        StreamingMetrics metrics,
        long bytes,
        int transportRequests,
        long transportBytes,
        double cpuSeconds)
    {
        var seconds = Math.Max(metrics.Elapsed.TotalSeconds, double.Epsilon);
        var throughput = bytes / 1024d / 1024d / seconds;
        return new ScenarioSnapshot(
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["bytes"] = bytes,
                ["transportRequests"] = transportRequests,
                ["transportBytes"] = transportBytes,
            },
            PerformanceReportJson.StreamingTiming(
                metrics.FirstByte.TotalMilliseconds,
                metrics.Elapsed.TotalMilliseconds,
                throughput,
                cpuSeconds));
    }

    private static ScenarioSnapshot ToPipelinedSnapshot(
        StreamingMetrics metrics,
        int transportRequests,
        long transportBytes,
        int transportBatchRequests,
        SegmentCacheSnapshot cacheBefore,
        SegmentCacheSnapshot cacheAfter,
        double cpuSeconds)
    {
        var seconds = Math.Max(metrics.Elapsed.TotalSeconds, double.Epsilon);
        var throughput = metrics.BytesRead / 1024d / 1024d / seconds;
        return new ScenarioSnapshot(
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["bytes"] = metrics.BytesRead,
                ["transportRequests"] = transportRequests,
                ["transportBytes"] = transportBytes,
                ["transportBatchRequests"] = transportBatchRequests,
                ["cacheHits"] = cacheAfter.Hits - cacheBefore.Hits,
                ["cacheMisses"] = cacheAfter.Misses - cacheBefore.Misses,
                ["cacheBytesServed"] = cacheAfter.BytesServed - cacheBefore.BytesServed,
                ["cacheBatchBypassRequests"] = cacheAfter.BatchBypassRequests - cacheBefore.BatchBypassRequests,
                ["cacheBatchBypassArticles"] = cacheAfter.BatchBypassArticles - cacheBefore.BatchBypassArticles,
                ["cacheWriteCommits"] = cacheAfter.WriteCommits - cacheBefore.WriteCommits,
            },
            PerformanceReportJson.StreamingTiming(
                metrics.FirstByte.TotalMilliseconds,
                metrics.Elapsed.TotalMilliseconds,
                throughput,
                cpuSeconds));
    }

    private static async Task<StreamingMetrics> ReadAllAsync(INntpClient client, Fixture fixture)
    {
        await using var stream = fixture.CreateStream(client);
        var buffer = new byte[ProbeSize];
        var stopwatch = Stopwatch.StartNew();
        var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: true).ConfigureAwait(false);
        var firstByte = stopwatch.Elapsed;
        await stream.CopyToAsync(Stream.Null).ConfigureAwait(false);
        stopwatch.Stop();
        return new StreamingMetrics(stopwatch.Elapsed, firstByte, read, OperationCount: 1);
    }

    private static async Task<StreamingMetrics> ReadAllVerifiedAsync(NzbFileStream stream, byte[] expected)
    {
        await using (stream)
        {
            var output = new byte[expected.Length];
            var stopwatch = Stopwatch.StartNew();
            var firstCount = Math.Min(ProbeSize, output.Length);
            var firstRead = await stream.ReadAtLeastAsync(output.AsMemory(0, firstCount), firstCount, throwOnEndOfStream: true)
                .ConfigureAwait(false);
            var firstByte = stopwatch.Elapsed;
            var total = firstRead;
            while (total < output.Length)
            {
                var read = await stream.ReadAsync(output.AsMemory(total)).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
            }

            stopwatch.Stop();
            if (total != expected.Length)
                throw new InvalidOperationException(
                    $"Pipelined streaming read length {total} did not match fixture length {expected.Length}.");
            Verify(expected, output, "pipelined-read");
            return new StreamingMetrics(stopwatch.Elapsed, firstByte, total, OperationCount: 1);
        }
    }

    private static async Task<StreamingMetrics> PrimeCacheAsync(SegmentCacheNntpClient client, Fixture fixture)
    {
        var stopwatch = Stopwatch.StartNew();
        var firstByte = TimeSpan.Zero;
        var bytesRead = 0L;

        foreach (var segmentId in fixture.SegmentIds)
        {
            var response = await client.DecodedBodyAsync(segmentId, CancellationToken.None).ConfigureAwait(false);
            using var body = response.Stream
                ?? throw new InvalidOperationException($"Cache prime returned no body for {segmentId}.");
            var buffer = new byte[ProbeSize];
            var read = await body.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: true)
                .ConfigureAwait(false);
            if (bytesRead == 0) firstByte = stopwatch.Elapsed;
            bytesRead += read;
            await body.CopyToAsync(Stream.Null).ConfigureAwait(false);
        }

        stopwatch.Stop();
        return new StreamingMetrics(stopwatch.Elapsed, firstByte, bytesRead, fixture.SegmentIds.Length);
    }

    private static async Task<StreamingMetrics> ProbeAsync(
        INntpClient client,
        Fixture fixture,
        long offset,
        int count)
    {
        await using var stream = fixture.CreateStream(client);
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[count];
        var stopwatch = Stopwatch.StartNew();
        var read = await stream.ReadAtLeastAsync(buffer, count, throwOnEndOfStream: true).ConfigureAwait(false);
        stopwatch.Stop();
        Verify(fixture.Source.AsSpan((int)offset, read), buffer.AsSpan(0, read), "probe");
        return new StreamingMetrics(stopwatch.Elapsed, stopwatch.Elapsed, read, OperationCount: 1);
    }

    private static async Task<StreamingMetrics> SeekAsync(INntpClient client, Fixture fixture)
    {
        var offsets = new[] { 17L, SegmentSize * 3L + 91, SegmentSize * 8L + 7, SegmentSize * 2L + 511 };
        await using var stream = fixture.CreateStream(client);
        var buffer = new byte[ProbeSize];
        var stopwatch = Stopwatch.StartNew();
        var firstByte = TimeSpan.Zero;
        var bytesRead = 0L;

        foreach (var offset in offsets)
        {
            stream.Seek(offset, SeekOrigin.Begin);
            var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: true)
                .ConfigureAwait(false);
            if (bytesRead == 0) firstByte = stopwatch.Elapsed;
            Verify(fixture.Source.AsSpan((int)offset, read), buffer.AsSpan(0, read), "seek");
            bytesRead += read;
        }

        stopwatch.Stop();
        return new StreamingMetrics(stopwatch.Elapsed, firstByte, bytesRead, offsets.Length);
    }

    private static async Task WaitForCatalogAsync(SegmentCacheNntpClient client)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!client.IsCatalogReady && DateTime.UtcNow < deadline)
            await Task.Delay(10).ConfigureAwait(false);

        if (!client.IsCatalogReady)
            throw new TimeoutException("Segment cache catalog did not become ready.");
    }

    private static void Print(string scenario, ScenarioSnapshot snapshot, string? extra = null)
    {
        var deterministic = snapshot.Deterministic;
        var timing = snapshot.Timing;
        Console.WriteLine(
            $"{scenario} bytes={deterministic["bytes"]} transport_requests={deterministic["transportRequests"]} " +
            $"transport_bytes={deterministic["transportBytes"]} first_byte_ms={timing["firstByteMs"]:F3} " +
            $"elapsed_ms={timing["elapsedMs"]:F3} throughput_mib_s={timing["throughputMiBs"]:F3}" +
            (extra is null ? string.Empty : $" {extra}"));
    }

    private static void Verify(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string operation)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException($"Repeatable streaming {operation} read did not match its fixture.");
    }

    private static Fixture CreateFixture()
    {
        var source = new byte[SegmentSize * SegmentCount];
        new Random(1025).NextBytes(source);
        var segmentIds = Enumerable.Range(0, SegmentCount).Select(index => $"report-{index:D2}").ToArray();
        var ranges = Enumerable.Range(0, SegmentCount)
            .Select(index => new LongRange(index * SegmentSize, (index + 1L) * SegmentSize))
            .ToArray();
        var segments = segmentIds
            .Select((id, index) => KeyValuePair.Create(
                id, source.AsSpan(index * SegmentSize, SegmentSize).ToArray()))
            .ToDictionary();
        return new Fixture(
            source,
            segmentIds,
            ranges,
            segments,
            segmentIds.Zip(ranges).ToDictionary(pair => pair.First, pair => pair.Second));
    }

    private sealed record Fixture(
        byte[] Source,
        string[] SegmentIds,
        LongRange[] Ranges,
        IReadOnlyDictionary<string, byte[]> Segments,
        IReadOnlyDictionary<string, LongRange> RangesById)
    {
        public BenchmarkNntpClient CreateTransport() =>
            new(Segments, useCachedYencStreams: true, RangesById);

        public NzbFileStream CreateStream(INntpClient client) =>
            new(SegmentIds, Source.Length, client, articleBufferSize: 0, segmentByteRanges: Ranges,
                usePipelinedBodyRequests: false, fileName: "repeatable-streaming-benchmark.bin");

        public NzbFileStream CreateProductionShapedStream(INntpClient client) =>
            new(
                SegmentIds,
                Source.Length,
                client,
                articleBufferSize: ProductionArticleBufferSize,
                segmentByteRanges: Ranges,
                usePipelinedBodyRequests: true,
                fileName: "repeatable-streaming-pipelined-benchmark.bin",
                streamingBodyBatchWidth: ProductionBodyBatchWidth);
    }

    private readonly record struct StreamingMetrics(
        TimeSpan Elapsed,
        TimeSpan FirstByte,
        long BytesRead,
        int OperationCount);

    private readonly record struct DeadArticleCapture(ScenarioSnapshot Snapshot);
}
