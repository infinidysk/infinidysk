using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Queue.FileAggregators;
using NzbWebDAV.Queue.FileProcessors;

namespace NzbWebDAV.Tests.Queue;

public class MultipartMkvAggregatorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DavDatabaseContext _context;
    private readonly DavDatabaseClient _dbClient;

    public MultipartMkvAggregatorTests()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"multipart-mkv-agg-{Guid.NewGuid():N}.sqlite");
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _context = new DavDatabaseContext(options);
        _context.Database.EnsureCreated();
        _dbClient = new DavDatabaseClient(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        try { File.Delete(_dbPath); } catch (IOException) { /* ignore */ }
    }

    [Fact]
    public void UpdateDatabase_CreatesOneDavItemPerProcessorResult()
    {
        var historyItemId = Guid.NewGuid();
        var arrDownloadId = Guid.NewGuid();
        var mount = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            "season-pack",
            null,
            DavItem.ItemType.Directory,
            DavItem.ItemSubType.Directory,
            null,
            null,
            historyItemId,
            null,
            nzbBlobId: null,
            arrDownloadId: arrDownloadId);
        _context.Items.Add(mount);

        new MultipartMkvAggregator(_dbClient, mount, checkedFullHealth: false).UpdateDatabase(
        [
            Result("EP01.mkv", 10),
            Result("EP02.mkv", 20),
        ]);

        var items = _context.Items.Local
            .Where(i => i.ParentId == mount.Id)
            .OrderBy(i => i.Name)
            .ToList();
        Assert.Equal(["EP01.mkv", "EP02.mkv"], items.Select(i => i.Name).ToArray());
        Assert.All(items, i => Assert.Equal(DavItem.ItemSubType.MultipartFile, i.SubType));
        Assert.Equal([10L, 20L], items.Select(i => i.FileSize).ToArray());
        Assert.Equal(2, _context.BlobMultipartFiles.Count);
        Assert.All(items, i => Assert.Equal(arrDownloadId, i.ArrDownloadId));
        Assert.All(items, i => Assert.Equal(historyItemId, i.NzbBlobId));
        Assert.Equal(arrDownloadId, mount.ArrDownloadId);
    }

    private static MultipartMkvProcessor.Result Result(string filename, long size) => new()
    {
        Filename = filename,
        ReleaseDate = DateTimeOffset.UnixEpoch,
        Parts =
        [
            new DavMultipartFile.FilePart
            {
                SegmentIds = [filename],
                SegmentIdByteRange = LongRange.FromStartAndSize(0, size),
                FilePartByteRange = LongRange.FromStartAndSize(0, size),
            },
        ],
    };
}
