using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.FileAggregators;
using NzbWebDAV.Queue.FileProcessors;

namespace NzbWebDAV.Tests.Queue;

public class FileAggregatorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DavDatabaseContext _context;
    private readonly DavDatabaseClient _dbClient;

    public FileAggregatorTests()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"file-agg-{Guid.NewGuid():N}.sqlite");
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
    public void UpdateDatabase_PropagatesArrDownloadIdToNestedDirectoryAndLeaf()
    {
        var historyItemId = Guid.NewGuid();
        var arrDownloadId = Guid.NewGuid();
        var mount = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            "release",
            null,
            DavItem.ItemType.Directory,
            DavItem.ItemSubType.Directory,
            null,
            null,
            historyItemId,
            null,
            arrDownloadId: arrDownloadId);
        _context.Items.Add(mount);

        new FileAggregator(_dbClient, mount, checkedFullHealth: false).UpdateDatabase(
        [
            new FileProcessor.Result
            {
                NzbFile = new NzbFile
                {
                    Subject = "\"nested/video.mkv\" yEnc",
                    Segments = { new NzbSegment { MessageId = "seg@test", Bytes = 10 } },
                },
                FileName = "nested/video.mkv",
                FileSize = 10,
                ReleaseDate = DateTimeOffset.UnixEpoch,
            },
        ]);

        var directory = Assert.Single(_context.Items.Local, i =>
            i.ParentId == mount.Id && i.Type == DavItem.ItemType.Directory);
        var leaf = Assert.Single(_context.Items.Local, i =>
            i.Type == DavItem.ItemType.UsenetFile);
        Assert.Equal(arrDownloadId, directory.ArrDownloadId);
        Assert.Equal(arrDownloadId, leaf.ArrDownloadId);
        Assert.Equal(historyItemId, leaf.NzbBlobId);
        Assert.NotEqual(leaf.NzbBlobId, leaf.ArrDownloadId);
    }

    [Fact]
    public void UpdateDatabase_SevenZipEmptyMember_PreservesNestedFileAndBlob()
    {
        var historyItemId = Guid.NewGuid();
        var arrDownloadId = Guid.NewGuid();
        var mount = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            "release",
            null,
            DavItem.ItemType.Directory,
            DavItem.ItemSubType.Directory,
            null,
            null,
            historyItemId,
            null,
            arrDownloadId: arrDownloadId);
        _context.Items.Add(mount);

        new SevenZipAggregator(_dbClient, mount, checkedFullHealth: false).UpdateDatabase(
        [
            new SevenZipProcessor.Result
            {
                SevenZipFiles =
                [
                    new SevenZipProcessor.SevenZipFile
                    {
                        ArchiveSetId = "empty-sevenzip-regression",
                        PathWithinArchive = "nested/marker.txt",
                        DavMultipartFileMeta = new DavMultipartFile.Meta
                        {
                            AesParams = null,
                            FileParts = [],
                        },
                        ReleaseDate = DateTimeOffset.UnixEpoch,
                        SniffedVideoExtension = null,
                    },
                ],
            },
        ]);

        var directory = Assert.Single(_context.Items.Local, item =>
            item.ParentId == mount.Id && item.Type == DavItem.ItemType.Directory);
        Assert.Equal("nested", directory.Name);
        var leaf = Assert.Single(_context.Items.Local, item =>
            item.ParentId == directory.Id && item.Type == DavItem.ItemType.UsenetFile);
        Assert.Equal("marker.txt", leaf.Name);
        Assert.Equal(DavItem.ItemSubType.MultipartFile, leaf.SubType);
        Assert.Equal(0L, leaf.FileSize);
        Assert.Equal(historyItemId, leaf.HistoryItemId);
        Assert.Equal(historyItemId, leaf.NzbBlobId);
        Assert.Equal(arrDownloadId, leaf.ArrDownloadId);
        var blob = Assert.Single(_context.BlobMultipartFiles);
        Assert.Equal(blob.Id, leaf.FileBlobId);
        Assert.Empty(blob.Metadata.FileParts);
        Assert.Null(blob.Metadata.AesParams);
    }
}
