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
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Queue;

/// <summary>
/// Database infrastructure failures at the finalize boundary are not content
/// failures: SQLITE_BUSY/LOCKED retries the commit in place, and persistent
/// contention or disk/corruption errors leave the item queued with a backoff
/// instead of writing a misleading failed-history row.
/// </summary>
[Collection(nameof(ConfigPathCollection))]
public sealed class FinalizeDatabaseErrorTests : IAsyncLifetime
{
    private const string Category = "other";
    private const string JobName = "finalize-db-job";
    private const string VideoFileName = "movie.mkv";

    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-finalize-cfg-{Guid.NewGuid():N}");
    private readonly string _segmentId = $"finalize-seg-{Guid.NewGuid():N}@example.com";
    private readonly byte[] _payload = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
    private string? _previousConfigPath;

    public async Task InitializeAsync()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(_configRoot);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configRoot);

        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        await Task.CompletedTask;
        try { Directory.Delete(_configRoot, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task FinalizeCommit_TransientBusyOnce_RetriesAndCompletesImport()
    {
        var interceptor = new SqliteFaultOnFinalizeInterceptor(errorCode: 5, maxThrows: 1);
        await using var context = CreateContext(interceptor);
        var queueItem = await SeedQueueItemAsync(context, _segmentId);

        await ProcessAsync(context, queueItem, _segmentId);

        // One faulted attempt plus one successful in-place retry.
        Assert.Equal(2, interceptor.FinalizeAttempts);
        context.ChangeTracker.Clear();
        Assert.Empty(await context.QueueItems.AsNoTracking().ToListAsync());
        var historyItem = Assert.Single(await context.HistoryItems.AsNoTracking().ToListAsync());
        Assert.Null(historyItem.FailMessage);
        Assert.Equal(HistoryItem.DownloadStatusOption.Completed, historyItem.DownloadStatus);
        Assert.True(await context.Items.AsNoTracking().AnyAsync(x => x.Name == VideoFileName));
    }

    [Fact]
    public async Task FinalizeCommit_PersistentBusy_LeavesItemQueuedWithBackoff()
    {
        var interceptor = new SqliteFaultOnFinalizeInterceptor(errorCode: 5, maxThrows: int.MaxValue);
        await using var context = CreateContext(interceptor);
        var queueItem = await SeedQueueItemAsync(context, _segmentId);

        await ProcessAsync(context, queueItem, _segmentId);

        context.ChangeTracker.Clear();
        Assert.Empty(await context.HistoryItems.AsNoTracking().ToListAsync());
        var remaining = await context.QueueItems.AsNoTracking().SingleAsync();
        Assert.Equal(queueItem.Id, remaining.Id);
        Assert.NotNull(remaining.PauseUntil);
        Assert.True(remaining.PauseUntil > DateTime.Now.AddSeconds(30),
            $"expected a real backoff, got {remaining.PauseUntil}");
        Assert.True(remaining.PauseUntil < DateTime.Now.AddMinutes(2),
            $"transient contention should back off for ~60s, got {remaining.PauseUntil}");
    }

    [Theory]
    [InlineData(8)]
    [InlineData(13)]
    [InlineData(11)]
    public async Task FinalizeCommit_DiskOrCorruption_LeavesItemQueuedWithLongBackoff(int errorCode)
    {
        var interceptor = new SqliteFaultOnFinalizeInterceptor(errorCode, maxThrows: int.MaxValue);
        await using var context = CreateContext(interceptor);
        var queueItem = await SeedQueueItemAsync(context, _segmentId);

        await ProcessAsync(context, queueItem, _segmentId);

        // Disk/corruption codes are not retried in place...
        Assert.Equal(1, interceptor.FinalizeAttempts);
        context.ChangeTracker.Clear();
        Assert.Empty(await context.HistoryItems.AsNoTracking().ToListAsync());
        var remaining = await context.QueueItems.AsNoTracking().SingleAsync();
        Assert.Equal(queueItem.Id, remaining.Id);
        Assert.NotNull(remaining.PauseUntil);
        Assert.True(remaining.PauseUntil > DateTime.Now.AddMinutes(3),
            $"disk/corruption should back off for minutes, got {remaining.PauseUntil}");
    }

    [Fact]
    public async Task FailedFinalize_PersistentBusy_PausesInsteadOfHotLooping()
    {
        // Content failure (missing article) whose failed-history finalize then
        // hits persistent SQLITE_BUSY: the item must stay queued with a backoff
        // rather than becoming immediately reclaimable.
        var missingSegmentId = $"missing-{Guid.NewGuid():N}@example.com";
        var interceptor = new SqliteFaultOnFinalizeInterceptor(errorCode: 5, maxThrows: int.MaxValue);
        await using var context = CreateContext(interceptor);
        var queueItem = await SeedQueueItemAsync(context, missingSegmentId);

        await ProcessAsync(context, queueItem, missingSegmentId);

        context.ChangeTracker.Clear();
        Assert.Empty(await context.HistoryItems.AsNoTracking().ToListAsync());
        var remaining = await context.QueueItems.AsNoTracking().SingleAsync();
        Assert.Equal(queueItem.Id, remaining.Id);
        Assert.NotNull(remaining.PauseUntil);
        Assert.True(remaining.PauseUntil > DateTime.Now.AddSeconds(30),
            $"expected a real backoff, got {remaining.PauseUntil}");
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

    private async Task<QueueItem> SeedQueueItemAsync(DavDatabaseContext context, string segmentId)
    {
        var nzbBytes = CreateNzbBytes(segmentId);
        var queueItem = new QueueItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            FileName = $"{JobName}.nzb",
            JobName = JobName,
            NzbFileSize = nzbBytes.Length,
            TotalSegmentBytes = _payload.Length,
            Category = Category,
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None,
        };
        context.QueueItems.Add(queueItem);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return await context.QueueItems.SingleAsync(q => q.Id == queueItem.Id);
    }

    private async Task ProcessAsync(DavDatabaseContext context, QueueItem queueItem, string segmentId)
    {
        await using var nzbStream = new MemoryStream(CreateNzbBytes(segmentId));
        var processor = new QueueItemProcessor(
            queueItem,
            nzbStream,
            new DavDatabaseClient(context),
            new ScriptedVideoNntpClient(VideoFileName, _segmentId, _payload),
            new ConfigManager(),
            new WebsocketManager(),
            new Progress<int>(),
            CancellationToken.None);
        await processor.ProcessAsync();
    }

    private byte[] CreateNzbBytes(string segmentId) => Encoding.UTF8.GetBytes(
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
          <file subject="&quot;{VideoFileName}&quot; yEnc (1/1)">
            <groups><group>alt.binaries.test</group></groups>
            <segments>
              <segment bytes="{_payload.Length}" number="1">{segmentId}</segment>
            </segments>
          </file>
        </nzb>
        """);

    /// <summary>
    /// Faults any save that deletes a queue item (success or failed-history
    /// finalize) with the given SQLite primary result code, up to maxThrows times.
    /// </summary>
    private sealed class SqliteFaultOnFinalizeInterceptor(int errorCode, int maxThrows)
        : SaveChangesInterceptor
    {
        private int _finalizeAttempts;
        public int FinalizeAttempts => Volatile.Read(ref _finalizeAttempts);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var isFinalize = eventData.Context!.ChangeTracker.Entries<QueueItem>()
                .Any(e => e.State == EntityState.Deleted);
            if (isFinalize && Interlocked.Increment(ref _finalizeAttempts) <= maxThrows)
            {
                throw new DbUpdateException(
                    "simulated sqlite fault",
                    new SqliteException($"SQLite Error {errorCode}.", errorCode));
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
