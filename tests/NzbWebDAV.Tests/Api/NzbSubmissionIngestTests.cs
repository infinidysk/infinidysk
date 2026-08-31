using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Api.SabControllers.AddFile;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Tests.Utils;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Api;

/// <summary>
/// Submission-ingest bounds: the decompressed 256 MiB limit is enforced while
/// the blob is copied (not after), validation runs before any NZB backup, and
/// rejected submissions leave neither blobs nor backup files.
/// </summary>
[Collection(nameof(ConfigPathCollection))]
public sealed class NzbSubmissionIngestTests : IAsyncLifetime
{
    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-ingest-cfg-{Guid.NewGuid():N}");
    private string? _previousConfigPath;
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _dbClient = null!;
    private QueueManager _queueManager = null!;
    private ConfigManager _configManager = null!;
    private WebsocketManager _websocketManager = null!;

    public async Task InitializeAsync()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(_configRoot);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configRoot);

        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={DavDatabaseContext.DatabaseFilePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        _context = new DavDatabaseContext(options);
        await _context.Database.MigrateAsync();
        _dbClient = new DavDatabaseClient(_context);

        _configManager = new ConfigManager();
        _configManager.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue = JsonSerializer.Serialize(new UsenetProviderConfig()),
            },
        ]);

        _websocketManager = new WebsocketManager();
        var usenet = new UsenetStreamingClient(
            _configManager,
            _websocketManager,
            new ProviderUsageTracker(),
            new MetricsWriter(),
            new ProviderBytesTracker(),
            new StreamTraceBuffer(100),
            new ActiveReadRegistry());
        _queueManager = new QueueManager(
            usenet,
            _configManager,
            _websocketManager,
            new ProviderUsageTracker(),
            new WatchdogLog(),
            new QueueItemSourceTracker(),
            new BenchmarkGate());
    }

    public async Task DisposeAsync()
    {
        _queueManager.Dispose();
        await _context.DisposeAsync();
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        try { Directory.Delete(_configRoot, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task SubmitAsync_OversizeDecompressedXml_FailsDuringCopyAndLeavesNothing()
    {
        var assignedId = Guid.NewGuid();
        var oversize = (long)NzbInputLimits.Default.MaxXmlBytes + 1;
        var controller = CreateController();

        var ex = await Assert.ThrowsAsync<ApiValidationException>(() =>
            controller.AddFileAsync(CreateRequest(
                "oversize.nzb", assignedId, new LimitedReadStreamTests.RepeatingByteStream(oversize))));

        Assert.Contains("maximum allowed size", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(BlobStore.ReadBlob(assignedId));
        Assert.False(await _context.QueueItems.AsNoTracking().AnyAsync(q => q.Id == assignedId));
        Assert.Empty(Directory.GetFiles(_configRoot, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SubmitAsync_GzipBomb_EnforcesDecompressedLimit()
    {
        var assignedId = Guid.NewGuid();
        // A tiny compressed payload that decompresses past the 256 MiB limit.
        var bomb = new MemoryStream();
        await using (var compressor = new GZipStream(bomb, CompressionMode.Compress, leaveOpen: true))
        {
            var chunk = new byte[1024 * 1024];
            for (var remaining = (long)NzbInputLimits.Default.MaxXmlBytes + 1;
                 remaining > 0;
                 remaining -= chunk.Length)
            {
                await compressor.WriteAsync(chunk);
            }
        }

        bomb.Position = 0;
        Assert.True(bomb.Length < 8 * 1024 * 1024, "the bomb payload should compress well");
        var controller = CreateController();

        var ex = await Assert.ThrowsAsync<ApiValidationException>(() =>
            controller.AddFileAsync(CreateRequest("bomb.nzb", assignedId, bomb)));

        Assert.Contains("maximum allowed size", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(BlobStore.ReadBlob(assignedId));
        Assert.False(await _context.QueueItems.AsNoTracking().AnyAsync(q => q.Id == assignedId));
    }

    [Fact]
    public async Task SubmitAsync_BackupEnabled_ValidationFailureLeavesNoBackupFile()
    {
        var backupRoot = Path.Join(_configRoot, "nzb-backups");
        EnableBackup(backupRoot);
        var assignedId = Guid.NewGuid();
        const string invalidNzb = """
            <nzb><file subject="bad"><segments>
              <segment bytes="nope" number="1">id-one@example</segment>
            </segments></file></nzb>
            """;
        var controller = CreateController();

        await Assert.ThrowsAsync<ApiValidationException>(() =>
            controller.AddFileAsync(CreateRequest(
                "invalid.nzb", assignedId,
                new MemoryStream(Encoding.UTF8.GetBytes(invalidNzb)))));

        Assert.Null(BlobStore.ReadBlob(assignedId));
        Assert.False(Directory.Exists(backupRoot)
            && Directory.EnumerateFiles(backupRoot, "*.nzb", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task SubmitAsync_BackupEnabled_ValidNzbStillBackedUp()
    {
        var backupRoot = Path.Join(_configRoot, "nzb-backups");
        EnableBackup(backupRoot);
        var assignedId = Guid.NewGuid();
        var controller = CreateController();

        var response = await controller.AddFileAsync(CreateRequest(
            "good.nzb", assignedId,
            new MemoryStream(Encoding.UTF8.GetBytes(ValidNzb))));

        Assert.True(response.Status);
        Assert.Null((await _context.QueueItems.AsNoTracking().SingleAsync()).ArrDownloadId);
        var backupFile = Assert.Single(
            Directory.EnumerateFiles(backupRoot, "*.nzb", SearchOption.AllDirectories));
        Assert.Equal(ValidNzb, await File.ReadAllTextAsync(backupFile));
    }

    private const string ValidNzb = """
        <?xml version="1.0" encoding="utf-8"?>
        <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
          <file subject="test">
            <groups><group>alt.binaries.test</group></groups>
            <segments>
              <segment bytes="100" number="1">seg@example.com</segment>
            </segments>
          </file>
        </nzb>
        """;

    private void EnableBackup(string backupRoot)
    {
        _configManager.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.ApiNzbBackupEnabled,
                ConfigValue = "true",
            },
            new ConfigItem
            {
                ConfigName = ConfigKeys.ApiNzbBackupLocation,
                ConfigValue = backupRoot,
            },
        ]);
    }

    private AddFileController CreateController() =>
        new(
            new DefaultHttpContext(),
            _dbClient,
            _queueManager,
            _configManager,
            _websocketManager);

    private static AddFileRequest CreateRequest(string fileName, Guid nzoId, Stream nzbStream) =>
        new()
        {
            NzoId = nzoId,
            ReplaceExistingQueueItem = true,
            FileName = fileName,
            ContentType = "application/x-nzb",
            NzbFileStream = nzbStream,
            Category = "tv",
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None,
            CancellationToken = CancellationToken.None,
        };
}
