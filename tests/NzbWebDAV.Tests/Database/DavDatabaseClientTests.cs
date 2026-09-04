using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Database;

public sealed class DavDatabaseClientTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"nzbdav-tests-{Guid.NewGuid():N}.sqlite");
    private DavDatabaseContext _context = null!;
    private TrackingDbContextFactory _contextFactory = null!;
    private DavDatabaseClient _client = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        _context = new DavDatabaseContext(options);
        await _context.Database.MigrateAsync();
        _contextFactory = new TrackingDbContextFactory(options);
        _client = new DavDatabaseClient(_context, dbContextFactory: _contextFactory);
    }

    [Fact]
    public async Task DirectoryQueriesAndRecursiveSize_UseRealSqliteSchema()
    {
        // the root item is already seeded by the database migrations
        var directory = DavItem.New(
            Guid.NewGuid(), DavItem.Root, "movies", null,
            DavItem.ItemType.Directory, DavItem.ItemSubType.Directory,
            null, null, null, null);
        var nestedDirectory = DavItem.New(
            Guid.NewGuid(), directory, "science-fiction", null,
            DavItem.ItemType.Directory, DavItem.ItemSubType.Directory,
            null, null, null, null);
        var firstFile = DavItem.New(
            Guid.NewGuid(), directory, "first.mkv", 100,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
            null, null, null, null);
        var nestedFile = DavItem.New(
            Guid.NewGuid(), nestedDirectory, "nested.mkv", 250,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
            null, null, null, null);

        _context.Items.AddRange(directory, nestedDirectory, firstFile, nestedFile);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var children = await _client.GetDirectoryChildrenAsync(directory.Id);
        Assert.Equal(
            new[] { "first.mkv", "science-fiction" },
            children.Select(item => item.Name));

        var streamedChildren = new List<DavItem>();
        await foreach (var child in _client.GetDirectoryChildrenEnumerableAsync(directory.Id))
            streamedChildren.Add(child);
        Assert.Equal(
            new[] { "first.mkv", "science-fiction" },
            streamedChildren.Select(item => item.Name));
        Assert.NotSame(_context, _contextFactory.LastCreatedContext);
        Assert.True(_contextFactory.LastCreatedContext!.IsDisposed);

        Assert.Equal(350, await _client.GetRecursiveSize(directory.Id));
        Assert.Equal(firstFile.Id, (await _client.GetFileById(firstFile.Id.ToString()))?.Id);
        Assert.Equal(
            firstFile.Id,
            (await _client.GetFilesByIdPrefix(firstFile.IdPrefix)).Single().Id);
    }

    [Fact]
    public async Task GetDirectoryChildrenEnumerableAsync_DisposesStreamingContextWhenStoppedEarly()
    {
        var directory = DavItem.New(
            Guid.NewGuid(), DavItem.Root, "shows", null,
            DavItem.ItemType.Directory, DavItem.ItemSubType.Directory,
            null, null, null, null);
        _context.Items.AddRange(
            directory,
            DavItem.New(
                Guid.NewGuid(), directory, "episode1.mkv", 100,
                DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
                null, null, null, null),
            DavItem.New(
                Guid.NewGuid(), directory, "episode2.mkv", 100,
                DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
                null, null, null, null));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await foreach (var child in _client.GetDirectoryChildrenEnumerableAsync(directory.Id))
        {
            Assert.Equal("episode1.mkv", child.Name);
            Assert.False(_contextFactory.LastCreatedContext!.IsDisposed);
            break;
        }

        Assert.True(_contextFactory.LastCreatedContext!.IsDisposed);
    }

    [Fact]
    public async Task GetDavMultipartFileAsync_BackfillsExpectedSizeForLegacyLazyMetadata()
    {
        var id = Guid.NewGuid();
        var item = DavItem.New(
            id,
            DavItem.Root,
            "movie.mkv",
            1_234,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.MultipartFile,
            null,
            null,
            null,
            null);
        var multipart = new DavMultipartFile
        {
            Id = id,
            Metadata = new DavMultipartFile.Meta
            {
                IsLazy = true,
                PathInArchive = "movie.mkv",
                FileParts =
                [
                    new DavMultipartFile.FilePart
                    {
                        SegmentIds = ["vol1"],
                        SegmentIdByteRange = new NzbWebDAV.Models.LongRange(0, 1_000),
                        FilePartByteRange = new NzbWebDAV.Models.LongRange(60, 1_000),
                    }
                ],
                PendingParts =
                [
                    new DavMultipartFile.PendingPart
                    {
                        SegmentIds = ["vol2"],
                        SegmentIdByteRange = new NzbWebDAV.Models.LongRange(0, 300),
                        EstimatedDataSize = 294,
                    }
                ],
            },
        };
        _context.Items.Add(item);
        _context.MultipartFiles.Add(multipart);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var loaded = await _client.GetDavMultipartFileAsync(item);

        Assert.NotNull(loaded);
        Assert.Equal(1_234, loaded!.Metadata.ExpectedFileSize);
    }

    [Fact]
    public async Task GetDavNzbFileAsync_CorruptBlobWithLegacyRow_FallsBackWithoutThrowing()
    {
        var blobId = Guid.NewGuid();
        var item = NewFile(DavItem.ItemSubType.NzbFile, blobId);
        _context.Items.Add(item);
        _context.NzbFiles.Add(new DavNzbFile { Id = item.Id, SegmentIds = ["<legacy@example>"] });
        await _context.SaveChangesAsync();
        var client = new DavDatabaseClient(_context, new ThrowingBlobStore(blobId));

        var loaded = await client.GetDavNzbFileAsync(item);

        Assert.NotNull(loaded);
        Assert.Equal(["<legacy@example>"], loaded!.SegmentIds);
    }

    [Fact]
    public async Task GetDavRarFileAsync_CorruptBlobWithLegacyRow_FallsBackWithoutThrowing()
    {
        var blobId = Guid.NewGuid();
        var item = NewFile(DavItem.ItemSubType.RarFile, blobId);
        _context.Items.Add(item);
        _context.RarFiles.Add(new DavRarFile { Id = item.Id });
        await _context.SaveChangesAsync();
        var client = new DavDatabaseClient(_context, new ThrowingBlobStore(blobId));

        var loaded = await client.GetDavRarFileAsync(item);

        Assert.NotNull(loaded);
    }

    [Fact]
    public async Task GetDavMultipartFileAsync_CorruptBlobWithLegacyRow_FallsBackWithoutThrowing()
    {
        var blobId = Guid.NewGuid();
        var item = NewFile(DavItem.ItemSubType.MultipartFile, blobId);
        _context.Items.Add(item);
        _context.MultipartFiles.Add(new DavMultipartFile { Id = item.Id, Metadata = new DavMultipartFile.Meta() });
        await _context.SaveChangesAsync();
        var client = new DavDatabaseClient(_context, new ThrowingBlobStore(blobId));

        var loaded = await client.GetDavMultipartFileAsync(item);

        Assert.NotNull(loaded);
    }

    [Fact]
    public async Task GetDavMultipartFileAsync_CorruptBlobWithoutLegacyRow_RethrowsCorruptedBlobPayloadException()
    {
        var blobId = Guid.NewGuid();
        var item = NewFile(DavItem.ItemSubType.MultipartFile, blobId);
        _context.Items.Add(item);
        await _context.SaveChangesAsync();
        var client = new DavDatabaseClient(_context, new ThrowingBlobStore(blobId));

        var ex = await Assert.ThrowsAsync<CorruptedBlobPayloadException>(
            () => client.GetDavMultipartFileAsync(item));

        Assert.Equal(blobId, ex.BlobId);
    }

    private static DavItem NewFile(DavItem.ItemSubType subType, Guid blobId)
    {
        var id = Guid.NewGuid();
        return DavItem.New(
            id, DavItem.Root, $"{id:N}.mkv", 100,
            DavItem.ItemType.UsenetFile, subType,
            null, null, null, blobId);
    }

    /// <summary>
    /// A fake <see cref="IBlobStore"/> whose typed reads throw
    /// <see cref="CorruptedBlobPayloadException"/> for one configured blob id,
    /// simulating a truncated/corrupt on-disk blob without touching the filesystem.
    /// </summary>
    private sealed class ThrowingBlobStore(Guid corruptedBlobId) : IBlobStore
    {
        public Task WriteBlob(Guid id, Stream stream, CancellationToken cancellationToken = default) =>
            Task.FromException(new NotSupportedException());

        public Task WriteBlob<T>(Guid id, T blob, CancellationToken cancellationToken = default) =>
            Task.FromException(new NotSupportedException());

        public Stream? ReadBlob(Guid id) => null;

        public Task<T?> ReadBlob<T>(Guid id) =>
            id == corruptedBlobId
                ? Task.FromException<T?>(new CorruptedBlobPayloadException(
                    id, "/config/blobs/fake", typeof(T), new IOException("simulated corrupt read")))
                : Task.FromResult<T?>(default);

        public bool Exists(Guid id) => false;

        public bool Delete(Guid id) => false;
    }

    [Fact]
    public async Task MoveQueueItemsToTopAsync_BumpsPriorityAndCreatedAt()
    {
        var first = CreateQueueItem("first.nzb", DateTime.UtcNow.AddMinutes(-30), QueueItem.PriorityOption.Normal);
        var second = CreateQueueItem("second.nzb", DateTime.UtcNow.AddMinutes(-20), QueueItem.PriorityOption.Normal);
        var third = CreateQueueItem("third.nzb", DateTime.UtcNow.AddMinutes(-10), QueueItem.PriorityOption.High);

        _context.QueueItems.AddRange(first, second, third);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var before = await _client.GetQueueItems(null);
        Assert.Equal([third.Id, first.Id, second.Id], before.Select(q => q.Id));

        var moved = await _client.MoveQueueItemsToTopAsync([second.Id]);
        Assert.Equal([second.Id], moved);
        _context.ChangeTracker.Clear();

        var after = await _client.GetQueueItems(null);
        Assert.Equal([second.Id, third.Id, first.Id], after.Select(q => q.Id));
        Assert.Equal(QueueItem.PriorityOption.Force, after[0].Priority);
    }

    [Fact]
    public async Task MoveQueueItemsToTopAsync_PreservesRelativeOrderOfMovedIds()
    {
        var first = CreateQueueItem("first.nzb", DateTime.UtcNow.AddMinutes(-30), QueueItem.PriorityOption.Normal);
        var second = CreateQueueItem("second.nzb", DateTime.UtcNow.AddMinutes(-20), QueueItem.PriorityOption.Normal);
        var third = CreateQueueItem("third.nzb", DateTime.UtcNow.AddMinutes(-10), QueueItem.PriorityOption.Normal);

        _context.QueueItems.AddRange(first, second, third);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // Request order: third then second → third should be absolute top.
        await _client.MoveQueueItemsToTopAsync([third.Id, second.Id]);
        _context.ChangeTracker.Clear();

        var after = await _client.GetQueueItems(null);
        Assert.Equal([third.Id, second.Id, first.Id], after.Select(q => q.Id));
        Assert.All(after.Take(2), q => Assert.Equal(QueueItem.PriorityOption.Force, q.Priority));
    }

    [Fact]
    public async Task SwitchQueueItemAsync_MovesSourceToPeerOriginalPosition()
    {
        var first = CreateQueueItem("first.nzb", DateTime.UtcNow.AddMinutes(-3), QueueItem.PriorityOption.Normal);
        var second = CreateQueueItem("second.nzb", DateTime.UtcNow.AddMinutes(-2), QueueItem.PriorityOption.Normal);
        var third = CreateQueueItem("third.nzb", DateTime.UtcNow.AddMinutes(-1), QueueItem.PriorityOption.Normal);
        _context.QueueItems.AddRange(first, second, third);
        await _context.SaveChangesAsync();

        var result = await _client.SwitchQueueItemAsync(third.Id, first.Id.ToString(), []);
        _context.ChangeTracker.Clear();

        Assert.Equal(0, result.Position);
        Assert.Equal(0, result.Priority);
        var ordered = await _client.GetQueueItems(null);
        Assert.Equal([third.Id, first.Id, second.Id], ordered.Select(item => item.Id));
    }

    [Fact]
    public async Task SwitchQueueItemAsync_ReturnsSentinelForInvalidTarget()
    {
        var item = CreateQueueItem("item.nzb", DateTime.UtcNow, QueueItem.PriorityOption.Normal);
        _context.QueueItems.Add(item);
        await _context.SaveChangesAsync();

        var result = await _client.SwitchQueueItemAsync(item.Id, "-1", []);

        Assert.Equal(DavDatabaseClient.QueueSwitchResult.NotMoved, result);
    }

    [Fact]
    public async Task CompletedSymlinkCategoryChildren_AreDistinctAndOrdered()
    {
        var zetaDirectory = DavItem.New(
            Guid.NewGuid(), DavItem.ContentFolder, "zeta", null,
            DavItem.ItemType.Directory, DavItem.ItemSubType.Directory,
            null, null, null, null);
        var alphaDirectory = DavItem.New(
            Guid.NewGuid(), DavItem.ContentFolder, "alpha", null,
            DavItem.ItemType.Directory, DavItem.ItemSubType.Directory,
            null, null, null, null);
        var failedDirectory = DavItem.New(
            Guid.NewGuid(), DavItem.ContentFolder, "failed", null,
            DavItem.ItemType.Directory, DavItem.ItemSubType.Directory,
            null, null, null, null);

        _context.Items.AddRange(zetaDirectory, alphaDirectory, failedDirectory);
        _context.HistoryItems.AddRange(
            CreateHistoryItem("zeta.nzb", zetaDirectory.Id, HistoryItem.DownloadStatusOption.Completed),
            CreateHistoryItem("zeta-duplicate.nzb", zetaDirectory.Id, HistoryItem.DownloadStatusOption.Completed),
            CreateHistoryItem("alpha.nzb", alphaDirectory.Id, HistoryItem.DownloadStatusOption.Completed),
            CreateHistoryItem("failed.nzb", failedDirectory.Id, HistoryItem.DownloadStatusOption.Failed));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var children = await _client.GetCompletedSymlinkCategoryChildren("movies");
        Assert.Equal(new[] { "alpha", "zeta" }, children.Select(item => item.Name));

        var streamedChildren = new List<DavItem>();
        await foreach (var child in _client.GetCompletedSymlinkCategoryChildrenEnumerableAsync("movies"))
            streamedChildren.Add(child);
        Assert.Equal(new[] { "alpha", "zeta" }, streamedChildren.Select(item => item.Name));
    }

    [Fact]
    public async Task GetItemsByIdsBatchedAsync_ReturnsAllItemsAcrossLargeIdSets()
    {
        var ids = Enumerable.Range(0, 600).Select(_ => Guid.NewGuid()).ToList();
        _context.Items.AddRange(ids.Select(id => DavItem.New(id, DavItem.Root, $"{id:N}.mkv", 10,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile, null, null, null, null)));
        await _context.SaveChangesAsync(); _context.ChangeTracker.Clear();
        var found = await _client.GetItemsByIdsBatchedAsync(ids, batchSize: 500);
        Assert.Equal(600, found.Count);
        Assert.Equal(ids.OrderBy(x => x).ToList(), found.Select(x => x.Id).OrderBy(x => x).ToList());
    }

    [Fact]
    public async Task GetFileById_NonGuidName_ReturnsNull()
    {
        Assert.Null(await _client.GetFileById("not-a-guid"));
        Assert.Null(await _client.GetFileById(".."));
        Assert.Null(await _client.GetFileById("favicon.ico"));
    }

    [Fact]
    public async Task GetItemByPathAsync_ResolvesNestedPersistedPaths()
    {
        var directory = DavItem.New(
            Guid.NewGuid(), DavItem.Root, "movies", null,
            DavItem.ItemType.Directory, DavItem.ItemSubType.Directory,
            null, null, null, null);
        var nestedDirectory = DavItem.New(
            Guid.NewGuid(), directory, "science-fiction", null,
            DavItem.ItemType.Directory, DavItem.ItemSubType.Directory,
            null, null, null, null);
        var nestedFile = DavItem.New(
            Guid.NewGuid(), nestedDirectory, "nested.mkv", 250,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
            null, null, null, null);

        _context.Items.AddRange(directory, nestedDirectory, nestedFile);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var hit = await _client.GetItemByPathAsync(nestedFile.Path);
        Assert.NotNull(hit);
        Assert.Equal(nestedFile.Id, hit.Id);
        Assert.Equal("/movies/science-fiction/nested.mkv", hit.Path);

        Assert.Null(await _client.GetItemByPathAsync("/movies/missing.mkv"));
    }

    [Fact]
    public async Task QueueItemProcessor_MovesMissingNzbToFailedHistory()
    {
        var queueItem = new QueueItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            FileName = "missing.nzb",
            JobName = "missing",
            NzbFileSize = 100,
            TotalSegmentBytes = 200,
            Category = "movies",
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None
        };
        _context.QueueItems.Add(queueItem);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var config = new ConfigManager();
        using var healthCheckConnectionGate = new HealthCheckConnectionGate(config);
        var processor = new QueueItemProcessor(
            queueItem,
            queueNzbStream: null,
            _client,
            new FakeNntpClient(new Dictionary<string, byte[]>()),
            config,
            new WebsocketManager(),
            new Progress<int>(),
            healthCheckConnectionGate,
            CancellationToken.None);
        await processor.ProcessAsync();

        Assert.Empty(await _context.QueueItems.AsNoTracking().ToListAsync());
        var historyItem = Assert.Single(
            await _context.HistoryItems.AsNoTracking().ToListAsync());
        Assert.Equal(queueItem.Id, historyItem.Id);
        Assert.Equal(HistoryItem.DownloadStatusOption.Failed, historyItem.DownloadStatus);
        Assert.Equal("The NZB file could not be found.", historyItem.FailMessage);
    }

    private static HistoryItem CreateHistoryItem(
        string fileName,
        Guid downloadDirId,
        HistoryItem.DownloadStatusOption status)
    {
        return new HistoryItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            FileName = fileName,
            JobName = Path.GetFileNameWithoutExtension(fileName),
            Category = "movies",
            DownloadStatus = status,
            DownloadDirId = downloadDirId
        };
    }

    private static QueueItem CreateQueueItem(
        string fileName,
        DateTime createdAt,
        QueueItem.PriorityOption priority)
    {
        return new QueueItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = createdAt,
            FileName = fileName,
            JobName = Path.GetFileNameWithoutExtension(fileName),
            NzbFileSize = 100,
            TotalSegmentBytes = 200,
            Category = "movies",
            Priority = priority,
            PostProcessing = QueueItem.PostProcessingOption.None
        };
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        File.Delete(_databasePath);
    }

    private sealed class TrackingDbContextFactory(DbContextOptions<DavDatabaseContext> options)
        : IDbContextFactory<DavDatabaseContext>
    {
        public TrackingDavDatabaseContext? LastCreatedContext { get; private set; }

        public DavDatabaseContext CreateDbContext()
        {
            LastCreatedContext = new TrackingDavDatabaseContext(options);
            return LastCreatedContext;
        }

        public Task<DavDatabaseContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class TrackingDavDatabaseContext(DbContextOptions<DavDatabaseContext> options)
        : DavDatabaseContext(options)
    {
        public bool IsDisposed { get; private set; }

        public override async ValueTask DisposeAsync()
        {
            IsDisposed = true;
            await base.DisposeAsync();
        }
    }
}
