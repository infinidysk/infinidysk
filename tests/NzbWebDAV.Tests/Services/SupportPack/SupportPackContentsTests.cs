using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Logging;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Services.SupportPack;
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
        Path.Combine(Path.GetTempPath(), $"nzbdav-support-{Guid.NewGuid():N}");

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
        try { Directory.Delete(_configRoot, recursive: true); } catch { /* best effort */ }
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

    private static async Task<Dictionary<string, string>> ReadPackEntriesAsync(
        LogBufferSink logBuffer,
        WarningLogBuffer warningBuffer)
    {
        var configManager = new ConfigManager();
        var websocketManager = new WebsocketManager();
        var usenet = new UsenetStreamingClient(
            configManager,
            websocketManager,
            new ProviderUsageTracker(),
            new MetricsWriter(),
            new ProviderBytesTracker(),
            new StreamTraceBuffer(100, enabled: false),
            new ActiveReadRegistry());

        var service = new SupportPackService(
            logBuffer,
            warningBuffer,
            configManager,
            new MetricsWriter(),
            new ProviderBytesTracker(),
            usenet,
            new ArticleMissNegativeCache(configManager),
            new InFlightArticleBudget(64 * 1024 * 1024));

        using var buffer = new MemoryStream();
        await service.WriteAsync(buffer, CancellationToken.None);

        buffer.Position = 0;
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
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
