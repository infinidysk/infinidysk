using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Tasks;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Tasks;

[Collection(nameof(BaseTaskCollection))]
public sealed class RemoveMissingPayloadsTaskTests
{
    [Fact]
    public async Task DryRun_ReportsCandidateWithoutMutatingLinksOrDatabase()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        var libraryDir = NewTempDirectory();
        await using var db = await TempDb.CreateAsync();
        try
        {
            var (_, _, item) = await SeedMissingItemAsync(db.Context);
            var linkPath = await WriteStrmAsync(libraryDir, "episode.strm", item.Id);
            var blobStore = new TrackingBlobStore();
            var arr = new ScriptedArrClient(libraryDir);
            var task = NewTask(db, libraryDir, blobStore, [arr], isDryRun: true);

            Assert.True(await task.Execute());

            Assert.True(await ItemExistsAsync(db, item.Id));
            Assert.True(File.Exists(linkPath));
            Assert.Equal(0, arr.RemoveCalls);
            Assert.Equal(1, task.Stats.Candidates);
            Assert.Equal(1, task.Stats.LinkedFiles);
            Assert.Contains(item.Path, RemoveMissingPayloadsTask.GetAuditReport());
        }
        finally
        {
            await ResetAsync(libraryDir);
        }
    }

    [Fact]
    public async Task Execute_RejectsDryRunApprovalAfterLinkStateChanges()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        var libraryDir = NewTempDirectory();
        await using var db = await TempDb.CreateAsync();
        try
        {
            var (_, _, item) = await SeedMissingItemAsync(db.Context);
            var linkPath = await WriteStrmAsync(libraryDir, "episode.strm", item.Id);
            var blobStore = new TrackingBlobStore();
            var arr = new ScriptedArrClient(libraryDir);
            var dryRun = NewTask(
                db,
                libraryDir,
                blobStore,
                [arr],
                isDryRun: true,
                requirePreviewApproval: true);
            Assert.True(await dryRun.Execute());
            Assert.True(dryRun.Succeeded);
            Assert.NotNull(dryRun.IssuedPreviewToken);
            await BaseTask.ResetRunningTaskForTestsAsync();

            await File.WriteAllTextAsync(
                linkPath,
                $"http://localhost/view/.ids/{Guid.NewGuid()}.mkv");
            var cleanup = NewTask(
                db,
                libraryDir,
                blobStore,
                [arr],
                isDryRun: false,
                requirePreviewApproval: true,
                previewToken: dryRun.IssuedPreviewToken);

            Assert.True(await cleanup.Execute());

            Assert.False(cleanup.Succeeded);
            Assert.Contains("state changed", cleanup.TerminalMessage ?? string.Empty);
            Assert.True(await ItemExistsAsync(db, item.Id));
            Assert.True(File.Exists(linkPath));
            Assert.Equal(0, arr.RemoveCalls);
        }
        finally
        {
            await ResetAsync(libraryDir);
        }
    }

    [Fact]
    public async Task Execute_RemovesEveryVerifiedLinkAndRequestsOneArrSearch()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        var libraryDir = NewTempDirectory();
        await using var db = await TempDb.CreateAsync();
        try
        {
            var (category, release, item) = await SeedMissingItemAsync(db.Context);
            var firstLink = await WriteStrmAsync(libraryDir, "episode.strm", item.Id);
            var secondLink = await WriteStrmAsync(libraryDir, "episode-copy.strm", item.Id);
            var arr = new ScriptedArrClient(libraryDir);
            var task = NewTask(
                db,
                libraryDir,
                new TrackingBlobStore(),
                [arr],
                isDryRun: false);

            Assert.True(await task.Execute());

            Assert.False(await ItemExistsAsync(db, item.Id));
            Assert.False(await ItemExistsAsync(db, release.Id));
            Assert.True(await ItemExistsAsync(db, category.Id));
            Assert.False(File.Exists(firstLink));
            Assert.False(File.Exists(secondLink));
            Assert.Equal(1, arr.RemoveCalls);
            Assert.True(arr.SearchWasAllowed);
            Assert.Equal(1, task.Stats.RemovedItems);
            Assert.Equal(2, task.Stats.RemovedLinks);
            Assert.Equal(1, task.Stats.SearchesRequested);
        }
        finally
        {
            await ResetAsync(libraryDir);
        }
    }

    [Fact]
    public async Task Execute_RemovesUnlinkedMissingPayloadWithoutArrCalls()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        var libraryDir = NewTempDirectory();
        await using var db = await TempDb.CreateAsync();
        try
        {
            var (_, release, item) = await SeedMissingItemAsync(db.Context);
            var task = NewTask(
                db,
                libraryDir,
                new TrackingBlobStore(),
                [],
                isDryRun: false);

            Assert.True(await task.Execute());

            Assert.False(await ItemExistsAsync(db, item.Id));
            Assert.False(await ItemExistsAsync(db, release.Id));
            Assert.Equal(1, task.Stats.RemovedItems);
            Assert.Equal(0, task.Stats.RemovedLinks);
            Assert.Equal(0, task.Stats.SearchesRequested);
        }
        finally
        {
            await ResetAsync(libraryDir);
        }
    }

    [Fact]
    public async Task Execute_RetainsItemWhenGeneratedSidecarStillTargetsItOutsideLibrary()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        var libraryDir = NewTempDirectory();
        var outputRoot = NewTempDirectory();
        await using var db = await TempDb.CreateAsync();
        try
        {
            var (_, _, item) = await SeedMissingItemAsync(db.Context);
            var sidecarPath = Path.Join(outputRoot, "episode.strm");
            item.GeneratedStrmOutputRoot = outputRoot;
            item.GeneratedStrmPath = sidecarPath;
            item.GeneratedStrmTarget =
                $"http://original.test/view/.ids/{item.Id}.mkv";
            await db.Context.SaveChangesAsync();
            await File.WriteAllTextAsync(
                sidecarPath,
                $"http://changed.test/view/.ids/{item.Id}.mkv");
            var task = NewTask(
                db,
                libraryDir,
                new TrackingBlobStore(),
                [],
                isDryRun: false);

            Assert.True(await task.Execute());

            Assert.True(await ItemExistsAsync(db, item.Id));
            Assert.True(File.Exists(sidecarPath));
            Assert.Equal(1, task.Stats.SkippedItems);
            Assert.Contains(
                "generated STRM sidecar could not be removed safely",
                RemoveMissingPayloadsTask.GetAuditReport());
        }
        finally
        {
            await ResetAsync(libraryDir);
            try { Directory.Delete(outputRoot, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public async Task Execute_AllowsConfiguredLibraryRootThatIsSymlink()
    {
        if (OperatingSystem.IsWindows())
            return;

        await BaseTask.ResetRunningTaskForTestsAsync();
        var physicalLibraryDir = NewTempDirectory();
        var libraryDir = Path.Join(
            Path.GetTempPath(),
            $"nzbdav-missing-payload-link-{Guid.NewGuid():N}");
        Directory.CreateSymbolicLink(libraryDir, physicalLibraryDir);
        await using var db = await TempDb.CreateAsync();
        try
        {
            var (_, _, item) = await SeedMissingItemAsync(db.Context);
            var linkPath = await WriteStrmAsync(libraryDir, "episode.strm", item.Id);
            var task = NewTask(
                db,
                libraryDir,
                new TrackingBlobStore(),
                [],
                isDryRun: false);

            Assert.True(await task.Execute());

            Assert.False(await ItemExistsAsync(db, item.Id));
            Assert.False(File.Exists(linkPath));
            Assert.Equal(1, task.Stats.RemovedLinks);
        }
        finally
        {
            await BaseTask.ResetRunningTaskForTestsAsync();
            RemoveMissingPayloadsTask.ClearAuditForTests();
            RemoveMissingPayloadsTask.ClearPreviewApprovalForTests();
            try { Directory.Delete(libraryDir); } catch (IOException) { /* best effort */ }
            try { Directory.Delete(physicalLibraryDir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public async Task Execute_LeavesItemWhenArrOwnershipCannotBeChecked()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        var libraryDir = NewTempDirectory();
        await using var db = await TempDb.CreateAsync();
        try
        {
            var (_, _, item) = await SeedMissingItemAsync(db.Context);
            var linkPath = await WriteStrmAsync(libraryDir, "episode.strm", item.Id);
            var arr = new ScriptedArrClient(
                libraryDir,
                rootFailure: new HttpRequestException("connection refused"));
            var task = NewTask(
                db,
                libraryDir,
                new TrackingBlobStore(),
                [arr],
                isDryRun: false);

            Assert.True(await task.Execute());

            Assert.True(await ItemExistsAsync(db, item.Id));
            Assert.True(File.Exists(linkPath));
            Assert.Equal(0, arr.RemoveCalls);
            Assert.Equal(1, task.Stats.SkippedItems);
            Assert.Contains("ownership is ambiguous", RemoveMissingPayloadsTask.GetAuditReport());
        }
        finally
        {
            await ResetAsync(libraryDir);
        }
    }

    [Fact]
    public async Task Execute_LeavesItemWhenOneArrMatchesAndAnotherIsUnreachable()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        var libraryDir = NewTempDirectory();
        await using var db = await TempDb.CreateAsync();
        try
        {
            var (_, _, item) = await SeedMissingItemAsync(db.Context);
            var linkPath = await WriteStrmAsync(libraryDir, "episode.strm", item.Id);
            var matching = new ScriptedArrClient(
                libraryDir,
                host: "http://sonarr.test");
            var unreachable = new ScriptedArrClient(
                libraryDir,
                rootFailure: new HttpRequestException("connection refused"),
                host: "http://radarr.test");
            var task = NewTask(
                db,
                libraryDir,
                new TrackingBlobStore(),
                [matching, unreachable],
                isDryRun: false);

            Assert.True(await task.Execute());

            Assert.True(await ItemExistsAsync(db, item.Id));
            Assert.True(File.Exists(linkPath));
            Assert.Equal(0, matching.RemoveCalls);
            Assert.Equal(1, task.Stats.SkippedItems);
        }
        finally
        {
            await ResetAsync(libraryDir);
        }
    }

    [Fact]
    public async Task Execute_LeavesItemWhenDistinctArrMediaFilesMatchItsLinks()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        var libraryDir = NewTempDirectory();
        await using var db = await TempDb.CreateAsync();
        try
        {
            var (_, _, item) = await SeedMissingItemAsync(db.Context);
            var firstLink = await WriteStrmAsync(libraryDir, "episode.strm", item.Id);
            var secondLink = await WriteStrmAsync(libraryDir, "episode-copy.strm", item.Id);
            var firstArr = new ScriptedArrClient(
                libraryDir,
                host: "http://sonarr-one.test",
                matchPath: firstLink,
                fileId: 201);
            var secondArr = new ScriptedArrClient(
                libraryDir,
                host: "http://sonarr-two.test",
                matchPath: secondLink,
                fileId: 202);
            var task = NewTask(
                db,
                libraryDir,
                new TrackingBlobStore(),
                [firstArr, secondArr],
                isDryRun: false);

            Assert.True(await task.Execute());

            Assert.True(await ItemExistsAsync(db, item.Id));
            Assert.True(File.Exists(firstLink));
            Assert.True(File.Exists(secondLink));
            Assert.Equal(0, firstArr.RemoveCalls);
            Assert.Equal(0, secondArr.RemoveCalls);
            Assert.Contains(
                "multiple distinct Arr media-file records",
                RemoveMissingPayloadsTask.GetAuditReport());
        }
        finally
        {
            await ResetAsync(libraryDir);
        }
    }

    [Fact]
    public async Task Execute_RechecksPayloadAfterArrLookupAndLeavesRecoveredItem()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        var libraryDir = NewTempDirectory();
        await using var db = await TempDb.CreateAsync();
        try
        {
            var payloadId = Guid.NewGuid();
            var (_, _, item) = await SeedMissingItemAsync(db.Context, payloadId);
            var linkPath = await WriteStrmAsync(libraryDir, "episode.strm", item.Id);
            var blobStore = new TrackingBlobStore();
            var arr = new ScriptedArrClient(
                libraryDir,
                onFind: () => blobStore.Add(payloadId));
            var task = NewTask(db, libraryDir, blobStore, [arr], isDryRun: false);

            Assert.True(await task.Execute());

            Assert.True(await ItemExistsAsync(db, item.Id));
            Assert.True(File.Exists(linkPath));
            Assert.Equal(0, arr.RemoveCalls);
            Assert.Contains("RECOVERED", RemoveMissingPayloadsTask.GetAuditReport());
        }
        finally
        {
            await ResetAsync(libraryDir);
        }
    }

    [Fact]
    public async Task Execute_RevalidatesLinkBeforeArrMutation()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        var libraryDir = NewTempDirectory();
        await using var db = await TempDb.CreateAsync();
        try
        {
            var (_, _, item) = await SeedMissingItemAsync(db.Context);
            var linkPath = await WriteStrmAsync(libraryDir, "episode.strm", item.Id);
            var replacementId = Guid.NewGuid();
            var arr = new ScriptedArrClient(
                libraryDir,
                onFind: () => File.WriteAllText(
                    linkPath,
                    $"http://localhost/view/.ids/{replacementId}.mkv"));
            var task = NewTask(
                db,
                libraryDir,
                new TrackingBlobStore(),
                [arr],
                isDryRun: false);

            Assert.True(await task.Execute());

            Assert.True(await ItemExistsAsync(db, item.Id));
            Assert.True(File.Exists(linkPath));
            Assert.Contains(replacementId.ToString(), await File.ReadAllTextAsync(linkPath));
            Assert.Equal(0, arr.RemoveCalls);
            Assert.Contains(
                "changed after it was scanned",
                RemoveMissingPayloadsTask.GetAuditReport());
        }
        finally
        {
            await ResetAsync(libraryDir);
        }
    }

    [Fact]
    public async Task Execute_RestoresQuarantinedLinkWhenArrRemovalFails()
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        var libraryDir = NewTempDirectory();
        await using var db = await TempDb.CreateAsync();
        try
        {
            var (_, _, item) = await SeedMissingItemAsync(db.Context);
            var linkPath = await WriteStrmAsync(libraryDir, "episode.strm", item.Id);
            var arr = new ScriptedArrClient(
                libraryDir,
                removeFailure: new HttpRequestException("arr unavailable"));
            var task = NewTask(
                db,
                libraryDir,
                new TrackingBlobStore(),
                [arr],
                isDryRun: false);

            Assert.True(await task.Execute());

            Assert.True(await ItemExistsAsync(db, item.Id));
            Assert.True(File.Exists(linkPath));
            Assert.Single(Directory.EnumerateFiles(libraryDir));
            Assert.Equal(1, arr.RemoveCalls);
            Assert.Contains("Arr cleanup failed", RemoveMissingPayloadsTask.GetAuditReport());
        }
        finally
        {
            await ResetAsync(libraryDir);
        }
    }

    private static RemoveMissingPayloadsTask NewTask(
        TempDb db,
        string libraryDir,
        IBlobStore blobStore,
        IReadOnlyList<ArrClient> arrClients,
        bool isDryRun,
        bool requirePreviewApproval = false,
        string? previewToken = null)
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = libraryDir },
            new ConfigItem { ConfigName = ConfigKeys.RcloneMountDir, ConfigValue = "/mnt/infinidysk" },
        ]);
        return new RemoveMissingPayloadsTask(
            config,
            new WebsocketManager(),
            new ArrReplacementSearchBudget(),
            isDryRun,
            createContext: db.CreateContext,
            blobStore: blobStore,
            arrClients: arrClients,
            previewToken: previewToken,
            requirePreviewApproval: requirePreviewApproval,
            progressHeartbeatInterval: TimeSpan.FromHours(1));
    }

    private static async Task<(DavItem Category, DavItem Release, DavItem Item)> SeedMissingItemAsync(
        DavDatabaseContext context,
        Guid? payloadId = null)
    {
        var category = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            $"category-{Guid.NewGuid():N}",
            null,
            DavItem.ItemType.Directory,
            DavItem.ItemSubType.Directory,
            null,
            null,
            null,
            null);
        var release = DavItem.New(
            Guid.NewGuid(),
            category,
            $"release-{Guid.NewGuid():N}",
            null,
            DavItem.ItemType.Directory,
            DavItem.ItemSubType.Directory,
            null,
            null,
            null,
            null);
        var item = DavItem.New(
            Guid.NewGuid(),
            release,
            "episode.mkv",
            100,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            DateTimeOffset.UtcNow.AddDays(-1),
            null,
            null,
            payloadId ?? Guid.NewGuid());
        context.Items.AddRange(category, release, item);
        await context.SaveChangesAsync();
        return (category, release, item);
    }

    private static async Task<string> WriteStrmAsync(
        string libraryDir,
        string name,
        Guid itemId)
    {
        var path = Path.Join(libraryDir, name);
        await File.WriteAllTextAsync(
            path,
            $"http://localhost/view/.ids/{itemId}.mkv");
        return path;
    }

    private static async Task<bool> ItemExistsAsync(TempDb db, Guid id)
    {
        await using var context = db.CreateContext();
        return await context.Items.AsNoTracking().AnyAsync(item => item.Id == id);
    }

    private static string NewTempDirectory()
    {
        var path = Path.Join(Path.GetTempPath(), $"nzbdav-missing-payload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task ResetAsync(string libraryDir)
    {
        await BaseTask.ResetRunningTaskForTestsAsync();
        RemoveMissingPayloadsTask.ClearAuditForTests();
        RemoveMissingPayloadsTask.ClearPreviewApprovalForTests();
        try { Directory.Delete(libraryDir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private sealed class TrackingBlobStore : IBlobStore
    {
        private readonly HashSet<Guid> _ids = [];

        public void Add(Guid id) => _ids.Add(id);
        public bool Exists(Guid id) => _ids.Contains(id);
        public bool Delete(Guid id) => _ids.Remove(id);
        public Stream? ReadBlob(Guid id) => null;
        public Task<T?> ReadBlob<T>(Guid id) => Task.FromResult(default(T));
        public Task WriteBlob(
            Guid id,
            Stream stream,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new NotSupportedException());
        public Task WriteBlob<T>(
            Guid id,
            T blob,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new NotSupportedException());
    }

    private sealed class ScriptedArrClient : ArrClient
    {
        private readonly string _root;
        private readonly Exception? _rootFailure;
        private readonly Exception? _removeFailure;
        private readonly Action? _onFind;
        private readonly string? _matchPath;
        private readonly int _fileId;

        public ScriptedArrClient(
            string root,
            Exception? rootFailure = null,
            Action? onFind = null,
            Exception? removeFailure = null,
            string host = "http://arr.test",
            string? matchPath = null,
            int fileId = 201)
            : base(host, "test-key")
        {
            _root = root;
            _rootFailure = rootFailure;
            _onFind = onFind;
            _removeFailure = removeFailure;
            _matchPath = matchPath;
            _fileId = fileId;
        }

        public int RemoveCalls { get; private set; }
        public bool SearchWasAllowed { get; private set; }

        public override Task<List<ArrRootFolder>> GetRootFolders(CancellationToken ct = default) =>
            _rootFailure is not null
                ? Task.FromException<List<ArrRootFolder>>(_rootFailure)
                : Task.FromResult(new List<ArrRootFolder> { new() { Path = _root } });

        public override Task<ArrMediaFileMatch?> FindMediaFileAsync(
            string symlinkOrStrmPath,
            CancellationToken ct = default)
        {
            _onFind?.Invoke();
            if (_matchPath is not null
                && !string.Equals(
                    Path.GetFullPath(_matchPath),
                    Path.GetFullPath(symlinkOrStrmPath),
                    StringComparison.Ordinal))
            {
                return Task.FromResult<ArrMediaFileMatch?>(null);
            }

            return Task.FromResult<ArrMediaFileMatch?>(
                new(ArrMediaKind.Episode, _fileId, [301]));
        }

        public override Task<ArrMissingPayloadCleanupOutcome> RemoveMissingPayloadAndSearchAsync(
            ArrMediaFileMatch match,
            Func<IReadOnlyList<string>, bool>? shouldRequestSearch = null,
            CancellationToken ct = default)
        {
            RemoveCalls++;
            if (_removeFailure is not null)
                return Task.FromException<ArrMissingPayloadCleanupOutcome>(_removeFailure);
            SearchWasAllowed = shouldRequestSearch?.Invoke(match.MediaKeys) ?? true;
            return Task.FromResult(
                SearchWasAllowed
                    ? ArrMissingPayloadCleanupOutcome.RemovedSearchRequested
                    : ArrMissingPayloadCleanupOutcome.RemovedSearchWithheld);
        }
    }

    private sealed class TempDb : IAsyncDisposable
    {
        private readonly string _path;

        private TempDb(string path, DavDatabaseContext context)
        {
            _path = path;
            Context = context;
        }

        public DavDatabaseContext Context { get; }

        public DavDatabaseContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<DavDatabaseContext>()
                .UseSqlite($"Data Source={_path}")
                .AddInterceptors(new SqliteMainDbPragmas())
                .ReplaceService<
                    IMigrationsSqlGenerator,
                    SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
                .Options;
            return new DavDatabaseContext(options);
        }

        public static async Task<TempDb> CreateAsync()
        {
            var path = Path.Join(
                Path.GetTempPath(),
                $"nzbdav-missing-payload-{Guid.NewGuid():N}.sqlite");
            var options = new DbContextOptionsBuilder<DavDatabaseContext>()
                .UseSqlite($"Data Source={path}")
                .AddInterceptors(new SqliteMainDbPragmas())
                .ReplaceService<
                    IMigrationsSqlGenerator,
                    SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
                .Options;
            var context = new DavDatabaseContext(options);
            await context.Database.MigrateAsync();
            return new TempDb(path, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            try { File.Delete(_path); } catch (IOException) { /* best effort */ }
            try { File.Delete(_path + "-wal"); } catch (IOException) { /* best effort */ }
            try { File.Delete(_path + "-shm"); } catch (IOException) { /* best effort */ }
        }
    }
}
