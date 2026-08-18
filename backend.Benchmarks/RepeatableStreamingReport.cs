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

    public static async Task RunAsync()
    {
        var fixture = CreateFixture();
        var cacheDir = Path.Join(Path.GetTempPath(), "nzbdav-repeatable-streaming-" + Guid.NewGuid().ToString("N"));

        try
        {
            using var transport = fixture.CreateTransport();
            using var cached = new SegmentCacheNntpClient(
                transport,
                cacheDir,
                maxBytes: fixture.Source.Length * 2L);
            await WaitForCatalogAsync(cached).ConfigureAwait(false);

            var cold = await ReadAllAsync(transport, fixture).ConfigureAwait(false);
            Print("cold-sequential", cold, fixture.Source.Length, transport.BodyRequestCount,
                transport.BodyBytesRequested);

            var requestsBeforePrime = transport.BodyRequestCount;
            var bytesBeforePrime = transport.BodyBytesRequested;
            var cachePrime = await PrimeCacheAsync(cached, fixture).ConfigureAwait(false);
            Print("cache-prime", cachePrime, fixture.Source.Length,
                transport.BodyRequestCount - requestsBeforePrime,
                transport.BodyBytesRequested - bytesBeforePrime);

            var requestsBeforeWarmRead = transport.BodyRequestCount;
            var bytesBeforeWarmRead = transport.BodyBytesRequested;
            var warm = await ReadAllAsync(cached, fixture).ConfigureAwait(false);
            Print("warm-reread", warm, fixture.Source.Length,
                transport.BodyRequestCount - requestsBeforeWarmRead,
                transport.BodyBytesRequested - bytesBeforeWarmRead);

            var requestsBeforeRangeProbe = transport.BodyRequestCount;
            var bytesBeforeRangeProbe = transport.BodyBytesRequested;
            var range = await ProbeAsync(cached, fixture, SegmentSize * 4L + 137, ProbeSize).ConfigureAwait(false);
            Print("range-probe", range, ProbeSize,
                transport.BodyRequestCount - requestsBeforeRangeProbe,
                transport.BodyBytesRequested - bytesBeforeRangeProbe);

            var requestsBeforeTailProbe = transport.BodyRequestCount;
            var bytesBeforeTailProbe = transport.BodyBytesRequested;
            var tail = await ProbeAsync(cached, fixture, fixture.Source.Length - ProbeSize, ProbeSize).ConfigureAwait(false);
            Print("tail-probe", tail, ProbeSize,
                transport.BodyRequestCount - requestsBeforeTailProbe,
                transport.BodyBytesRequested - bytesBeforeTailProbe);

            var requestsBeforeSeeks = transport.BodyRequestCount;
            var bytesBeforeSeeks = transport.BodyBytesRequested;
            var seeks = await SeekAsync(cached, fixture).ConfigureAwait(false);
            Print("seeks", seeks, seeks.BytesRead,
                transport.BodyRequestCount - requestsBeforeSeeks,
                transport.BodyBytesRequested - bytesBeforeSeeks,
                extra: $"seek_count={seeks.OperationCount}");

            var dead = await DeadArticleAsync(fixture).ConfigureAwait(false);
            Print("dead-article", dead.Metrics, dead.ZeroFilledBytes, dead.TransportRequests,
                dead.TransportBytes, $"zero_filled_bytes={dead.ZeroFilledBytes}");
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
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

    private static async Task<DeadArticleResult> DeadArticleAsync(Fixture fixture)
    {
        var missingSegment = fixture.SegmentIds[5];
        using var transport = new BenchmarkNntpClient(
            fixture.Segments.Where(pair => pair.Key != missingSegment).ToDictionary(),
            useCachedYencStreams: true,
            fixture.RangesById);
        await using var stream = fixture.CreateStream(transport);
        stream.Seek(SegmentSize * 5L, SeekOrigin.Begin);
        var buffer = new byte[SegmentSize];
        var stopwatch = Stopwatch.StartNew();
        var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: true).ConfigureAwait(false);
        stopwatch.Stop();

        if (buffer.AsSpan(0, read).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidOperationException("Dead-article probe did not return an all-zero gap.");

        return new DeadArticleResult(
            new StreamingMetrics(stopwatch.Elapsed, stopwatch.Elapsed, read, OperationCount: 1),
            read,
            transport.BodyRequestCount,
            transport.BodyBytesRequested);
    }

    private static async Task WaitForCatalogAsync(SegmentCacheNntpClient client)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!client.IsCatalogReady && DateTime.UtcNow < deadline)
            await Task.Delay(10).ConfigureAwait(false);

        if (!client.IsCatalogReady)
            throw new TimeoutException("Segment cache catalog did not become ready.");
    }

    private static void Print(
        string scenario,
        StreamingMetrics metrics,
        long bytes,
        int transportRequests,
        long transportBytes,
        string? extra = null)
    {
        var seconds = Math.Max(metrics.Elapsed.TotalSeconds, double.Epsilon);
        var throughput = bytes / 1024d / 1024d / seconds;
        Console.WriteLine(
            $"{scenario} bytes={bytes} transport_requests={transportRequests} " +
            $"transport_bytes={transportBytes} first_byte_ms={metrics.FirstByte.TotalMilliseconds:F3} " +
            $"elapsed_ms={metrics.Elapsed.TotalMilliseconds:F3} throughput_mib_s={throughput:F3}" +
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
    }

    private readonly record struct StreamingMetrics(
        TimeSpan Elapsed,
        TimeSpan FirstByte,
        long BytesRead,
        int OperationCount);

    private readonly record struct DeadArticleResult(
        StreamingMetrics Metrics,
        long ZeroFilledBytes,
        int TransportRequests,
        long TransportBytes);
}
