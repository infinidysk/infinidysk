using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Database;

public sealed class RemoveHistoryItemsIdempotencyTests : IDisposable
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"nzbdav-remove-history-{Guid.NewGuid():N}.sqlite");
    private readonly DavDatabaseContext _context;

    public RemoveHistoryItemsIdempotencyTests()
    {
        _context = new DavDatabaseContext(new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options);
        _context.Database.EnsureCreated();
    }

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
}
