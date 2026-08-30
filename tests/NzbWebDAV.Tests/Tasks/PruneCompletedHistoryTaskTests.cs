using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Tasks;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Tasks;

[Collection(nameof(BaseTaskCollection))]
public class PruneCompletedHistoryTaskTests
{
    [Fact]
    public async Task Execute_PrunesOnlyCompletedMatchingCategoryAndAge()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        await using var harness = await TempDb.CreateAsync();
        try
        {
            var ctx = harness.Context;
            var oldMoviesId = Guid.NewGuid(); var recentMoviesId = Guid.NewGuid(); var oldTvId = Guid.NewGuid(); var failedId = Guid.NewGuid();
            ctx.HistoryItems.AddRange(
                CreateHistory(oldMoviesId, "old-movies.nzb", "movies", DateTime.UtcNow.AddDays(-120)),
                CreateHistory(recentMoviesId, "recent-movies.nzb", "movies", DateTime.UtcNow.AddDays(-5)),
                CreateHistory(oldTvId, "old-tv.nzb", "tv", DateTime.UtcNow.AddDays(-120)),
                CreateHistory(failedId, "failed.nzb", "movies", DateTime.UtcNow.AddDays(-120), HistoryItem.DownloadStatusOption.Failed));
            await ctx.SaveChangesAsync(); ctx.ChangeTracker.Clear();
            Assert.True(await new PruneCompletedHistoryTask(new WebsocketManager(), false, "movies", 90, () => harness.CreateContext()).Execute());
            Assert.Null(await ctx.HistoryItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == oldMoviesId));
            Assert.NotNull(await ctx.HistoryItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == recentMoviesId));
            Assert.NotNull(await ctx.HistoryItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == oldTvId));
            Assert.NotNull(await ctx.HistoryItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == failedId));
        }
        finally { await BaseTask.ResetRunningTaskForTestsAsync(); }
    }

    [Fact]
    public async Task DryRun_PublishesProgressOnPruneTopicNotCleanupTopic()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        await using var harness = await TempDb.CreateAsync();
        try
        {
            var ctx = harness.Context;
            ctx.HistoryItems.Add(CreateHistory(Guid.NewGuid(), "dry-run.nzb", "movies", DateTime.UtcNow.AddDays(-200)));
            await ctx.SaveChangesAsync(); ctx.ChangeTracker.Clear();
            var websocket = new WebsocketManager();
            Assert.True(await new PruneCompletedHistoryTask(websocket, true, null, 90, () => harness.CreateContext()).Execute());
            var progress = websocket.PeekLastMessage(WebsocketTopic.PruneCompletedHistoryTaskProgress);
            Assert.NotNull(progress);
            Assert.StartsWith("Dry Run - Done.", progress);
            Assert.Null(websocket.PeekLastMessage(WebsocketTopic.CleanupTaskProgress));
        }
        finally { await BaseTask.ResetRunningTaskForTestsAsync(); }
    }

    [Fact]
    public async Task DryRun_DoesNotModifyDatabase()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        await using var harness = await TempDb.CreateAsync();
        try
        {
            var ctx = harness.Context;
            ctx.HistoryItems.Add(CreateHistory(Guid.NewGuid(), "dry-run.nzb", "movies", DateTime.UtcNow.AddDays(-200)));
            await ctx.SaveChangesAsync(); ctx.ChangeTracker.Clear();
            Assert.True(await new PruneCompletedHistoryTask(new WebsocketManager(), true, null, 90, () => harness.CreateContext()).Execute());
            Assert.Equal(1, await ctx.HistoryItems.CountAsync());
            Assert.Empty(await ctx.HistoryCleanupItems.AsNoTracking().ToListAsync());
        }
        finally { await BaseTask.ResetRunningTaskForTestsAsync(); }
    }

    [Fact]
    public async Task Execute_PrunesInBatches_WhenMoreThanBatchSize()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        await using var harness = await TempDb.CreateAsync();
        try
        {
            var ctx = harness.Context;
            for (var i = 0; i < 150; i++) ctx.HistoryItems.Add(CreateHistory(Guid.NewGuid(), $"batch-{i}.nzb", "movies", DateTime.UtcNow.AddDays(-200)));
            await ctx.SaveChangesAsync(); ctx.ChangeTracker.Clear();
            Assert.True(await new PruneCompletedHistoryTask(new WebsocketManager(), false, "movies", 90, () => harness.CreateContext()).Execute());
            Assert.Equal(0, await ctx.HistoryItems.CountAsync());
            Assert.Equal(150, await ctx.HistoryCleanupItems.CountAsync());
        }
        finally { await BaseTask.ResetRunningTaskForTestsAsync(); }
    }

    [Fact]
    public async Task Execute_PrunesAndReportsDone_WhenProgressObserverThrowsOnce()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        await using var harness = await TempDb.CreateAsync();
        try
        {
            var ctx = harness.Context;
            var id = Guid.NewGuid();
            ctx.HistoryItems.Add(CreateHistory(id, "old-movies.nzb", "movies", DateTime.UtcNow.AddDays(-120)));
            await ctx.SaveChangesAsync();
            ctx.ChangeTracker.Clear();

            var invocation = 0;
            var messages = new ConcurrentQueue<string>();
            void Observer(string message)
            {
                if (Interlocked.Increment(ref invocation) == 1)
                    throw new InvalidOperationException("progress observer failure");
                messages.Enqueue(message);
            }

            Assert.True(await new PruneCompletedHistoryTask(
                new WebsocketManager(),
                false,
                "movies",
                90,
                () => harness.CreateContext(),
                progressObserver: Observer).Execute());

            Assert.Null(await ctx.HistoryItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id));
            Assert.Equal(1, await ctx.HistoryCleanupItems.CountAsync());
            Assert.Contains(
                messages,
                message => message.StartsWith("Done. Pruned 1", StringComparison.Ordinal));
            Assert.DoesNotContain(
                messages,
                message => message.StartsWith("Failed:", StringComparison.Ordinal));
        }
        finally { await BaseTask.ResetRunningTaskForTestsAsync(); }
    }

    [Fact]
    public async Task BuildFilterQuery_OnlyIncludesCompletedStatus()
    {
        await using var harness = await TempDb.CreateAsync();
        var ctx = harness.Context;
        var completedId = Guid.NewGuid(); var failedId = Guid.NewGuid();
        ctx.HistoryItems.AddRange(
            CreateHistory(completedId, "ok.nzb", "movies", DateTime.UtcNow.AddDays(-10)),
            CreateHistory(failedId, "bad.nzb", "movies", DateTime.UtcNow.AddDays(-10), HistoryItem.DownloadStatusOption.Failed));
        await ctx.SaveChangesAsync();
        Assert.Equal([completedId], PruneCompletedHistoryTask.BuildFilterQuery(ctx, null, null).Select(h => h.Id).ToList());
    }

    private static HistoryItem CreateHistory(Guid id, string fileName, string category, DateTime createdAt,
        HistoryItem.DownloadStatusOption status = HistoryItem.DownloadStatusOption.Completed) => new()
        {
            Id = id,
            CreatedAt = createdAt,
            FileName = fileName,
            JobName = Path.GetFileNameWithoutExtension(fileName),
            Category = category,
            DownloadStatus = status,
            TotalSegmentBytes = 100,
            DownloadTimeSeconds = 1,
        };

    private sealed class TempDb : IAsyncDisposable
    {
        private readonly string _path;
        private TempDb(string path, DavDatabaseContext context) { _path = path; Context = context; }
        public DavDatabaseContext Context { get; }
        public DavDatabaseContext CreateContext() => new(new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite($"Data Source={_path}")
            .AddInterceptors(new SqliteMainDbPragmas()).ReplaceService<IMigrationsSqlGenerator, SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>().Options);
        public static async Task<TempDb> CreateAsync()
        {
            var path = Path.Join(Path.GetTempPath(), $"nzbdav-prune-{Guid.NewGuid():N}.sqlite");
            var ctx = new DavDatabaseContext(new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite($"Data Source={path}")
                .AddInterceptors(new SqliteMainDbPragmas()).ReplaceService<IMigrationsSqlGenerator, SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>().Options);
            await ctx.Database.MigrateAsync();
            return new TempDb(path, ctx);
        }
        public async ValueTask DisposeAsync() { await Context.DisposeAsync(); try { File.Delete(_path); } catch (IOException) { } }
    }
}
