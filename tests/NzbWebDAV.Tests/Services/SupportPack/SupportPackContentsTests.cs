using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Logging;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Diagnostics;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Services.SupportPack;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Websocket;
using Serilog;
using Serilog.Events;

namespace NzbWebDAV.Tests.Services.SupportPack;

[Collection(nameof(ConfigPathCollection))]
public sealed class SupportPackContentsTests : IDisposable
{
    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-support-{Guid.NewGuid():N}");

    private readonly string? _previousConfigPath;

    public SupportPackContentsTests()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(_configRoot);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configRoot);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        try { Directory.Delete(_configRoot, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task Pack_KeepsWarningsThatOverflowedTheMainLogBuffer()
    {
        // Mirrors the production wiring: one small all-level buffer plus a
        // Warning-and-above lane, both fed by the same logger.
        var mainSink = new LogBufferSink(10);
        var warningBuffer = new WarningLogBuffer(new LogBufferSink(500));
        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(mainSink)
            .WriteTo.Sink(warningBuffer.Sink, restrictedToMinimumLevel: LogEventLevel.Warning)
            .CreateLogger();

        logger.Warning("Streaming timeout executing nntp BODY command");
        for (var i = 0; i < 50; i++)
            logger.Debug("Watchtower: cycle starting {Index}", i);

        var entries = await ReadPackEntriesAsync(mainSink, warningBuffer);

        var backendLog = entries["logs/backend.log"];
        var warningsLog = entries["logs/warnings.log"];

        Assert.DoesNotContain("Streaming timeout", backendLog);
        Assert.Contains("Watchtower: cycle starting", backendLog);
        Assert.Contains("Streaming timeout executing nntp BODY command", warningsLog);
        Assert.DoesNotContain("Watchtower: cycle starting", warningsLog);

        using var manifest = JsonDocument.Parse(entries["manifest.json"]);
        var warnings = manifest.RootElement.GetProperty("warnings");
        Assert.Equal(1, warnings.GetProperty("count").GetInt32());
        Assert.Equal(500, warnings.GetProperty("capacity").GetInt32());
        Assert.Equal("included", manifest.RootElement.GetProperty("sections").GetProperty("warnings").GetString());
    }

    [Fact]
    public async Task Pack_ExplainsAnEmptyWarningLaneInsteadOfShippingAnEmptyFile()
    {
        var entries = await ReadPackEntriesAsync(new LogBufferSink(10), new WarningLogBuffer(new LogBufferSink(50)));

        // A zero-byte file reads as a broken export; say why it is empty.
        Assert.Contains("No warnings or errors", entries["logs/warnings.log"]);
    }

    [Fact]
    public async Task Pack_ReportsProcessUptimeRatherThanServiceConstructionTime()
    {
        var entries = await ReadPackEntriesAsync(new LogBufferSink(10), new WarningLogBuffer(new LogBufferSink(50)));

        using var environment = JsonDocument.Parse(entries["environment.json"]);
        var root = environment.RootElement;
        var generatedAt = root.GetProperty("generatedAtUtc").GetDateTimeOffset();
        var reportedStart = root.GetProperty("processStartedAtUtc").GetDateTimeOffset();
        var uptimeSeconds = root.GetProperty("uptimeSeconds").GetInt64();

        using var process = Process.GetCurrentProcess();
        var actualStart = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);

        // The service is constructed moments before the pack is written, so anything
        // measured from construction would report ~0. It must track the OS process.
        Assert.True(
            Math.Abs((reportedStart - actualStart).TotalSeconds) < 5,
            $"reported process start {reportedStart:O} should match {actualStart:O}");
        Assert.Equal((long)(generatedAt - reportedStart).TotalSeconds, uptimeSeconds);
    }

    [Fact]
    public async Task Pack_ReportsCorruptionTrackingCountsInPar2RepairSection()
    {
        var entries = await ReadPackEntriesAsync(new LogBufferSink(10), new WarningLogBuffer(new LogBufferSink(50)));

        using var environment = JsonDocument.Parse(entries["environment.json"]);
        var tracking = environment.RootElement.GetProperty("par2Repair").GetProperty("corruptionTracking");
        Assert.True(tracking.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, tracking.GetProperty("filesWithCorruptRecords").GetInt32());
        Assert.Equal(0, tracking.GetProperty("recordedCorruptSegments").GetInt32());
    }

    [Fact]
    public async Task Pack_ReportsNoActivePar2RepairWhenIdle()
    {
        var entries = await ReadPackEntriesAsync(new LogBufferSink(10), new WarningLogBuffer(new LogBufferSink(50)));

        using var environment = JsonDocument.Parse(entries["environment.json"]);
        var repair = environment.RootElement.GetProperty("par2Repair");
        Assert.True(repair.TryGetProperty("active", out var active));
        Assert.Equal(JsonValueKind.Null, active.ValueKind);
    }

    [Fact]
    public async Task Pack_ReportsHealthCheckGateAdmissionState()
    {
        var entries = await ReadPackEntriesAsync(
            new LogBufferSink(10),
            new WarningLogBuffer(new LogBufferSink(50)));

        using var environment = JsonDocument.Parse(entries["environment.json"]);
        var gate = environment.RootElement.GetProperty("healthCheckGate");
        Assert.Equal(1, gate.GetProperty("limit").GetInt32());
        Assert.Equal(0, gate.GetProperty("active").GetInt32());
        Assert.Equal(0, gate.GetProperty("waitingQueue").GetInt32());
        Assert.Equal(0, gate.GetProperty("waitingBackground").GetInt32());
    }

    [Fact]
    public async Task Pack_ReportsCpuGcAndThreadPoolCountersForBottleneckTriage()
    {
        var entries = await ReadPackEntriesAsync(new LogBufferSink(10), new WarningLogBuffer(new LogBufferSink(50)));

        using var environment = JsonDocument.Parse(entries["environment.json"]);
        var root = environment.RootElement;

        var cpu = root.GetProperty("cpu");
        Assert.True(cpu.GetProperty("processorCount").GetInt32() >= 1);
        Assert.True(cpu.GetProperty("lifetimeTotalMs").GetInt64() >= 0);

        // The on-demand probe is kept as a footnote, so it must still be readable.
        var onDemand = cpu.GetProperty("onDemandSample");
        Assert.True(onDemand.GetProperty("windowMs").GetInt64() > 0);
        Assert.True(onDemand.GetProperty("percentAllCores").GetDouble() >= 0);

        var gc = root.GetProperty("gc");
        Assert.True(gc.GetProperty("gen0Collections").GetInt32() >= 0);
        Assert.True(gc.GetProperty("gen2Collections").GetInt32() >= 0);
        Assert.True(gc.GetProperty("totalAllocatedBytes").GetInt64() > 0);
        Assert.True(gc.GetProperty("totalPauseDurationMs").GetInt64() >= 0);
        Assert.True(gc.TryGetProperty("isServerGc", out _));
        Assert.True(gc.GetProperty("heapLimitBytes").GetInt64() > 0);
        Assert.True(gc.TryGetProperty("regionRangeBytes", out _));
        Assert.True(gc.TryGetProperty("regionSizeBytes", out _));
        Assert.True(gc.TryGetProperty("index", out _));
        Assert.True(gc.TryGetProperty("generation", out _));
        Assert.True(gc.TryGetProperty("compacted", out _));
        Assert.True(gc.TryGetProperty("concurrent", out _));
        Assert.True(gc.TryGetProperty("memoryLoadBytes", out _));
        Assert.True(gc.TryGetProperty("highMemoryLoadThresholdBytes", out _));
        Assert.True(gc.TryGetProperty("totalAvailableMemoryBytes", out _));
        Assert.True(gc.TryGetProperty("conserveMemory", out _));
        Assert.True(gc.TryGetProperty("lohThresholdBytes", out _));
        Assert.True(gc.TryGetProperty("heapHardLimitLohBytes", out _));
        Assert.True(gc.TryGetProperty("heapHardLimitLohPercent", out _));
        Assert.False(gc.TryGetProperty("DOTNET_GCConserveMemory", out _));
        // Article buffers land on the large-object heap, so its size must be visible.
        var generations = gc.GetProperty("generations").EnumerateArray()
            .Select(entry => entry.GetProperty("name").GetString())
            .ToList();
        Assert.Contains("loh", generations);

        var threadPool = root.GetProperty("threadPool");
        Assert.True(threadPool.GetProperty("threadCount").GetInt32() >= 0);
        Assert.True(threadPool.GetProperty("pendingWorkItems").GetInt64() >= 0);
        Assert.True(threadPool.GetProperty("completedWorkItems").GetInt64() >= 0);

        var runtime = root.GetProperty("runtime");
        Assert.True(runtime.TryGetProperty("virtualMemoryBytes", out _));
        Assert.True(runtime.TryGetProperty("addressSpaceLimitBytes", out _));

        // No providers are configured in this fixture, so the section is present but empty.
        Assert.Equal(JsonValueKind.Array, root.GetProperty("connections").ValueKind);
    }

    [Fact]
    public async Task Pack_ReportsConcurrentReadAuditCounters()
    {
        var tracker = new ConcurrentReadTracker();
        using (var first = tracker.BeginRead("/movie.mkv", 0, ConcurrentReadRegion.StartRange))
        using (var firstFetch = tracker.BeginSegmentFetch("segment-1"))
        using (var second = tracker.BeginRead("/movie.mkv", 1_000_000, ConcurrentReadRegion.OffsetRange))
        using (var secondFetch = tracker.BeginSegmentFetch("segment-1"))
        {
            Assert.Equal(1, tracker.Snapshot().DuplicateInFlightSegmentFetches);
        }

        var entries = await ReadPackEntriesAsync(
            new LogBufferSink(10),
            new WarningLogBuffer(new LogBufferSink(50)),
            concurrentReadTracker: tracker);

        using var environment = JsonDocument.Parse(entries["environment.json"]);
        var runtime = environment.RootElement.GetProperty("runtime");
        Assert.Equal(2, runtime.GetProperty("concurrentReadStarts").GetInt64());
        Assert.Equal(1, runtime.GetProperty("concurrentReadOverlapEvents").GetInt64());
        Assert.Equal(
            1,
            runtime.GetProperty("concurrentReadDuplicateInFlightSegmentFetches").GetInt64());
        Assert.Equal(
            1_000_000,
            runtime.GetProperty("concurrentReadMaxStartDistanceBytes").GetInt64());
        Assert.Equal(0, runtime.GetProperty("sharedStreamAttachHits").GetInt64());
        Assert.True(runtime.TryGetProperty("sharedStreamRingRetainedBytes", out var ringBytes));
        Assert.Equal(0, ringBytes.GetInt64());
        Assert.True(runtime.TryGetProperty("sharedStreamRingRetainedBytesPeak", out _));
        Assert.True(runtime.TryGetProperty("sharedStreamRingLogicalBytes", out var logical));
        Assert.Equal(0, logical.GetInt64());
        Assert.True(runtime.TryGetProperty("sharedStreamPumpScratchRentedBytes", out _));
        Assert.True(runtime.TryGetProperty("sharedStreamRingConfiguredMaxBytes", out var configuredMax));
        Assert.True(configuredMax.GetInt64() > 0);
        Assert.True(runtime.TryGetProperty("sharedStreamLiveEntries", out _));
        Assert.True(runtime.TryGetProperty("sharedStreamReadyEntries", out _));
        Assert.True(runtime.TryGetProperty("sharedStreamDrainingEntries", out _));
        Assert.True(runtime.TryGetProperty("sharedStreamLaggingReaders", out _));
        Assert.True(runtime.TryGetProperty("sharedStreamPressureDetaches", out _));
        Assert.True(runtime.TryGetProperty("sharedStreamPressureReaps", out _));
    }

    [Fact]
    public async Task Pack_ReportsSegmentBufferPoolSnapshotWhenCustomPoolIsDefault()
    {
        var previous = PooledBufferStream.DefaultPool;
        var pool = new SegmentBufferPool(maxIdleBytes: 4 * 1024 * 1024);
        var buffer = pool.Rent(750_000);
        pool.Return(buffer);
        PooledBufferStream.DefaultPool = pool;
        try
        {
            var entries = await ReadPackEntriesAsync(
                new LogBufferSink(10),
                new WarningLogBuffer(new LogBufferSink(50)));

            using var environment = JsonDocument.Parse(entries["environment.json"]);
            var snapshot = environment.RootElement.GetProperty("segmentBufferPool");
            Assert.Equal(JsonValueKind.Object, snapshot.ValueKind);
            Assert.Equal(buffer.Length, snapshot.GetProperty("idleBytes").GetInt64());
            Assert.Equal(0, snapshot.GetProperty("checkedOutBytes").GetInt64());
            Assert.Equal(1, snapshot.GetProperty("rentCount").GetInt64());
            Assert.Equal(1, snapshot.GetProperty("returnCount").GetInt64());
            Assert.True(snapshot.TryGetProperty("trimmedBytes", out _));
            Assert.True(snapshot.TryGetProperty("maxIdleBytes", out var maxIdle));
            Assert.Equal(4 * 1024 * 1024, maxIdle.GetInt64());
            Assert.True(snapshot.TryGetProperty("staleExpiredBytes", out _));
            Assert.True(snapshot.TryGetProperty("classLimitDroppedBytes", out _));
            Assert.True(snapshot.TryGetProperty("capacityEvictedBytes", out _));
            Assert.True(snapshot.TryGetProperty("droppedTooLargeBytes", out _));
            Assert.True(snapshot.TryGetProperty("rejectedReturnCount", out _));
            Assert.True(snapshot.TryGetProperty("reuseCount", out _));
            Assert.True(snapshot.TryGetProperty("allocationCount", out _));
            Assert.True(snapshot.TryGetProperty("allocationAttemptCount", out _));
            Assert.True(snapshot.TryGetProperty("allocationFailureCount", out _));

            var sizeClass = Assert.Single(snapshot.GetProperty("sizeClasses").EnumerateArray());
            Assert.Equal(buffer.Length, sizeClass.GetProperty("bufferSize").GetInt32());
            Assert.Equal(1, sizeClass.GetProperty("bufferCount").GetInt32());
            Assert.Equal(buffer.Length, sizeClass.GetProperty("idleBytes").GetInt64());

            var lifetime = Assert.Single(snapshot.GetProperty("lifetimeSizeClasses").EnumerateArray());
            Assert.Equal(buffer.Length, lifetime.GetProperty("bufferSize").GetInt32());
            Assert.Equal(1, lifetime.GetProperty("rentCount").GetInt64());
            Assert.Equal(1, lifetime.GetProperty("returnCount").GetInt64());
            Assert.Equal(1, lifetime.GetProperty("allocationCount").GetInt64());
        }
        finally
        {
            PooledBufferStream.DefaultPool = previous;
        }
    }

    [Fact]
    public async Task Pack_ProjectsVersionedMemoryComponentsWithoutSensitiveIdentifiers()
    {
        var entries = await ReadPackEntriesAsync(
            new LogBufferSink(10),
            new WarningLogBuffer(new LogBufferSink(50)));

        using var environment = JsonDocument.Parse(entries["environment.json"]);
        var memory = environment.RootElement
            .GetProperty("runtime")
            .GetProperty("memoryComponents");

        Assert.Equal(1, memory.GetProperty("schemaVersion").GetInt32());
        Assert.True(memory.TryGetProperty("capturedAtUtc", out _));
        Assert.True(memory.TryGetProperty("inFlightArticles", out _));
        Assert.True(memory.TryGetProperty("sharedStreams", out _));
        Assert.True(memory.TryGetProperty("cacheWriter", out _));
        Assert.DoesNotContain("path", memory.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("message", memory.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider", memory.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pack_ReportsNullSegmentBufferPoolWhenOverrideUsesSharedPool()
    {
        var previous = PooledBufferStream.DefaultPool;
        PooledBufferStream.DefaultPool = SharedArrayPoolAdapter.Instance;
        try
        {
            var entries = await ReadPackEntriesAsync(
                new LogBufferSink(10),
                new WarningLogBuffer(new LogBufferSink(50)));

            using var environment = JsonDocument.Parse(entries["environment.json"]);
            Assert.Equal(
                JsonValueKind.Null,
                environment.RootElement.GetProperty("segmentBufferPool").ValueKind);
        }
        finally
        {
            PooledBufferStream.DefaultPool = previous;
        }
    }

    [Fact]
    public async Task Pack_ReportsPeakCpuAttributedToPlaybackRatherThanOnlyAnIdleSnapshot()
    {
        // Packs are collected after the symptom has passed, so an instantaneous sample
        // describes an idle process. These are the counters that span the incident.
        var busyAt = new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);
        var playbackAt = busyAt.AddMinutes(30);
        var runtimeUsage = new RuntimeUsageTracker(processorCount: 4);
        runtimeUsage.Record(
            TimeSpan.FromMilliseconds(16000),
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromSeconds(5),
            activeReads: 0,
            busyAt);
        runtimeUsage.Record(
            TimeSpan.FromMilliseconds(8000),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(5),
            activeReads: 2,
            playbackAt);

        var entries = await ReadPackEntriesAsync(
            new LogBufferSink(10),
            new WarningLogBuffer(new LogBufferSink(50)),
            runtimeUsage);

        using var environment = JsonDocument.Parse(entries["environment.json"]);
        var root = environment.RootElement;

        var sampler = root.GetProperty("runtimeSampler");
        Assert.True(sampler.GetProperty("intervalMs").GetInt64() > 0);
        Assert.Equal(2, sampler.GetProperty("sampleCount").GetInt64());
        Assert.Equal(10_000, sampler.GetProperty("windowSpanMs").GetInt64());
        Assert.Equal(playbackAt, sampler.GetProperty("lastSampleAtUtc").GetDateTimeOffset());

        var rolling = root.GetProperty("cpu").GetProperty("rolling");
        Assert.Equal(40, rolling.GetProperty("currentPercentAllCores").GetDouble());
        Assert.Equal(60, rolling.GetProperty("oneMinutePercentAllCores").GetDouble());

        var peak = rolling.GetProperty("peak");
        Assert.Equal(80, peak.GetProperty("percent").GetDouble());
        Assert.Equal(busyAt, peak.GetProperty("atUtc").GetDateTimeOffset());
        Assert.Equal(0, peak.GetProperty("activeReads").GetInt32());

        // Without the read count a peak cannot be told apart from a queue import or a
        // health sweep, which is what made the original figure unusable.
        var peakWhileReading = rolling.GetProperty("peakWhileReading");
        Assert.Equal(40, peakWhileReading.GetProperty("percent").GetDouble());
        Assert.Equal(playbackAt, peakWhileReading.GetProperty("atUtc").GetDateTimeOffset());
        Assert.Equal(2, peakWhileReading.GetProperty("activeReads").GetInt32());

        var gcRolling = root.GetProperty("gc").GetProperty("rolling");
        Assert.Equal(2, gcRolling.GetProperty("currentPausePercent").GetDouble());
        Assert.Equal(5, gcRolling.GetProperty("peak").GetProperty("percent").GetDouble());
        Assert.Equal(2, gcRolling.GetProperty("peakWhileReading").GetProperty("percent").GetDouble());
    }

    [Fact]
    public async Task Pack_ReportsRollingFiguresAsNullBeforeTheSamplerHasTicked()
    {
        var entries = await ReadPackEntriesAsync(new LogBufferSink(10), new WarningLogBuffer(new LogBufferSink(50)));

        using var environment = JsonDocument.Parse(entries["environment.json"]);
        var root = environment.RootElement;

        // A pack pulled in the first seconds of process life has nothing banked yet.
        // Null is honest here; zero would read as a genuinely idle backend.
        Assert.Equal(0, root.GetProperty("runtimeSampler").GetProperty("sampleCount").GetInt64());
        var rolling = root.GetProperty("cpu").GetProperty("rolling");
        Assert.Equal(JsonValueKind.Null, rolling.GetProperty("currentPercentAllCores").ValueKind);
        Assert.Equal(JsonValueKind.Null, rolling.GetProperty("peak").ValueKind);
        Assert.Equal(JsonValueKind.Null, rolling.GetProperty("peakWhileReading").ValueKind);
    }

    [Fact]
    public async Task Pack_IncludesStreamTracesWhileTracingIsEnabled()
    {
        var disabled = await ReadPackEntriesAsync(
            new LogBufferSink(10),
            new WarningLogBuffer(new LogBufferSink(50)),
            new StreamTraceBuffer(100, enabled: false));

        using (var disabledManifest = JsonDocument.Parse(disabled["manifest.json"]))
        {
            Assert.Equal(
                "disabled",
                disabledManifest.RootElement.GetProperty("sections").GetProperty("streamTraces").GetString());
        }

        Assert.False(disabled.ContainsKey("stream-traces/sessions.json"));
        Assert.False(disabled.ContainsKey("stream-traces/events.jsonl"));

        var buffer = new StreamTraceBuffer(100, enabled: false);
        buffer.EnableFor(TimeSpan.FromMinutes(15), 100, StreamTraceBuffer.SourceUi);
        var session = Guid.NewGuid();
        var range = buffer.RangeOpen(session, "/view/movie.mkv", "GET", 0, 99, 1000, "ua", "203.0.113.10");
        buffer.Seek(session, 50);
        buffer.Segment(session, "provider-a", SegmentFetch.FetchStatus.Ok, 12, 0, "msgid@a");
        buffer.RangeEnd(session, range, ReadSession.EndReasonCode.Completed, 100);

        var enabled = await ReadPackEntriesAsync(
            new LogBufferSink(10),
            new WarningLogBuffer(new LogBufferSink(50)),
            buffer);

        using var enabledManifest = JsonDocument.Parse(enabled["manifest.json"]);
        Assert.Equal(5, enabledManifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            "included",
            enabledManifest.RootElement.GetProperty("sections").GetProperty("streamTraces").GetString());
        Assert.False(enabledManifest.RootElement.GetProperty("streamTraces").GetProperty("overflowed").GetBoolean());

        Assert.Contains("/view/movie.mkv", enabled["stream-traces/sessions.json"]);
        Assert.Contains("RangeOpen", enabled["stream-traces/events.jsonl"]);
        Assert.Contains("Seek", enabled["stream-traces/events.jsonl"]);
        Assert.Contains("[IP-", enabled["stream-traces/events.jsonl"]);
        Assert.DoesNotContain("203.0.113.10", enabled["stream-traces/events.jsonl"]);

        using var environment = JsonDocument.Parse(enabled["environment.json"]);
        var tracing = environment.RootElement.GetProperty("streamTracing");
        Assert.True(tracing.GetProperty("enabled").GetBoolean());
        Assert.Equal("ui", tracing.GetProperty("source").GetString());
        Assert.True(tracing.GetProperty("expiresAtUnixMs").GetInt64() > 0);
        Assert.True(tracing.GetProperty("eventCount").GetInt64() >= 4);
        Assert.False(tracing.GetProperty("overflowed").GetBoolean());
    }

    [Fact]
    public async Task Pack_FlagsTruncatedStreamTraceCaptures()
    {
        var buffer = new StreamTraceBuffer(100, enabled: false);
        buffer.EnableFor(TimeSpan.FromMinutes(15), 100, StreamTraceBuffer.SourceUi);
        var session = Guid.NewGuid();
        for (var i = 0; i < 150; i++)
            buffer.Seek(session, i);

        var pack = await ReadPackEntriesAsync(
            new LogBufferSink(10),
            new WarningLogBuffer(new LogBufferSink(50)),
            buffer);

        Assert.True(pack.ContainsKey("stream-traces/OVERFLOW.txt"));
        Assert.Contains("INCOMPLETE", pack["stream-traces/OVERFLOW.txt"]);

        using var manifest = JsonDocument.Parse(pack["manifest.json"]);
        Assert.Equal(5, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            "included-truncated",
            manifest.RootElement.GetProperty("sections").GetProperty("streamTraces").GetString());
        var streamTraces = manifest.RootElement.GetProperty("streamTraces");
        Assert.True(streamTraces.GetProperty("overflowed").GetBoolean());
        Assert.Equal(150, streamTraces.GetProperty("eventCount").GetInt64());
        Assert.Equal(100, streamTraces.GetProperty("retainedEventCount").GetInt64());
        Assert.Equal(50, streamTraces.GetProperty("overwrittenEventCount").GetInt64());

        using var sessions = JsonDocument.Parse(pack["stream-traces/sessions.json"]);
        Assert.True(sessions.RootElement.GetArrayLength() >= 1);
        Assert.Contains("retainedEventCount", pack["stream-traces/sessions.json"]);
        Assert.Contains("eventsComplete", pack["stream-traces/sessions.json"]);
    }

    [Fact]
    public async Task Pack_IncludesEffectiveStreamingConfigAndSegmentCacheMetrics()
    {
        var statistics = new SegmentCacheStatistics();
        statistics.BeginGeneration(enabled: true, maxBytes: 2048);
        statistics.RecordHit(32);
        statistics.RecordMiss();
        statistics.RecordBatchBypass(4);
        var generation = statistics.BeginGeneration(enabled: true, maxBytes: 4096);
        generation.SetCatalogReady(15, entries: 2, currentBytes: 64);

        var entries = await ReadPackEntriesAsync(
            new LogBufferSink(10),
            new WarningLogBuffer(new LogBufferSink(50)),
            segmentCacheStatistics: statistics);

        Assert.True(entries.ContainsKey("configuration-effective-streaming.json"));
        Assert.True(entries.ContainsKey("metrics/segment-cache.json"));
        Assert.Contains("public benchmark extraction", entries["README.txt"]);

        using var effective = JsonDocument.Parse(entries["configuration-effective-streaming.json"]);
        Assert.Equal(1, effective.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(40, effective.RootElement.GetProperty("streaming").GetProperty("articleBufferSize").GetInt32());
        Assert.True(effective.RootElement.GetProperty("streaming").GetProperty("pipelinedBodyRequests").GetBoolean());
        Assert.Equal(4, effective.RootElement.GetProperty("streaming").GetProperty("bodyBatchWidth").GetInt32());
        Assert.False(effective.RootElement.GetProperty("segmentCache").GetProperty("enabled").GetBoolean());
        Assert.Equal(
            "default",
            effective.RootElement.GetProperty("source").GetProperty("streaming").GetProperty("articleBufferSize").GetString());
        Assert.DoesNotContain("/config/segment-cache", entries["configuration-effective-streaming.json"]);

        using var cache = JsonDocument.Parse(entries["metrics/segment-cache.json"]);
        Assert.Equal(1, cache.RootElement.GetProperty("schemaVersion").GetInt32());
        var snapshot = cache.RootElement.GetProperty("cache");
        Assert.True(snapshot.GetProperty("enabled").GetBoolean());
        Assert.True(snapshot.GetProperty("catalogReady").GetBoolean());
        Assert.Equal(15, snapshot.GetProperty("catalogLoadDurationMs").GetInt64());
        Assert.Equal(2, snapshot.GetProperty("entries").GetInt64());
        Assert.Equal(64, snapshot.GetProperty("currentBytes").GetInt64());
        Assert.Equal(4096, snapshot.GetProperty("maxBytes").GetInt64());
        Assert.Equal(1, snapshot.GetProperty("hits").GetInt64());
        Assert.Equal(32, snapshot.GetProperty("bytesServed").GetInt64());
        Assert.Equal(1, snapshot.GetProperty("misses").GetInt64());
        Assert.Equal(1, snapshot.GetProperty("batchBypassRequests").GetInt64());
        Assert.Equal(4, snapshot.GetProperty("batchBypassArticles").GetInt64());
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("queuedWriteBytes").ValueKind);

        using var manifest = JsonDocument.Parse(entries["manifest.json"]);
        Assert.Equal(5, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            "included",
            manifest.RootElement.GetProperty("sections").GetProperty("configurationEffectiveStreaming").GetString());
        Assert.Equal(
            "included",
            manifest.RootElement.GetProperty("sections").GetProperty("segmentCache").GetString());

        var offenders = new List<string>();
        CollectNonCamelCaseNames(effective.RootElement, "configuration-effective-streaming.json", offenders);
        CollectNonCamelCaseNames(cache.RootElement, "metrics/segment-cache.json", offenders);
        Assert.True(offenders.Count == 0, string.Join(", ", offenders));
    }

    [Fact]
    public async Task Pack_EffectiveStreamingConfigOmitsSentinelSecrets()
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue = JsonSerializer.Serialize(new UsenetProviderConfig
                {
                    Providers =
                    [
                        new UsenetProviderConfig.ConnectionDetails
                        {
                            ProviderId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                            Type = ProviderType.Pooled,
                            Host = "news.sentinel.example",
                            Port = 563,
                            UseSsl = true,
                            User = "sentinel-user",
                            Pass = "sentinel-pass",
                            MaxConnections = 8,
                            StorageGroup = "sentinel-group",
                        },
                    ],
                }),
            },
            new ConfigItem { ConfigName = ConfigKeys.UsenetSegmentCachePath, ConfigValue = "/tmp/sentinel-cache" },
            new ConfigItem { ConfigName = ConfigKeys.ApiKey, ConfigValue = "sentinel-api-key" },
            new ConfigItem { ConfigName = ConfigKeys.WebdavPass, ConfigValue = "sentinel-webdav" },
        ]);

        var logBuffer = new LogBufferSink(10);
        using var logger = new LoggerConfiguration().WriteTo.Sink(logBuffer).CreateLogger();
        logger.Information("Provider authentication failed for sentinel-user with sentinel-pass");
        var entries = await ReadPackEntriesAsync(
            logBuffer,
            new WarningLogBuffer(new LogBufferSink(50)),
            configManager: configManager);

        var effective = entries["configuration-effective-streaming.json"];
        var cache = entries["metrics/segment-cache.json"];
        foreach (var forbidden in new[]
                 {
                     "news.sentinel.example", "sentinel-user", "sentinel-pass",
                     "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "sentinel-group",
                     "/tmp/sentinel-cache", "sentinel-api-key", "sentinel-webdav",
                     "<msg@example>",
                 })
        {
            Assert.DoesNotContain(forbidden, effective);
            Assert.DoesNotContain(forbidden, cache);
        }

        foreach (var content in entries.Values)
        {
            Assert.DoesNotContain("sentinel-user", content);
            Assert.DoesNotContain("sentinel-pass", content);
        }
    }

    [Fact]
    public async Task Pack_JsonArtifacts_RemainValidWithPathologicalSecrets()
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.ApiKey, ConfigValue = "true" },
            new ConfigItem { ConfigName = ConfigKeys.ApiStrmKey, ConfigValue = "null" },
            new ConfigItem { ConfigName = ConfigKeys.WebdavPass, ConfigValue = "schema" },
            new ConfigItem { ConfigName = ConfigKeys.RclonePass, ConfigValue = "enabled" },
            new ConfigItem { ConfigName = ConfigKeys.WatchtowerProfileToken, ConfigValue = "203.0.113.50" },
            // Explicitly on so the boolean "enabled" property stays true and proves the
            // redactor does not rewrite it when a secret value happens to equal "enabled".
            new ConfigItem { ConfigName = ConfigKeys.UsenetSegmentCacheEnabled, ConfigValue = "true" },
        ]);

        var entries = await ReadPackEntriesAsync(
            new LogBufferSink(10),
            new WarningLogBuffer(new LogBufferSink(50)),
            configManager: configManager);

        foreach (var (name, content) in entries)
        {
            if (!name.EndsWith(".json", StringComparison.Ordinal))
                continue;

            using var document = JsonDocument.Parse(content);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        }

        using var effective = JsonDocument.Parse(entries["configuration-effective-streaming.json"]);
        Assert.Equal(1, effective.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            JsonValueKind.True,
            effective.RootElement.GetProperty("streaming").GetProperty("pipelinedBodyRequests").ValueKind);
        Assert.Equal(
            JsonValueKind.True,
            effective.RootElement.GetProperty("segmentCache").GetProperty("enabled").ValueKind);

        using var manifest = JsonDocument.Parse(entries["manifest.json"]);
        Assert.Equal(
            "included",
            manifest.RootElement.GetProperty("sections").GetProperty("configurationEffectiveStreaming").GetString());
        Assert.Equal(
            "included",
            manifest.RootElement.GetProperty("sections").GetProperty("segmentCache").GetString());
    }

    [Fact]
    public async Task Pack_IncludesRetainedStreamTracesUntilDiscarded()
    {
        var buffer = new StreamTraceBuffer(100, enabled: false);
        buffer.EnableFor(TimeSpan.FromMinutes(15), 100, StreamTraceBuffer.SourceUi);
        var session = Guid.NewGuid();
        buffer.RangeOpen(session, "/view/retained.mkv", "GET", 0, 99, 1000, null, null);
        buffer.StopRecording();

        var retained = await ReadPackEntriesAsync(
            new LogBufferSink(10),
            new WarningLogBuffer(new LogBufferSink(50)),
            buffer);

        using (var manifest = JsonDocument.Parse(retained["manifest.json"]))
        {
            Assert.Equal(
                "included",
                manifest.RootElement.GetProperty("sections").GetProperty("streamTraces").GetString());
        }

        Assert.Contains("/view/retained.mkv", retained["stream-traces/sessions.json"]);
        Assert.Contains("RangeOpen", retained["stream-traces/events.jsonl"]);

        using (var environment = JsonDocument.Parse(retained["environment.json"]))
        {
            var tracing = environment.RootElement.GetProperty("streamTracing");
            Assert.False(tracing.GetProperty("enabled").GetBoolean());
            Assert.True(tracing.GetProperty("retained").GetBoolean());
            Assert.True(tracing.GetProperty("retainedUntilUnixMs").GetInt64() > 0);
            Assert.True(tracing.GetProperty("eventCount").GetInt64() > 0);
        }

        buffer.Discard();
        var discarded = await ReadPackEntriesAsync(
            new LogBufferSink(10),
            new WarningLogBuffer(new LogBufferSink(50)),
            buffer);

        using var discardedManifest = JsonDocument.Parse(discarded["manifest.json"]);
        Assert.Equal(
            "disabled",
            discardedManifest.RootElement.GetProperty("sections").GetProperty("streamTraces").GetString());
        Assert.False(discarded.ContainsKey("stream-traces/sessions.json"));
        Assert.False(discarded.ContainsKey("stream-traces/events.jsonl"));
    }

    [Fact]
    public async Task Pack_FlagsAnUntracedFreshCaptureAsLowQuality()
    {
        var entries = await ReadPackEntriesAsync(
            new LogBufferSink(10),
            new WarningLogBuffer(new LogBufferSink(50)));

        using var manifest = JsonDocument.Parse(entries["manifest.json"]);
        var packQuality = manifest.RootElement.GetProperty("packQuality")
            .EnumerateArray().Select(x => x.GetString()).ToList();

        Assert.Contains(packQuality, w => w!.Contains("No stream traces"));
        Assert.Contains(packQuality, w => w!.Contains("sampler window"));
    }

    [Fact]
    public async Task Pack_FlagsAWrappedLogBuffer()
    {
        var logBuffer = new LogBufferSink(2);
        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(logBuffer)
            .CreateLogger();
        for (var i = 0; i < 5; i++) logger.Information("entry {Index}", i);

        var entries = await ReadPackEntriesAsync(
            logBuffer,
            new WarningLogBuffer(new LogBufferSink(50)));

        using var manifest = JsonDocument.Parse(entries["manifest.json"]);
        var packQuality = manifest.RootElement.GetProperty("packQuality")
            .EnumerateArray().Select(x => x.GetString()).ToList();

        Assert.Contains(packQuality, w => w!.Contains("ring buffer has wrapped"));
    }

    [Fact]
    public async Task Pack_OmitsTracingAndSamplerWarningsForAHealthyCapture()
    {
        var buffer = new StreamTraceBuffer(100, enabled: false);
        buffer.EnableFor(TimeSpan.FromMinutes(15), 100, StreamTraceBuffer.SourceUi);
        buffer.RangeOpen(Guid.NewGuid(), "/view/movie.mkv", "GET", 0, 99, 1000, "ua", null);

        var runtimeUsage = new RuntimeUsageTracker(processorCount: 4);
        var at = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 13; i++)
        {
            runtimeUsage.Record(
                TimeSpan.FromMilliseconds(1000),
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromSeconds(5),
                activeReads: 1,
                at.AddSeconds(5.0 * i));
        }

        var entries = await ReadPackEntriesAsync(
            new LogBufferSink(10),
            new WarningLogBuffer(new LogBufferSink(50)),
            buffer,
            runtimeUsage);

        using var manifest = JsonDocument.Parse(entries["manifest.json"]);
        var packQuality = manifest.RootElement.GetProperty("packQuality")
            .EnumerateArray().Select(x => x.GetString()).ToList();

        Assert.DoesNotContain(packQuality, w => w!.Contains("No stream traces"));
        Assert.DoesNotContain(packQuality, w => w!.Contains("sampler window"));
    }

    [Fact]
    public async Task Pack_UsesCamelCaseFieldNamesThroughout()
    {
        var buffer = new StreamTraceBuffer(100, enabled: false);
        buffer.EnableFor(TimeSpan.FromMinutes(15), 100, StreamTraceBuffer.SourceUi);
        var session = Guid.NewGuid();
        var range = buffer.RangeOpen(session, "/view/movie.mkv", "GET", 0, 99, 1000, "ua", null);
        buffer.Segment(session, "provider-a", SegmentFetch.FetchStatus.Ok, 12, 0, "msgid@a");
        buffer.RangeEnd(session, range, ReadSession.EndReasonCode.Completed, 100);

        // Migrate the metrics database so metrics/recent.json is produced and the
        // recursive walk covers the metrics section too (providerHours/connections
        // stay empty without seeded providers, but metricsHealth and the other
        // subsections are populated and get checked).
        MetricsDbContext.ResetOptionsForTests();
        List<string> offenders;
        try
        {
            await using (var metricsDb = new MetricsDbContext())
                await metricsDb.Database.MigrateAsync();

            var entries = await ReadPackEntriesAsync(
                new LogBufferSink(10),
                new WarningLogBuffer(new LogBufferSink(50)),
                buffer);

            offenders = new List<string>();
            foreach (var (name, content) in entries)
            {
                if (name.EndsWith(".json", StringComparison.Ordinal))
                {
                    using var doc = JsonDocument.Parse(content);
                    CollectNonCamelCaseNames(doc.RootElement, name, offenders);
                }
                else if (name.EndsWith(".jsonl", StringComparison.Ordinal))
                {
                    foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        using var doc = JsonDocument.Parse(line);
                        CollectNonCamelCaseNames(doc.RootElement, name, offenders);
                    }
                }
            }
        }
        finally
        {
            MetricsDbContext.ResetOptionsForTests();
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public async Task Pack_RenamesPreviouslyMixedSectionsToCamelCase()
    {
        // metricsHealth lives in metrics/recent.json, which is only written once the
        // metrics database has its tables; migrate so the section is produced here.
        MetricsDbContext.ResetOptionsForTests();
        try
        {
            await using (var metricsDb = new MetricsDbContext())
                await metricsDb.Database.MigrateAsync();

            var entries = await ReadPackEntriesAsync(
                new LogBufferSink(10),
                new WarningLogBuffer(new LogBufferSink(50)));

            // manifest ring-buffer stats were PascalCase shorthand (OldestSequence/NewestSequence).
            using var manifest = JsonDocument.Parse(entries["manifest.json"]);
            var logs = manifest.RootElement.GetProperty("logs");
            Assert.True(logs.TryGetProperty("oldestSequence", out _));
            Assert.False(logs.TryGetProperty("OldestSequence", out _));
            var warnings = manifest.RootElement.GetProperty("warnings");
            Assert.True(warnings.TryGetProperty("oldestSequence", out _));
            Assert.True(warnings.TryGetProperty("newestSequence", out _));

            // metricsHealth mixed queued/dropped with LastSuccessfulFlushAtMs/LastFlushError.
            using var metrics = JsonDocument.Parse(entries["metrics/recent.json"]);
            var metricsHealth = metrics.RootElement.GetProperty("metricsHealth");
            Assert.True(metricsHealth.TryGetProperty("lastSuccessfulFlushAtMs", out _));
            Assert.False(metricsHealth.TryGetProperty("LastSuccessfulFlushAtMs", out _));
            Assert.True(metricsHealth.TryGetProperty("lastFlushError", out _));
        }
        finally
        {
            MetricsDbContext.ResetOptionsForTests();
        }
    }

    [Fact]
    public async Task Pack_IncludesCircuitTransitionDiagnostics()
    {
        MetricsDbContext.ResetOptionsForTests();
        try
        {
            await using (var metricsDb = new MetricsDbContext())
            {
                await metricsDb.Database.MigrateAsync();
                metricsDb.MetricEvents.Add(new MetricEvent
                {
                    At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Kind = "circuit",
                    Tag1 = "provider-a",
                    Tag2 = "open",
                    Num = 60_000,
                    Note = """
                    {"failureReason":"connection failure (get-connection-CouldNotConnectToUsenetException)","pool":{"liveConnections":25,"idleConnections":23,"activeConnections":2}}
                    """,
                });
                await metricsDb.SaveChangesAsync();
            }

            var entries = await ReadPackEntriesAsync(
                new LogBufferSink(10),
                new WarningLogBuffer(new LogBufferSink(50)));

            using var metrics = JsonDocument.Parse(entries["metrics/recent.json"]);
            var transition = metrics.RootElement
                .GetProperty("circuits")
                .GetProperty("transitions")
                .EnumerateArray()
                .Single(item => item.GetProperty("providerKey").GetString() == "provider-a");
            var diagnostics = transition.GetProperty("diagnostics");

            Assert.Equal(
                "connection failure (get-connection-CouldNotConnectToUsenetException)",
                diagnostics.GetProperty("failureReason").GetString());
            Assert.Equal(25, diagnostics.GetProperty("pool").GetProperty("liveConnections").GetInt32());
            Assert.Equal(23, diagnostics.GetProperty("pool").GetProperty("idleConnections").GetInt32());
            Assert.Equal(2, diagnostics.GetProperty("pool").GetProperty("activeConnections").GetInt32());
        }
        finally
        {
            MetricsDbContext.ResetOptionsForTests();
        }
    }

    private static void CollectNonCamelCaseNames(JsonElement element, string path, List<string> offenders)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    // The environment map is keyed by environment-variable NAMES (LOG_LEVEL,
                    // TZ, …) — opaque data keys the serializer intentionally leaves as-is
                    // (no DictionaryKeyPolicy), not field names subject to the casing policy.
                    var isOpaqueDataKey = path.Equals("environment.json/environment", StringComparison.Ordinal);
                    if (!isOpaqueDataKey && !IsCamelCase(property.Name))
                        offenders.Add($"{path}: {property.Name}");
                    CollectNonCamelCaseNames(property.Value, $"{path}/{property.Name}", offenders);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectNonCamelCaseNames(item, path, offenders);

                break;
        }
    }

    private static bool IsCamelCase(string name) =>
        name.Length > 0
        && char.IsLower(name[0])
        && name.All(char.IsLetterOrDigit);

    private static Task<Dictionary<string, string>> ReadPackEntriesAsync(
        LogBufferSink logBuffer,
        WarningLogBuffer warningBuffer,
        RuntimeUsageTracker? runtimeUsage = null,
        ConcurrentReadTracker? concurrentReadTracker = null,
        SegmentCacheStatistics? segmentCacheStatistics = null,
        ConfigManager? configManager = null) =>
        ReadPackEntriesAsync(
            logBuffer,
            warningBuffer,
            new StreamTraceBuffer(100, enabled: false),
            runtimeUsage,
            concurrentReadTracker,
            segmentCacheStatistics,
            configManager);

    private static async Task<Dictionary<string, string>> ReadPackEntriesAsync(
        LogBufferSink logBuffer,
        WarningLogBuffer warningBuffer,
        StreamTraceBuffer streamTraceBuffer,
        RuntimeUsageTracker? runtimeUsage = null,
        ConcurrentReadTracker? concurrentReadTracker = null,
        SegmentCacheStatistics? segmentCacheStatistics = null,
        ConfigManager? configManager = null)
    {
        configManager ??= new ConfigManager();
        var websocketManager = new WebsocketManager();
        var activeReads = new ActiveReadRegistry();
        var usenet = new UsenetStreamingClient(
            configManager,
            websocketManager,
            new ProviderUsageTracker(),
            new MetricsWriter(),
            new ProviderBytesTracker(),
            streamTraceBuffer,
            activeReads);

        using var gcDiagnosticsStore = new GcDiagnosticsStore();
        var repairDir = Path.Join(Path.GetTempPath(), "nzbdav-support-test-" + Guid.NewGuid().ToString("N"));
        var repairPatchStore = new RepairPatchStore(repairDir, 1024 * 1024);
        await repairPatchStore.EnsureCatalogLoadedAsync(CancellationToken.None);
        var par2RepairService = new Par2RepairService(configManager, usenet, repairPatchStore);
        using var healthCheckConnectionGate = new HealthCheckConnectionGate(configManager);
        var budget = new InFlightArticleBudget(64 * 1024 * 1024);
        var cacheStatistics = segmentCacheStatistics ?? new SegmentCacheStatistics();
        var snapshotBuilder = new MemoryComponentSnapshotBuilder(
            budget,
            configManager,
            concurrentReadTracker ?? new ConcurrentReadTracker(configManager: configManager),
            activeReads,
            cacheStatistics);
        var service = new SupportPackService(
            logBuffer,
            warningBuffer,
            configManager,
            new MetricsWriter(),
            new ProviderBytesTracker(),
            new ProviderLatencyTracker(),
            usenet,
            new ArticleMissNegativeCache(configManager),
            budget,
            streamTraceBuffer,
            runtimeUsage ?? new RuntimeUsageTracker(),
            gcDiagnosticsStore,
            par2RepairService,
            repairPatchStore,
            healthCheckConnectionGate,
            concurrentReadTracker,
            queueCoordinator: null,
            cacheStatistics,
            snapshotBuilder);

        using var memory = new MemoryStream();
        await service.WriteAsync(memory, CancellationToken.None);

        memory.Position = 0;
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            await using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            entries[entry.FullName] = await reader.ReadToEndAsync();
        }

        return entries;
    }
}
