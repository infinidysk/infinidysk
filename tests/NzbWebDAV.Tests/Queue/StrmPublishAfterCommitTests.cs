using System.Collections.Concurrent;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Queue.PostProcessors;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Websocket;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Tests.Queue;

/// <summary>
/// STRM sidecars publish only after the finalize commit succeeds: a failed
/// SaveChanges leaves no sidecars, a post-commit sidecar failure never
/// re-finalizes the import, and duplicate mark-failed imports publish nothing.
/// </summary>
[Collection(nameof(ConfigPathCollection))]
public sealed class StrmPublishAfterCommitTests : IAsyncLifetime
{
    private const string Category = "other";
    private const string JobName = "movie-job";
    private const string VideoFileName = "movie.mkv";
    private const string NestedArchiveOnlyBase64 =
        "N3q8ryccAAT/31JgFQAAAAAAAAB0AAAAAAAAAEfe5xhyYXItb25lcmFyLXR3b3ByZXZpZXcB" +
        "BAYAAwkHBwcABwsDAAEBAAEBAAEBAAwHBwcACAAABQMRTwBpAG4AbgBlAHIALgByAGEAcgAA" +
        "AGkAbgBuAGUAcgAuAHIAMAAwAAAAUwBhAG0AcABsAGUALwBwAHIAZQB2AGkAZQB3AC4AbQBr" +
        "AHYAAAAAAA==";
    private const string NestedArchiveWithMediaBase64 =
        "N3q8ryccAARtZlqlHAAAAAAAAACNAAAAAAAAADTiUmFyYXItb25lcmFyLXR3b3ByZXZpZXdm" +
        "ZWF0dXJlAQQGAAQJBwcHBwAHCwQAAQEAAQEAAQEAAQEADAcHBwcACAAABQQRYwBpAG4AbgBl" +
        "AHIALgByAGEAcgAAAGkAbgBuAGUAcgAuAHIAMAAwAAAAUwBhAG0AcABsAGUALwBwAHIAZQB2" +
        "AGkAZQB3AC4AbQBrAHYAAABtAG8AdgBpAGUALgBtAGsAdgAAAAAA";

    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-strm-cfg-{Guid.NewGuid():N}");
    private readonly string _strmDir =
        Path.Join(Path.GetTempPath(), $"nzbdav-strm-out-{Guid.NewGuid():N}");
    private readonly string _segmentId = $"strm-seg-{Guid.NewGuid():N}@example.com";
    private readonly byte[] _payload = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
    private string? _previousConfigPath;

    public async Task InitializeAsync()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(_configRoot);
        Directory.CreateDirectory(_strmDir);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configRoot);

        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        await Task.CompletedTask;
        try { Directory.Delete(_configRoot, recursive: true); } catch (IOException) { /* best effort */ }
        try { Directory.Delete(_strmDir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task SuccessfulImport_PublishesStrmAfterCommit()
    {
        await using var context = CreateContext();
        var queueItem = await SeedQueueItemAsync(context);

        await ProcessAsync(context, CreateConfig(), queueItem);

        context.ChangeTracker.Clear();
        Assert.Empty(await context.QueueItems.AsNoTracking().ToListAsync());
        var historyItem = Assert.Single(await context.HistoryItems.AsNoTracking().ToListAsync());
        Assert.Null(historyItem.FailMessage);
        Assert.Equal(HistoryItem.DownloadStatusOption.Completed, historyItem.DownloadStatus);

        var strmPath = Path.Join(_strmDir, Category, JobName, VideoFileName + ".strm");
        Assert.True(File.Exists(strmPath), $"expected sidecar at {strmPath}");
        Assert.Contains("/view/", await File.ReadAllTextAsync(strmPath));

        var videoItem = await context.Items.AsNoTracking()
            .SingleAsync(x => x.Name == VideoFileName);
        Assert.Equal(strmPath, videoItem.GeneratedStrmPath);
    }

    [Fact]
    public async Task FinalizeSaveFailure_LeavesNoStrmFiles()
    {
        await using var context = CreateContext(new ThrowOnQueueDeleteSaveInterceptor());
        var queueItem = await SeedQueueItemAsync(context);

        await ProcessAsync(context, CreateConfig(), queueItem);

        // Both the success finalize and the failed-history finalize fail at the
        // simulated commit boundary; the pre-fix code wrote sidecars inside the
        // staged operations, before that boundary.
        Assert.Empty(Directory.GetFiles(_strmDir, "*.strm", SearchOption.AllDirectories));
        context.ChangeTracker.Clear();
        Assert.Empty(await context.HistoryItems.AsNoTracking().ToListAsync());
        Assert.True(await context.QueueItems.AsNoTracking().AnyAsync(q => q.Id == queueItem.Id));
    }

    [Fact]
    public async Task StrmFailureAfterCommit_KeepsCompletedHistory()
    {
        // Force the sidecar write to fail: a regular file blocks the per-job
        // directory the writer needs to create.
        await File.WriteAllTextAsync(Path.Join(_strmDir, Category), "blocked");

        await using var context = CreateContext();
        var queueItem = await SeedQueueItemAsync(context);

        // Must not throw: a sidecar failure is logged, not re-finalized.
        await ProcessAsync(context, CreateConfig(), queueItem);

        context.ChangeTracker.Clear();
        Assert.Empty(await context.QueueItems.AsNoTracking().ToListAsync());
        var historyItem = Assert.Single(await context.HistoryItems.AsNoTracking().ToListAsync());
        Assert.Null(historyItem.FailMessage);
        Assert.Equal(HistoryItem.DownloadStatusOption.Completed, historyItem.DownloadStatus);
        Assert.Empty(Directory.GetFiles(_strmDir, "*.strm", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task StrmMetadataSave_TransientBusy_RetriesAndPublishes()
    {
        var interceptor = new ThrowOnStrmMetadataSaveInterceptor(maxThrows: 1, transientFault: true);
        await using var context = CreateContext(interceptor);
        var queueItem = await SeedQueueItemAsync(context);

        await ProcessAsync(context, CreateConfig(), queueItem);

        // One faulted attempt plus one successful in-place retry.
        Assert.Equal(2, interceptor.Attempts);
        context.ChangeTracker.Clear();
        var historyItem = Assert.Single(await context.HistoryItems.AsNoTracking().ToListAsync());
        Assert.Null(historyItem.FailMessage);
        var strmPath = Path.Join(_strmDir, Category, JobName, VideoFileName + ".strm");
        Assert.True(File.Exists(strmPath), $"expected sidecar at {strmPath}");
        var videoItem = await context.Items.AsNoTracking().SingleAsync(x => x.Name == VideoFileName);
        Assert.Equal(strmPath, videoItem.GeneratedStrmPath);
    }

    [Fact]
    public async Task StrmMetadataSaveFailure_RollsBackPublishedSidecars()
    {
        var interceptor = new ThrowOnStrmMetadataSaveInterceptor(maxThrows: int.MaxValue, transientFault: false);
        await using var context = CreateContext(interceptor);
        var queueItem = await SeedQueueItemAsync(context);

        await ProcessAsync(context, CreateConfig(), queueItem);

        // The import stays committed, but sidecars whose ownership metadata could
        // not be persisted must not be left behind for cleanup to miss.
        context.ChangeTracker.Clear();
        var historyItem = Assert.Single(await context.HistoryItems.AsNoTracking().ToListAsync());
        Assert.Null(historyItem.FailMessage);
        Assert.Equal(HistoryItem.DownloadStatusOption.Completed, historyItem.DownloadStatus);
        Assert.Empty(Directory.GetFiles(_strmDir, "*.strm", SearchOption.AllDirectories));
        var videoItem = await context.Items.AsNoTracking().SingleAsync(x => x.Name == VideoFileName);
        Assert.Null(videoItem.GeneratedStrmPath);
    }

    [Fact]
    public async Task DuplicateMarkFailed_PublishesNoStrmFiles()
    {
        await using var context = CreateContext();
        var categoryFolder = DavItem.New(
            Guid.NewGuid(), DavItem.ContentFolder, Category, null,
            DavItem.ItemType.Directory, DavItem.ItemSubType.Directory,
            null, null, null, null);
        var mountFolder = DavItem.New(
            Guid.NewGuid(), categoryFolder, JobName, null,
            DavItem.ItemType.Directory, DavItem.ItemSubType.Directory,
            null, null, Guid.NewGuid(), null);
        context.Items.Add(categoryFolder);
        context.Items.Add(mountFolder);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var queueItem = await SeedQueueItemAsync(context);

        var config = CreateConfig();
        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.ApiDuplicateNzbBehavior, ConfigValue = "mark-failed" },
        ]);
        await ProcessAsync(context, config, queueItem);

        context.ChangeTracker.Clear();
        var historyItem = Assert.Single(await context.HistoryItems.AsNoTracking().ToListAsync());
        Assert.Equal(HistoryItem.DownloadStatusOption.Failed, historyItem.DownloadStatus);
        Assert.Contains("Duplicate nzb", historyItem.FailMessage);
        Assert.Empty(Directory.GetFiles(_strmDir, "*.strm", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(true, true, false, false, true, 0)]
    [InlineData(false, true, false, false, false, 0)]
    [InlineData(true, false, false, false, false, 1)]
    [InlineData(true, true, true, false, false, 1)]
    [InlineData(true, true, false, true, true, 0)]
    public async Task RarInsideSevenZip_RespectsMediaPolicyAndRecordsSpecificFailure(
        bool requireMedia,
        bool sampleFilter,
        bool includeMainMedia,
        bool blockRarOutputs,
        bool expectFailure,
        int expectedStrmCount)
    {
        const string sourceName = "outer.7z";
        var archiveBytes = CreateNestedSevenZipBytes(includeMainMedia);
        await using var context = CreateContext();
        var queueItem = await SeedQueueItemAsync(context, sourceName, archiveBytes);
        var config = CreateConfig();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.ApiEnsureImportableVideo,
                ConfigValue = requireMedia ? "true" : "false",
            },
            new ConfigItem
            {
                ConfigName = ConfigKeys.ApiSampleFilterEnabled,
                ConfigValue = sampleFilter ? "true" : "false",
            },
            new ConfigItem
            {
                ConfigName = ConfigKeys.ApiDownloadFileBlocklist,
                ConfigValue = blockRarOutputs ? "*.rar, *.r??" : "",
            },
        ]);

        var sink = new QueueFailureSink();
        var previousLogger = Log.Logger;
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        Log.Logger = logger;
        try
        {
            await ProcessAsync(context, config, queueItem, sourceName, archiveBytes);
        }
        finally
        {
            Log.Logger = previousLogger;
        }

        context.ChangeTracker.Clear();
        Assert.Empty(await context.QueueItems.AsNoTracking().ToListAsync());
        var history = Assert.Single(await context.HistoryItems.AsNoTracking().ToListAsync());
        Assert.Equal(
            expectFailure ? HistoryItem.DownloadStatusOption.Failed : HistoryItem.DownloadStatusOption.Completed,
            history.DownloadStatus);
        Assert.Equal(expectedStrmCount,
            Directory.GetFiles(_strmDir, "*.strm", SearchOption.AllDirectories).Length);

        var terminalFailures = sink.Events
            .Where(logEvent => logEvent.MessageTemplate.Text.StartsWith(
                "Failed queue item ", StringComparison.Ordinal))
            .ToList();
        if (expectFailure)
        {
            Assert.Equal(EnsureImportableMediaValidator.NestedRarInSevenZipFailureMessage, history.FailMessage);
            Assert.Contains("RAR members inside 7z", history.FailMessage!);
            var failureLog = Assert.Single(terminalFailures);
            Assert.Equal(LogEventLevel.Error, failureLog.Level);
            Assert.Null(failureLog.Exception);
            Assert.DoesNotContain(sink.Events, logEvent => logEvent.Exception is not null);
            Assert.Equal(
                new ScalarValue(EnsureImportableMediaValidator.NestedRarInSevenZipFailureMessage),
                failureLog.Properties["Reason"]);
            var mountPath = $"{DavItem.ContentFolder.Path.TrimEnd('/')}/{Category}/{JobName}";
            Assert.False(await context.Items.AsNoTracking().AnyAsync(
                item => item.Path == mountPath || item.Path.StartsWith(mountPath + "/")));
        }
        else
        {
            Assert.Null(history.FailMessage);
            Assert.Empty(terminalFailures);
            if (!blockRarOutputs)
                Assert.True(await context.Items.AsNoTracking().AnyAsync(item => item.Name == "inner.rar"));
        }
    }

    private ConfigManager CreateConfig()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.ApiImportStrategy, ConfigValue = "strm" },
            new ConfigItem { ConfigName = ConfigKeys.ApiCompletedDownloadsDir, ConfigValue = _strmDir },
            new ConfigItem { ConfigName = ConfigKeys.GeneralBaseUrl, ConfigValue = "http://localhost:3000" },
            new ConfigItem { ConfigName = ConfigKeys.ApiStrmKey, ConfigValue = "test-strm-key" },
            // Pin the source filename; default-on single-video rename is covered elsewhere.
            new ConfigItem { ConfigName = ConfigKeys.ApiRenameSingleVideoToRelease, ConfigValue = "false" },
        ]);
        return config;
    }

    private DavDatabaseContext CreateContext(params IInterceptor[] extraInterceptors) =>
        new(new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={DavDatabaseContext.DatabaseFilePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .AddInterceptors(extraInterceptors)
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options);

    private async Task<QueueItem> SeedQueueItemAsync(
        DavDatabaseContext context,
        string sourceFileName = VideoFileName,
        byte[]? sourcePayload = null)
    {
        var payload = sourcePayload ?? _payload;
        var queueItem = new QueueItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            FileName = $"{JobName}.nzb",
            JobName = JobName,
            NzbFileSize = CreateNzbBytes(sourceFileName, payload).Length,
            TotalSegmentBytes = payload.Length,
            Category = Category,
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None,
        };
        context.QueueItems.Add(queueItem);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return await context.QueueItems.SingleAsync(q => q.Id == queueItem.Id);
    }

    private async Task ProcessAsync(
        DavDatabaseContext context,
        ConfigManager config,
        QueueItem queueItem,
        string sourceFileName = VideoFileName,
        byte[]? sourcePayload = null)
    {
        var payload = sourcePayload ?? _payload;
        await using var nzbStream = new MemoryStream(CreateNzbBytes(sourceFileName, payload));
        using var healthCheckConnectionGate = new HealthCheckConnectionGate(config);
        using var client = new ScriptedVideoNntpClient(sourceFileName, _segmentId, payload);
        var processor = new QueueItemProcessor(
            queueItem,
            nzbStream,
            new DavDatabaseClient(context),
            client,
            config,
            new WebsocketManager(),
            new Progress<int>(),
            healthCheckConnectionGate,
            CancellationToken.None);
        await processor.ProcessAsync();
    }

    private byte[] CreateNzbBytes(
        string sourceFileName = VideoFileName,
        byte[]? sourcePayload = null)
    {
        var payload = sourcePayload ?? _payload;
        return Encoding.UTF8.GetBytes(
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
          <file subject="&quot;{sourceFileName}&quot; yEnc (1/1)">
            <groups><group>alt.binaries.test</group></groups>
            <segments>
              <segment bytes="{payload.Length}" number="1">{_segmentId}</segment>
            </segments>
          </file>
        </nzb>
        """);
    }

    private static byte[] CreateNestedSevenZipBytes(bool includeMainMedia) =>
        Convert.FromBase64String(includeMainMedia
            ? NestedArchiveWithMediaBase64
            : NestedArchiveOnlyBase64);

    private sealed class QueueFailureSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }

    /// <summary>
    /// Simulates a finalize-commit failure: any save that deletes a queue item
    /// (success or failed-history finalize) faults with a DbUpdateException.
    /// </summary>
    private sealed class ThrowOnQueueDeleteSaveInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<QueueItem>()
                    .Any(e => e.State == EntityState.Deleted))
                throw new DbUpdateException("simulated finalize commit failure");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// Faults the post-commit STRM metadata save — the only save in the import flow
    /// that modifies existing DavItems — while leaving the finalize commit untouched.
    /// A transient fault wraps SQLITE_BUSY so the in-place retry engages; a
    /// non-transient fault exhausts nothing and propagates immediately.
    /// </summary>
    private sealed class ThrowOnStrmMetadataSaveInterceptor(int maxThrows, bool transientFault)
        : SaveChangesInterceptor
    {
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var isStrmMetadataSave = eventData.Context!.ChangeTracker.Entries<DavItem>()
                .Any(e => e.State == EntityState.Modified);
            if (isStrmMetadataSave && Interlocked.Increment(ref _attempts) <= maxThrows)
            {
                throw transientFault
                    ? new DbUpdateException(
                        "simulated busy", new SqliteException("SQLite Error 5.", 5))
                    : new DbUpdateException("simulated metadata save failure");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
