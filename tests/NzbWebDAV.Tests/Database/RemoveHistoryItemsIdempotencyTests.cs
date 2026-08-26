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
        await _context.SaveChangesAsync();

        Assert.False(await _context.HistoryItems.AnyAsync(x => x.Id == historyItemId));
        Assert.Equal(1, await _context.HistoryCleanupItems.CountAsync(x => x.Id == historyItemId));
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
        await _context.SaveChangesAsync();

        Assert.False(await _context.HistoryItems.AnyAsync(x => x.Id == historyItemId));
        Assert.Equal(1, await _context.HistoryCleanupItems.CountAsync(x => x.Id == historyItemId));
    }

    [Fact]
    public async Task RemoveHistoryItems_TwoContextsStagedBeforeEitherSaves_FourIdsDoNotExhaustRetries()
    {
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();
        foreach (var id in ids)
            _context.HistoryItems.Add(CompletedHistory(id));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await using var context2 = CreateContext();
        var client1 = new DavDatabaseClient(_context);
        var client2 = new DavDatabaseClient(context2);

        await client1.RemoveHistoryItemsAsync(ids, deleteFiles: false);
        await client2.RemoveHistoryItemsAsync(ids, deleteFiles: false);

        await client1.SaveHistoryRemovalAsync();
        await client2.SaveHistoryRemovalAsync();

        Assert.False(await _context.HistoryItems.AnyAsync(x => ids.Contains(x.Id)));
        Assert.Equal(ids.Count, await _context.HistoryCleanupItems.CountAsync(x => ids.Contains(x.Id)));
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
        var client1 = new DavDatabaseClient(_context);
        var client2 = new DavDatabaseClient(context2);

        await client1.RemoveHistoryItemsAsync([historyItemId], deleteFiles: true);
        await client2.RemoveHistoryItemsAsync([historyItemId], deleteFiles: true);

        await client1.SaveHistoryRemovalAsync();
        await client2.SaveHistoryRemovalAsync();

        Assert.False(await _context.HistoryItems.AnyAsync(x => x.Id == historyItemId));
        Assert.False(await _context.Items.AnyAsync(x => x.Id == dirId));
        Assert.Equal(1, await _context.HistoryCleanupItems.CountAsync(x => x.Id == historyItemId));
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
