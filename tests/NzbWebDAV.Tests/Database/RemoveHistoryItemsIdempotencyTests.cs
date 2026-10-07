using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Database;

public sealed class RemoveHistoryItemsIdempotencyTests : IDisposable
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"nzbdav-remove-history-{Guid.NewGuid():N}.sqlite");
    private readonly DavDatabaseContext _context;

    public RemoveHistoryItemsIdempotencyTests()
    {
        _context = CreateContext();
        _context.Database.EnsureCreated();
    }

    private DavDatabaseContext CreateContext() =>
        new(new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .Options);

    public void Dispose()
    {
        _context.Dispose();
        try { File.Delete(_databasePath); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task RemoveHistoryItems_WhenCleanupRowAlreadyPending_DoesNotThrowOrDuplicate()
    {
        var historyItemId = Guid.NewGuid();
        _context.HistoryItems.Add(new HistoryItem
        {
            Id = historyItemId,
            CreatedAt = DateTime.UtcNow,
            FileName = "file.mkv",
            JobName = "job",
            Category = "tv",
            DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
        });
        // A pending cleanup row from a prior delete that has not been processed yet.
        _context.HistoryCleanupItems.Add(new HistoryCleanupItem
        {
            Id = historyItemId,
            DeleteMountedFiles = false,
        });
        await _context.SaveChangesAsync();

        var client = new DavDatabaseClient(_context);
        await client.RemoveHistoryItemsAsync([historyItemId], deleteFiles: false);
        await client.SaveHistoryRemovalAsync();

        Assert.False(await _context.HistoryItems.AnyAsync(x => x.Id == historyItemId));
        Assert.Equal(1, await _context.HistoryCleanupItems.CountAsync(x => x.Id == historyItemId));
        Assert.False((await _context.HistoryCleanupItems.AsNoTracking()
            .SingleAsync(item => item.Id == historyItemId)).DeleteMountedFiles);
    }

    [Fact]
    public async Task RemoveHistoryItems_WhenNoCleanupRowPending_AddsExactlyOne()
    {
        var historyItemId = Guid.NewGuid();
        _context.HistoryItems.Add(new HistoryItem
        {
            Id = historyItemId,
            CreatedAt = DateTime.UtcNow,
            FileName = "file.mkv",
            JobName = "job",
            Category = "tv",
            DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
        });
        await _context.SaveChangesAsync();

        var client = new DavDatabaseClient(_context);
        await client.RemoveHistoryItemsAsync([historyItemId], deleteFiles: false);
        await client.SaveHistoryRemovalAsync();

        Assert.False(await _context.HistoryItems.AnyAsync(x => x.Id == historyItemId));
        Assert.Equal(1, await _context.HistoryCleanupItems.CountAsync(x => x.Id == historyItemId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoveHistoryItems_TwoContextsStagedBeforeEitherSaves_FourIdsDoNotExhaustRetries(
        bool consumeWinningCleanup)
    {
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();
        foreach (var historyId in ids)
            _context.HistoryItems.Add(CompletedHistory(historyId));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await using var context2 = CreateContext();
        var saveFailures = new List<Exception>();
        context2.SaveChangesFailed += (_, eventArgs) => saveFailures.Add(eventArgs.Exception);
        var client1 = new DavDatabaseClient(_context);
        var client2 = new DavDatabaseClient(context2);

        await client1.RemoveHistoryItemsAsync(ids, deleteFiles: false);
        await client2.RemoveHistoryItemsAsync(ids, deleteFiles: false);

        await client1.SaveHistoryRemovalAsync();
        if (consumeWinningCleanup)
        {
            await _context.HistoryCleanupItems
                .Where(item => ids.Contains(item.Id))
                .ExecuteDeleteAsync();
        }
        await client2.SaveHistoryRemovalAsync();

        Assert.NotEmpty(saveFailures);
        Assert.All(saveFailures, exception => Assert.IsType<DbUpdateConcurrencyException>(exception));
        Assert.False(await _context.HistoryItems.AsNoTracking().AnyAsync(item => ids.Contains(item.Id)));
        var cleanup = await _context.HistoryCleanupItems.AsNoTracking()
            .Where(item => ids.Contains(item.Id)).ToListAsync();
        Assert.Equal(consumeWinningCleanup ? 0 : ids.Count, cleanup.Count);
        Assert.All(cleanup, item => Assert.False(item.DeleteMountedFiles));
    }

    [Fact]
    public async Task RemoveHistoryItems_TwoContextsDeleteFiles_ConcurrentDavItemDeleteDoesNotThrow()
    {
        var historyItemId = Guid.NewGuid();
        var dirId = Guid.NewGuid();
        _context.Items.Add(new DavItem
        {
            Id = dirId,
            IdPrefix = dirId.ToString("N")[..DavItem.IdPrefixLength],
            CreatedAt = DateTime.UtcNow,
            Name = "job",
            Type = DavItem.ItemType.Directory,
            SubType = DavItem.ItemSubType.Directory,
            Path = "/job",
        });
        _context.HistoryItems.Add(CompletedHistory(historyItemId, dirId));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await using var context2 = CreateContext();
        var saveFailures = new List<Exception>();
        context2.SaveChangesFailed += (_, eventArgs) => saveFailures.Add(eventArgs.Exception);
        var client1 = new DavDatabaseClient(_context);
        var client2 = new DavDatabaseClient(context2);

        await client1.RemoveHistoryItemsAsync([historyItemId], deleteFiles: true);
        await client2.RemoveHistoryItemsAsync([historyItemId], deleteFiles: true);

        await client1.SaveHistoryRemovalAsync();
        await client2.SaveHistoryRemovalAsync();

        Assert.False(await _context.HistoryItems.AnyAsync(x => x.Id == historyItemId));
        Assert.False(await _context.Items.AnyAsync(x => x.Id == dirId));
        Assert.Equal(1, await _context.HistoryCleanupItems.CountAsync(x => x.Id == historyItemId));
        Assert.All(saveFailures, exception => Assert.IsType<DbUpdateConcurrencyException>(exception));
        Assert.True((await _context.HistoryCleanupItems.AsNoTracking()
            .SingleAsync(item => item.Id == historyItemId)).DeleteMountedFiles);
    }

    [Fact]
    public async Task SaveHistoryRemoval_OneStagedRowVanished_EnqueuesCleanupForEveryRowItDeleted()
    {
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).Order().ToList();
        var vanishedId = ids[1];
        foreach (var historyId in ids)
            _context.HistoryItems.Add(CompletedHistory(historyId));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await using var context2 = CreateContext();
        var saveFailures = new List<Exception>();
        context2.SaveChangesFailed += (_, eventArgs) => saveFailures.Add(eventArgs.Exception);
        var client1 = new DavDatabaseClient(_context);
        var client2 = new DavDatabaseClient(context2);

        await client1.RemoveHistoryItemsAsync([vanishedId], deleteFiles: false);
        await client2.RemoveHistoryItemsAsync(ids, deleteFiles: false);
        await client1.SaveHistoryRemovalAsync();
        await client2.SaveHistoryRemovalAsync();

        Assert.NotEmpty(saveFailures);
        Assert.All(saveFailures, exception => Assert.IsType<DbUpdateConcurrencyException>(exception));
        Assert.False(await _context.HistoryItems.AsNoTracking().AnyAsync(item => ids.Contains(item.Id)));
        var cleanup = await _context.HistoryCleanupItems.AsNoTracking()
            .Where(item => ids.Contains(item.Id)).ToListAsync();
        Assert.Equal(ids, cleanup.Select(item => item.Id).Order().ToList());
        Assert.All(cleanup, item => Assert.False(item.DeleteMountedFiles));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveHistoryRemoval_OwnedTransaction_UsesOneSaveAndRestoresNotificationOwnership(
        bool initiallySuppressed)
    {
        var historyItemId = Guid.NewGuid();
        _context.HistoryItems.Add(CompletedHistory(historyItemId));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        _context.SuppressAutomaticRcloneVfsForget = initiallySuppressed;

        var saveCount = 0;
        var saveHadTransaction = false;
        var saveWasSuppressed = false;
        _context.SavedChanges += (_, _) =>
        {
            saveCount++;
            saveHadTransaction = _context.Database.CurrentTransaction is not null;
            saveWasSuppressed = _context.SuppressAutomaticRcloneVfsForget;
        };

        var client = new DavDatabaseClient(_context);
        await client.RemoveHistoryItemsAsync([historyItemId], deleteFiles: false);
        await client.SaveHistoryRemovalAsync();

        Assert.Equal(1, saveCount);
        Assert.True(saveHadTransaction);
        Assert.True(saveWasSuppressed);
        Assert.Equal(initiallySuppressed, _context.SuppressAutomaticRcloneVfsForget);
        Assert.Null(_context.Database.CurrentTransaction);
        await using var observer = CreateContext();
        Assert.False(await observer.HistoryItems.AnyAsync(item => item.Id == historyItemId));
        Assert.False((await observer.HistoryCleanupItems.SingleAsync(item => item.Id == historyItemId))
            .DeleteMountedFiles);
    }

    [Fact]
    public async Task SaveHistoryRemoval_DeleteFails_RollsBackAndRestoresNotificationOwnership()
    {
        var historyItemId = Guid.NewGuid();
        _context.HistoryItems.Add(CompletedHistory(historyItemId));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        await _context.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER TR_Test_BlockHistoryDelete
            BEFORE DELETE ON HistoryItems
            BEGIN
                SELECT RAISE(ABORT, 'simulated history-delete failure');
            END;
            """);

        var client = new DavDatabaseClient(_context);
        await client.RemoveHistoryItemsAsync([historyItemId], deleteFiles: false);
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => client.SaveHistoryRemovalAsync());

        var sqliteException = Assert.IsType<SqliteException>(exception.InnerException);
        Assert.Contains("simulated history-delete failure", sqliteException.Message, StringComparison.Ordinal);
        Assert.False(_context.SuppressAutomaticRcloneVfsForget);
        Assert.Null(_context.Database.CurrentTransaction);
        await using var observer = CreateContext();
        Assert.True(await observer.HistoryItems.AnyAsync(item => item.Id == historyItemId));
        Assert.False(await observer.HistoryCleanupItems.AnyAsync(item => item.Id == historyItemId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveHistoryRemoval_CallerTransaction_RemainsOwnedByCaller(bool initiallySuppressed)
    {
        var historyItemId = Guid.NewGuid();
        _context.HistoryItems.Add(CompletedHistory(historyItemId));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        _context.SuppressAutomaticRcloneVfsForget = initiallySuppressed;

        var client = new DavDatabaseClient(_context);
        await client.RemoveHistoryItemsAsync([historyItemId], deleteFiles: false);
        await using var transaction = await _context.Database.BeginTransactionAsync();
        await client.SaveHistoryRemovalAsync();

        Assert.Same(transaction, _context.Database.CurrentTransaction);
        Assert.Equal(initiallySuppressed, _context.SuppressAutomaticRcloneVfsForget);
        Assert.False(await _context.HistoryItems.AsNoTracking().AnyAsync(item => item.Id == historyItemId));
        Assert.True(await _context.HistoryCleanupItems.AsNoTracking().AnyAsync(item => item.Id == historyItemId));
        await transaction.RollbackAsync();

        await using var observer = CreateContext();
        Assert.True(await observer.HistoryItems.AnyAsync(item => item.Id == historyItemId));
        Assert.False(await observer.HistoryCleanupItems.AnyAsync(item => item.Id == historyItemId));
    }

    private static HistoryItem CompletedHistory(Guid id, Guid? downloadDirId = null) => new()
    {
        Id = id,
        CreatedAt = DateTime.UtcNow,
        FileName = "file.mkv",
        JobName = "job",
        Category = "tv",
        DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
        DownloadDirId = downloadDirId,
    };
}
