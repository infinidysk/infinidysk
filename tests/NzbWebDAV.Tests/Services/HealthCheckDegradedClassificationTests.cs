using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Websocket;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Services;

/// <summary>
/// Full-coverage health checks of tolerance-eligible video files classify confirmed
/// segment holes as Degraded (persist holes, skip repair) or Failed (today's repair
/// path), instead of aborting on the first miss (issue #461).
/// </summary>
[Collection(nameof(ConfigPathCollection))]
public sealed class HealthCheckDegradedClassificationTests : IAsyncLifetime
{
    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-health-degraded-{Guid.NewGuid():N}");
    private string? _previousConfigPath;
    private DbContextOptions<DavDatabaseContext> _options = null!;
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _dbClient = null!;
    private ConfigManager _configManager = null!;
    private StreamingFailureTracker _failureTracker = null!;
    private RepairPatchStore _patchStore = null!;
    private UsenetStreamingClient _usenet = null!;
    private QueueManager _queueManager = null!;

    public async Task InitializeAsync()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(_configRoot);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configRoot);

        _options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={Path.Join(_configRoot, "db.sqlite")}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        _context = new DavDatabaseContext(_options);
        await _context.Database.MigrateAsync();
        _dbClient = new DavDatabaseClient(_context);

        _configManager = new ConfigManager();
        _configManager.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue = JsonSerializer.Serialize(new UsenetProviderConfig()),
            },
            new ConfigItem { ConfigName = ConfigKeys.RepairEnable, ConfigValue = "true" },
            new ConfigItem
            {
                ConfigName = ConfigKeys.MediaLibraryDir,
                ConfigValue = Path.Join(_configRoot, "library"),
            },
        ]);
        Directory.CreateDirectory(Path.Join(_configRoot, "library"));

        _failureTracker = new StreamingFailureTracker();
        _patchStore = new RepairPatchStore(Path.Join(_configRoot, "patches"), 1024 * 1024);
        await _patchStore.CatalogLoadTask;

        var websocketManager = new WebsocketManager();
        _usenet = new UsenetStreamingClient(
            _configManager,
            websocketManager,
            new ProviderUsageTracker(),
            new MetricsWriter(),
            new ProviderBytesTracker(),
            new StreamTraceBuffer(100),
            new ActiveReadRegistry());
        _queueManager = new QueueManager(
            _usenet,
            _configManager,
            websocketManager,
            new ProviderUsageTracker(),
            new WatchdogLog(),
            new QueueItemSourceTracker(),
            new BenchmarkGate(),
            startLoop: false);
    }

    public async Task DisposeAsync()
    {
        _queueManager.Dispose();
        _usenet.Dispose();
        await _context.DisposeAsync();
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        try { Directory.Delete(_configRoot, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task UrgentRepair_UsesFullyAttributedStreamingFailureIdsAsPar2Seed()
    {
        var segments = NewSegmentIds(3);
        var sizes = new long[] { 10_000, 10_000, 10_000 };
        var (item, _) = await AddVideoFileAsync("movie.mkv", segments, sizes);
        item.NextHealthCheck = DateTimeOffset.UnixEpoch;
        await _context.SaveChangesAsync();
        _configManager.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.RepairPar2Enabled, ConfigValue = "true" },
        ]);
        _failureTracker.RecordAttributedFailure(item.Id, segments[1]);
        var (service, par2) = await NewServiceAsync(NewFakeClient(segments, missing: []), par2Outcome: true);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        Assert.Equal([segments[1]], Assert.Single(par2.Requests));
        Assert.Equal(StreamingFailureSnapshot.Empty, _failureTracker.GetSnapshot(item.Id));
    }

    [Fact]
    public async Task BoundedHole_MarksDegraded_PersistsHoles_AndSkipsRepair()
    {
        var segments = NewSegmentIds(6);
        var sizes = new long[] { 10_000, 10_000, 50, 10_000, 10_000, 10_000 };
        var (item, oldBlobId) = await AddVideoFileAsync("movie.mkv", segments, sizes);
        var fake = NewFakeClient(segments, missing: [2]);
        _failureTracker.RecordFailure(item.Id);
        var (service, par2) = await NewServiceAsync(fake, par2Outcome: false);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        // Degraded row, no repair
        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.HealthResult.Degraded, row.Result);
        Assert.Equal(HealthCheckResult.RepairAction.None, row.RepairStatus);
        Assert.Contains("1 missing/corrupt segment(s) (largest run 1", row.Message);
        Assert.Contains("within tolerance for a resync-tolerant container", row.Message);
        Assert.Equal(1, _context.Items.AsNoTracking().Count(x => x.Id == item.Id));

        // PAR2 was offered the full hole list first
        Assert.Equal([segments[2]], Assert.Single(par2.Requests));

        // Holes persisted via blob swap; old blob queued for cleanup by the trigger
        var persisted = ReloadItem(item.Id);
        Assert.NotNull(persisted.FileBlobId);
        Assert.NotEqual(oldBlobId, persisted.FileBlobId);
        var blob = await BlobStore.ReadBlob<DavNzbFile>(persisted.FileBlobId!.Value);
        Assert.Equal([2], blob!.MissingSegmentIndices!);
        Assert.Null(blob.ContainerClass); // extension-mapped class is not persisted
        Assert.Contains(_context.BlobCleanupItems.AsNoTracking().ToList(), x => x.Id == oldBlobId);

        // Stays on the age-doubling recheck schedule; no Repair and no fail-fast seed;
        // the streaming-failure count is confirmed damage, so it is NOT cleared.
        Assert.NotNull(persisted.NextHealthCheck);
        Assert.True(persisted.NextHealthCheck > DateTimeOffset.UtcNow);
        Assert.Equal(1, _failureTracker.GetFailureCount(item.Id));
        HealthCheckService.CheckCachedMissingSegmentIds([segments[2]]);
    }

    [Fact]
    public async Task IdenticalRecheck_DoesNotRewriteBlob()
    {
        var segments = NewSegmentIds(6);
        var sizes = new long[] { 10_000, 10_000, 50, 10_000, 10_000, 10_000 };
        var (item, oldBlobId) = await AddVideoFileAsync(
            "movie.mkv", segments, sizes, preExistingHoles: [2]);
        var fake = NewFakeClient(segments, missing: [2]);
        var (service, _) = await NewServiceAsync(fake, par2Outcome: false);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.HealthResult.Degraded, row.Result);
        Assert.Equal(oldBlobId, ReloadItem(item.Id).FileBlobId);
        Assert.Empty(_context.BlobCleanupItems.AsNoTracking().ToList());
    }

    [Fact]
    public async Task RunOverCap_Fails_SeedsFailFastWithAllMisses_AndRepairs()
    {
        var segments = NewSegmentIds(6);
        var sizes = new long[] { 10_000, 10_000, 50, 50, 50, 10_000 };
        var (item, oldBlobId) = await AddVideoFileAsync("movie.mkv", segments, sizes);
        var fake = NewFakeClient(segments, missing: [2, 3, 4]);
        var (service, _) = await NewServiceAsync(fake, par2Outcome: false);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.HealthResult.Unhealthy, row.Result);
        Assert.Equal(HealthCheckResult.RepairAction.ActionNeeded, row.RepairStatus);
        Assert.Contains("symlink or strm-file", row.Message);
        Assert.Equal(oldBlobId, ReloadItem(item.Id).FileBlobId);
        foreach (var index in new[] { 2, 3, 4 })
            Assert.Throws<UsenetArticleNotFoundException>(
                () => HealthCheckService.CheckCachedMissingSegmentIds([segments[index]]));
    }

    [Fact]
    public async Task HoleAtSegmentZero_Fails_WithoutProbing()
    {
        var segments = NewSegmentIds(6);
        var sizes = new long[] { 50, 10_000, 10_000, 10_000, 10_000, 10_000 };
        var (item, _) = await AddVideoFileAsync("movie.mp4", segments, sizes);
        var fake = NewFakeClient(segments, missing: [0]);
        var (service, _) = await NewServiceAsync(fake, par2Outcome: false);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.HealthResult.Unhealthy, row.Result);
        Assert.Empty(fake.BodyRequestCounts);
        Assert.Throws<UsenetArticleNotFoundException>(
            () => HealthCheckService.CheckCachedMissingSegmentIds([segments[0]]));
    }

    [Fact]
    public async Task PrimaryMissServedByFallback_IsNotAHole()
    {
        var segments = NewSegmentIds(6);
        var sizes = new long[] { 10_000, 10_000, 50, 10_000, 10_000, 10_000 };
        var fallbackIds = new string[segments.Length][];
        for (var i = 0; i < fallbackIds.Length; i++) fallbackIds[i] = [];
        fallbackIds[2] = ["alt-seg2@test"];
        var (item, oldBlobId) = await AddVideoFileAsync(
            "movie.mkv", segments, sizes, fallbackIds: fallbackIds);
        var fake = NewFakeClient(segments, missing: [2]);
        fake.Serve("alt-seg2@test", new byte[50]);
        _failureTracker.RecordFailure(item.Id);
        var (service, _) = await NewServiceAsync(fake, par2Outcome: false);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.HealthResult.Healthy, row.Result);
        Assert.Equal(HealthCheckResult.RepairAction.None, row.RepairStatus);
        Assert.Equal("File is healthy.", row.Message);
        Assert.Equal(oldBlobId, ReloadItem(item.Id).FileBlobId);
        Assert.Equal(0, _failureTracker.GetFailureCount(item.Id)); // healthy path clears
        Assert.True(fake.StatRequestCounts.ContainsKey("alt-seg2@test"));
    }

    [Fact]
    public async Task Par2Success_RecordsHealthyViaPar2_AndClearsRecord()
    {
        var segments = NewSegmentIds(6);
        var sizes = new long[] { 10_000, 10_000, 50, 10_000, 10_000, 10_000 };
        var (item, oldBlobId) = await AddVideoFileAsync(
            "movie.mkv", segments, sizes, preExistingHoles: [2]);
        var fake = NewFakeClient(segments, missing: [2]);
        _failureTracker.RecordFailure(item.Id);
        var (service, par2) = await NewServiceAsync(fake, par2Outcome: true);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.HealthResult.Healthy, row.Result);
        Assert.Equal(HealthCheckResult.RepairAction.RepairedViaPar2, row.RepairStatus);
        Assert.Equal("Missing segment(s) repaired from PAR2 parity.", row.Message);
        Assert.Equal([segments[2]], Assert.Single(par2.Requests));

        var persisted = ReloadItem(item.Id);
        Assert.NotEqual(oldBlobId, persisted.FileBlobId);
        var blob = await BlobStore.ReadBlob<DavNzbFile>(persisted.FileBlobId!.Value);
        Assert.Null(blob!.MissingSegmentIndices);
        Assert.Contains(_context.BlobCleanupItems.AsNoTracking().ToList(), x => x.Id == oldBlobId);
        Assert.Equal(0, _failureTracker.GetFailureCount(item.Id));
    }

    [Fact]
    public async Task Par2VerifiedClean_RecordsHealthyWithoutRepairStatus()
    {
        var segments = NewSegmentIds(6);
        var sizes = new long[] { 10_000, 10_000, 50, 10_000, 10_000, 10_000 };
        var (item, _) = await AddVideoFileAsync(
            "movie.mkv", segments, sizes, preExistingHoles: [2]);
        var fake = NewFakeClient(segments, missing: [2]);
        _failureTracker.RecordFailure(item.Id);
        var (service, par2) = await NewServiceAsync(
            fake, Par2RepairOutcome.VerifiedClean);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.HealthResult.Healthy, row.Result);
        Assert.Equal(HealthCheckResult.RepairAction.None, row.RepairStatus);
        Assert.Equal(
            "PAR2 verified every file slice and found no damage.",
            row.Message);
        Assert.Equal([segments[2]], Assert.Single(par2.Requests));
    }

    [Fact]
    public async Task Par2Success_RunsWithoutEnabledArrWhenArrPreferenceIsOff()
    {
        _configManager.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.RepairPar2PreferredOverArr, ConfigValue = "false" },
        ]);
        var segments = NewSegmentIds(6);
        var sizes = new long[] { 10_000, 10_000, 50, 10_000, 10_000, 10_000 };
        var (item, _) = await AddVideoFileAsync(
            "movie.mkv", segments, sizes, preExistingHoles: [2]);
        var fake = NewFakeClient(segments, missing: [2]);
        var (service, par2) = await NewServiceAsync(fake, par2Outcome: true);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.RepairAction.RepairedViaPar2, row.RepairStatus);
        Assert.Equal([segments[2]], Assert.Single(par2.Requests));
    }

    [Fact]
    public async Task LinkedFile_WithoutEnabledArr_IsLeftInPlace()
    {
        _configManager.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.RepairPar2Enabled, ConfigValue = "false" },
        ]);
        var segments = NewSegmentIds(6);
        var sizes = Enumerable.Repeat(10_000L, segments.Length).ToArray();
        var (item, _) = await AddVideoFileAsync("movie.mkv", segments, sizes);
        var libraryPath = Path.Join(_configRoot, "library", "movie.strm");
        await File.WriteAllTextAsync(
            libraryPath,
            $"http://localhost:3000/view/.ids/{item.Id}.mkv");
        var fake = NewFakeClient(segments, missing: [0]);
        var (service, _) = await NewServiceAsync(fake, par2Outcome: false);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.RepairAction.ActionNeeded, row.RepairStatus);
        Assert.Contains("No enabled Radarr/Sonarr instances are configured", row.Message);
        Assert.Equal(1, _context.Items.AsNoTracking().Count(x => x.Id == item.Id));
        Assert.True(File.Exists(libraryPath));
    }

    [Fact]
    public async Task PartialSample_UsesLegacyAbortOnFirstMissPath()
    {
        var segments = NewSegmentIds(HealthCheckService.SampleFloor + 1000);
        var sizes = Enumerable.Repeat(100L, segments.Length).ToArray();
        var (item, oldBlobId) = await AddVideoFileAsync("movie.mkv", segments, sizes);
        // Index 50 is inside the head window, so it is always part of the sample.
        var fake = NewFakeClient(segments, missing: [50]);
        var (service, par2) = await NewServiceAsync(fake, par2Outcome: false);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        // Legacy path: single-segment PAR2 attempt, then repair; no hole record written.
        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.HealthResult.Unhealthy, row.Result);
        Assert.Equal(HealthCheckResult.RepairAction.ActionNeeded, row.RepairStatus);
        Assert.Equal([segments[50]], Assert.Single(par2.Requests));
        Assert.Equal(oldBlobId, ReloadItem(item.Id).FileBlobId);
        Assert.Throws<UsenetArticleNotFoundException>(
            () => HealthCheckService.CheckCachedMissingSegmentIds([segments[50]]));
    }

    [Fact]
    public async Task ToleranceDisabled_UsesLegacyPath()
    {
        _configManager.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.RepairDegradedToleranceEnabled, ConfigValue = "false" },
        ]);
        var segments = NewSegmentIds(6);
        var sizes = new long[] { 10_000, 10_000, 50, 10_000, 10_000, 10_000 };
        var (item, oldBlobId) = await AddVideoFileAsync("movie.mkv", segments, sizes);
        var fake = NewFakeClient(segments, missing: [2]);
        var (service, _) = await NewServiceAsync(fake, par2Outcome: false);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.HealthResult.Unhealthy, row.Result);
        Assert.Equal(oldBlobId, ReloadItem(item.Id).FileBlobId);
    }

    [Fact]
    public async Task RepairDisabled_UsesLegacyPath()
    {
        _configManager.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.RepairEnable, ConfigValue = "false" },
        ]);
        var segments = NewSegmentIds(6);
        var sizes = new long[] { 10_000, 10_000, 50, 10_000, 10_000, 10_000 };
        var (item, oldBlobId) = await AddVideoFileAsync("movie.mkv", segments, sizes);
        var fake = NewFakeClient(segments, missing: [2]);
        var (service, _) = await NewServiceAsync(fake, par2Outcome: false);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.HealthResult.Unhealthy, row.Result);
        Assert.Equal(oldBlobId, ReloadItem(item.Id).FileBlobId);
    }

    [Theory]
    [InlineData("movie.iso")]
    [InlineData("movie.avi")]
    public async Task IneligibleContainer_UsesLegacyPath(string fileName)
    {
        var segments = NewSegmentIds(6);
        var sizes = new long[] { 10_000, 10_000, 50, 10_000, 10_000, 10_000 };
        var (item, oldBlobId) = await AddVideoFileAsync(fileName, segments, sizes);
        var fake = NewFakeClient(segments, missing: [2]);
        var (service, _) = await NewServiceAsync(fake, par2Outcome: false);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.HealthResult.Unhealthy, row.Result);
        Assert.Equal(oldBlobId, ReloadItem(item.Id).FileBlobId);
    }

    [Fact]
    public async Task MoovAtEndMp4_TailMiss_Fails()
    {
        var segments = NewSegmentIds(6);
        var sizes = new long[] { 10_000, 10_000, 10_000, 10_000, 10_000, 50 };
        var (item, oldBlobId) = await AddVideoFileAsync("movie.mp4", segments, sizes);
        var fake = NewFakeClient(segments, missing: [5]);
        fake.Serve(segments[0], Mp4Head(Box("ftyp", 16), Box("mdat", 100)));
        var (service, _) = await NewServiceAsync(fake, par2Outcome: false);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.HealthResult.Unhealthy, row.Result);
        Assert.Equal(HealthCheckResult.RepairAction.ActionNeeded, row.RepairStatus);
        Assert.Equal(1, fake.BodyRequestCounts.GetValueOrDefault(segments[0])); // probed once
        var afterProbe = ReloadItem(item.Id);
        Assert.NotEqual(oldBlobId, afterProbe.FileBlobId); // Failed still persists the probe
        var blob = await BlobStore.ReadBlob<DavNzbFile>(afterProbe.FileBlobId!.Value);
        Assert.Null(blob!.MissingSegmentIndices);
        Assert.Equal((byte)MediaContainerClass.Mp4MoovAtEnd, blob.ContainerClass);
        Assert.Equal(0L, blob.CriticalHeadEndExclusive);
        Assert.Throws<UsenetArticleNotFoundException>(
            () => HealthCheckService.CheckCachedMissingSegmentIds([segments[5]]));
    }

    [Fact]
    public async Task FastStartMp4_Hole_ProbesOnceAndReusesPersistedClass()
    {
        var segments = NewSegmentIds(6);
        var sizes = new long[] { 10_000, 10_000, 10_000, 10_000, 50, 10_000 };
        var (item, oldBlobId) = await AddVideoFileAsync("movie.mp4", segments, sizes);
        var fake = NewFakeClient(segments, missing: [4]);
        fake.Serve(segments[0], Mp4Head(Box("ftyp", 16), Box("moov", 24), Box("mdat", 100)));
        var (service, _) = await NewServiceAsync(fake, par2Outcome: false);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.HealthResult.Degraded, row.Result);
        Assert.Contains("fast-start MP4 container", row.Message);
        var afterFirst = ReloadItem(item.Id);
        Assert.NotEqual(oldBlobId, afterFirst.FileBlobId);
        var firstBlob = await BlobStore.ReadBlob<DavNzbFile>(afterFirst.FileBlobId!.Value);
        Assert.Equal([4], firstBlob!.MissingSegmentIndices!);
        Assert.Equal((byte)MediaContainerClass.Mp4FastStart, firstBlob.ContainerClass);
        Assert.Equal(56, firstBlob.CriticalHeadEndExclusive);
        Assert.Equal(1, fake.BodyRequestCounts.GetValueOrDefault(segments[0]));

        // Second check with identical holes: persisted class is reused (no second BODY)
        // and the unchanged record is not rewritten.
        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        Assert.Equal(2, GetHealthRows(item.Id).Count);
        Assert.Equal(1, fake.BodyRequestCounts.GetValueOrDefault(segments[0]));
        Assert.Equal(afterFirst.FileBlobId, ReloadItem(item.Id).FileBlobId);
    }

    [Fact]
    public async Task FastStartMp4_HoleOverlappingMoov_FailsAndReusesPersistedExtentWithoutSecondBody()
    {
        var segments = NewSegmentIds(6);
        // Segment 1 is tiny so the hole stays inside the byte-share cap; the fail
        // must come from overlapping the moov, not from MaxMissingBytePercent.
        var sizes = new long[] { 10_000, 50, 10_000, 10_000, 10_000, 10_000 };
        var (item, oldBlobId) = await AddVideoFileAsync("movie.mp4", segments, sizes);
        var fake = NewFakeClient(segments, missing: [1]);
        // ftyp (24 bytes) + moov declared 15_000 → exclusive end 15_024, which
        // overlaps segment 1 (starts at 10_000). The header is in segment 0.
        fake.Serve(segments[0], Mp4Head(Box("ftyp", 16), BoxHeader("moov", 15_000)));
        var (service, _) = await NewServiceAsync(fake, par2Outcome: false);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.HealthResult.Unhealthy, row.Result);
        Assert.Equal(HealthCheckResult.RepairAction.ActionNeeded, row.RepairStatus);
        Assert.Equal(1, fake.BodyRequestCounts.GetValueOrDefault(segments[0]));
        var afterFirst = ReloadItem(item.Id);
        Assert.NotEqual(oldBlobId, afterFirst.FileBlobId);
        var firstBlob = await BlobStore.ReadBlob<DavNzbFile>(afterFirst.FileBlobId!.Value);
        Assert.Null(firstBlob!.MissingSegmentIndices);
        Assert.Equal((byte)MediaContainerClass.Mp4FastStart, firstBlob.ContainerClass);
        Assert.Equal(15_024, firstBlob.CriticalHeadEndExclusive);
        Assert.Throws<UsenetArticleNotFoundException>(
            () => HealthCheckService.CheckCachedMissingSegmentIds([segments[1]]));

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        Assert.Equal(2, GetHealthRows(item.Id).Count);
        Assert.All(
            GetHealthRows(item.Id),
            result => Assert.Equal(HealthCheckResult.HealthResult.Unhealthy, result.Result));
        Assert.Equal(1, fake.BodyRequestCounts.GetValueOrDefault(segments[0]));
    }

    [Fact]
    public async Task RecoveredFile_RecordsHealthy_AndClearsRecordKeepingContainerClass()
    {
        var segments = NewSegmentIds(6);
        var sizes = new long[] { 10_000, 10_000, 10_000, 10_000, 50, 10_000 };
        var (item, _) = await AddVideoFileAsync("movie.mp4", segments, sizes);
        var fake = NewFakeClient(segments, missing: [4]);
        fake.Serve(segments[0], Mp4Head(Box("ftyp", 16), Box("moov", 24), Box("mdat", 100)));
        var (service, _) = await NewServiceAsync(fake, par2Outcome: false);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);
        var afterFirst = ReloadItem(item.Id);
        Assert.Equal(HealthCheckResult.HealthResult.Degraded, Assert.Single(GetHealthRows(item.Id)).Result);

        // Provider-side restoration: the segment is back on the next sweep.
        fake.Serve(segments[4], new byte[50]);
        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        var rows = GetHealthRows(item.Id);
        Assert.Equal(2, rows.Count);
        var healthyRow = Assert.Single(rows, x => x.Result == HealthCheckResult.HealthResult.Healthy);
        Assert.Equal(HealthCheckResult.RepairAction.None, healthyRow.RepairStatus);

        var afterSecond = ReloadItem(item.Id);
        Assert.NotEqual(afterFirst.FileBlobId, afterSecond.FileBlobId);
        var blob = await BlobStore.ReadBlob<DavNzbFile>(afterSecond.FileBlobId!.Value);
        Assert.Null(blob!.MissingSegmentIndices);
        Assert.Equal((byte)MediaContainerClass.Mp4FastStart, blob.ContainerClass); // survives clears
        Assert.Equal(56, blob.CriticalHeadEndExclusive);
        Assert.Contains(_context.BlobCleanupItems.AsNoTracking().ToList(), x => x.Id == afterFirst.FileBlobId);
    }

    [Fact]
    public async Task Par2PatchedSegments_AreFilteredFromStat_ButCountTowardCoverage()
    {
        var segments = NewSegmentIds(4);
        var sizes = new long[] { 10_000, 100, 50, 10_000 };
        var (item, _) = await AddVideoFileAsync("movie.mkv", segments, sizes);
        CommitPatch(segments[1], (int)sizes[1]);
        var fake = NewFakeClient(segments, missing: [2]);
        var (service, _) = await NewServiceAsync(fake, par2Outcome: false);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        // The patched segment is never STATed, yet the check still classifies
        // (full coverage is measured on the sampled list, not the STAT list).
        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.HealthResult.Degraded, row.Result);
        Assert.False(fake.StatRequestCounts.ContainsKey(segments[1]));
        var persisted = ReloadItem(item.Id);
        var blob = await BlobStore.ReadBlob<DavNzbFile>(persisted.FileBlobId!.Value);
        Assert.Equal([2], blob!.MissingSegmentIndices!);
    }

    [Fact]
    public async Task RecordedCorrupt_StatClean_MarksDegradedWithinCaps()
    {
        var segments = NewSegmentIds(6);
        var sizes = new long[] { 10_000, 10_000, 50, 10_000, 10_000, 10_000 };
        var (item, _) = await AddVideoFileAsync(
            "movie.mkv", segments, sizes, preExistingCorrupt: [2]);
        var fake = NewFakeClient(segments, missing: [], corrupt: [2]);
        var (service, par2) = await NewServiceAsync(fake, par2Outcome: false);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.HealthResult.Degraded, row.Result);
        Assert.Contains("1 missing/corrupt segment(s)", row.Message);
        Assert.Equal([segments[2]], Assert.Single(par2.Requests));
        var blob = await BlobStore.ReadBlob<DavNzbFile>(ReloadItem(item.Id).FileBlobId!.Value);
        Assert.Equal([2], blob!.CorruptSegmentIndices!);
        Assert.Null(blob.MissingSegmentIndices);
    }

    [Fact]
    public async Task RecordedCorruptOverCap_FailsAndRepairs()
    {
        var segments = NewSegmentIds(8);
        var sizes = Enumerable.Repeat(10_000L, 8).ToArray();
        var corrupt = new[] { 1, 2, 3, 4, 5, 6 };
        var (item, _) = await AddVideoFileAsync(
            "movie.mkv", segments, sizes, preExistingCorrupt: corrupt);
        var fake = NewFakeClient(segments, missing: [], corrupt: corrupt);
        var (service, _) = await NewServiceAsync(fake, par2Outcome: false);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.HealthResult.Unhealthy, row.Result);
        Assert.Equal(HealthCheckResult.RepairAction.ActionNeeded, row.RepairStatus);
    }

    [Fact]
    public async Task RecordedCorruptAtSegmentZero_Fails()
    {
        var segments = NewSegmentIds(4);
        var sizes = new long[] { 10_000, 10_000, 10_000, 10_000 };
        var (item, _) = await AddVideoFileAsync(
            "movie.mkv", segments, sizes, preExistingCorrupt: [0]);
        var fake = NewFakeClient(segments, missing: [], corrupt: [0]);
        var (service, _) = await NewServiceAsync(fake, par2Outcome: false);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.HealthResult.Unhealthy, row.Result);
    }

    [Fact]
    public async Task RecordedCorruptUnionsWithStatHoles()
    {
        var segments = NewSegmentIds(6);
        var sizes = new long[] { 10_000, 10_000, 50, 10_000, 50, 10_000 };
        var (item, _) = await AddVideoFileAsync(
            "movie.mkv", segments, sizes, preExistingCorrupt: [4]);
        var fake = NewFakeClient(segments, missing: [2], corrupt: [4]);
        var (service, par2) = await NewServiceAsync(fake, par2Outcome: false);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.HealthResult.Degraded, row.Result);
        Assert.Equal([segments[2], segments[4]], Assert.Single(par2.Requests));
        var blob = await BlobStore.ReadBlob<DavNzbFile>(ReloadItem(item.Id).FileBlobId!.Value);
        Assert.Equal([2], blob!.MissingSegmentIndices!);
        Assert.Equal([4], blob.CorruptSegmentIndices!);
    }

    [Fact]
    public async Task PatchedCorruptIndices_AreExcludedFromClassification()
    {
        var segments = NewSegmentIds(4);
        var sizes = new long[] { 10_000, 100, 50, 10_000 };
        var (item, oldBlobId) = await AddVideoFileAsync(
            "movie.mkv", segments, sizes, preExistingCorrupt: [1]);
        CommitPatch(segments[1], (int)sizes[1]);
        var fake = NewFakeClient(segments, missing: []);
        var (service, par2) = await NewServiceAsync(fake, par2Outcome: false);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.HealthResult.Healthy, row.Result);
        Assert.Empty(par2.Requests);
        var blob = await BlobStore.ReadBlob<DavNzbFile>(ReloadItem(item.Id).FileBlobId!.Value);
        Assert.Null(blob!.CorruptSegmentIndices);
        Assert.NotEqual(oldBlobId, ReloadItem(item.Id).FileBlobId);
    }

    [Fact]
    public async Task ReconfirmationProbe_ClearsNowCleanCorruptRecord()
    {
        var segments = NewSegmentIds(4);
        var sizes = new long[] { 10_000, 10_000, 50, 10_000 };
        var (item, _) = await AddVideoFileAsync(
            "movie.mkv", segments, sizes, preExistingCorrupt: [2]);
        var fake = NewFakeClient(segments, missing: []);
        var (service, par2) = await NewServiceAsync(fake, par2Outcome: false);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.HealthResult.Healthy, row.Result);
        Assert.Empty(par2.Requests);
        var blob = await BlobStore.ReadBlob<DavNzbFile>(ReloadItem(item.Id).FileBlobId!.Value);
        Assert.Null(blob!.CorruptSegmentIndices);
        Assert.Null(blob.MissingSegmentIndices);
    }

    [Fact]
    public async Task ReconfirmationProbe_MismatchedSegmentId_KeepsCorruptRecord()
    {
        var segments = NewSegmentIds(4);
        var sizes = new long[] { 10_000, 10_000, 50, 10_000 };
        var (item, _) = await AddVideoFileAsync(
            "movie.mkv", segments, sizes, preExistingCorrupt: [2]);
        var fake = NewFakeClient(segments, missing: []);
        fake.ForcedResponseSegmentId = "wrong@example.com";
        var (service, par2) = await NewServiceAsync(fake, par2Outcome: false);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.HealthResult.Degraded, row.Result);
        Assert.Equal([segments[2]], Assert.Single(par2.Requests));
        var blob = await BlobStore.ReadBlob<DavNzbFile>(ReloadItem(item.Id).FileBlobId!.Value);
        Assert.Equal([2], blob!.CorruptSegmentIndices!);
    }

    [Fact]
    public async Task HealthyBranchDetour_ClearsStaleCorruptRecordWhenProbesPass()
    {
        var segments = NewSegmentIds(4);
        var sizes = new long[] { 10_000, 10_000, 10_000, 10_000 };
        var (item, oldBlobId) = await AddVideoFileAsync(
            "movie.mkv", segments, sizes, preExistingHoles: [1], preExistingCorrupt: [2]);
        var fake = NewFakeClient(segments, missing: []);
        var (service, _) = await NewServiceAsync(fake, par2Outcome: false);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.HealthResult.Healthy, row.Result);
        var persisted = ReloadItem(item.Id);
        Assert.NotEqual(oldBlobId, persisted.FileBlobId);
        var blob = await BlobStore.ReadBlob<DavNzbFile>(persisted.FileBlobId!.Value);
        Assert.Null(blob!.MissingSegmentIndices);
        Assert.Null(blob.CorruptSegmentIndices);
    }

    [Fact]
    public async Task PayloadOutOfMemory_IsDeferredWithoutStartingRepair()
    {
        var segments = NewSegmentIds(4);
        var (item, _) = await AddVideoFileAsync("movie.mkv", segments, [10_000, 10_000, 10_000, 10_000]);
        var (service, par2) = await NewServiceAsync(NewFakeClient(segments, missing: []), par2Outcome: false);
        var previousStore = BlobStore.Current;
        BlobStore.Use(new OutOfMemoryBlobStore());
        try
        {
            await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);
        }
        finally
        {
            BlobStore.Use(previousStore);
        }

        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.HealthResult.Unhealthy, row.Result);
        Assert.Equal(HealthCheckResult.RepairAction.ActionNeeded, row.RepairStatus);
        Assert.Contains("segment metadata exceeded", row.Message);
        Assert.Empty(par2.Requests);
        var persisted = ReloadItem(item.Id);
        Assert.Equal(item.Id, persisted.Id);
        Assert.True(persisted.NextHealthCheck > DateTimeOffset.UtcNow.AddHours(23));
    }

    private static string[] NewSegmentIds(int count) =>
        Enumerable.Range(0, count).Select(i => $"seg{i}-{Guid.NewGuid():N}@test").ToArray();

    private static FakeNntpClient NewFakeClient(string[] segments, int[] missing, int[]? corrupt = null)
    {
        var corruptSet = new HashSet<int>(corrupt ?? []);
        var present = segments
            .Where((_, index) => !missing.Contains(index))
            .ToDictionary(id => id, _ => new byte[128], StringComparer.Ordinal);
        if (corruptSet.Count == 0)
            return new FakeNntpClient(present);

        return new FakeNntpClient(
            present,
            useCachedYencStreams: true,
            decodedStreamFactory: (id, bytes) =>
            {
                var index = Array.IndexOf(segments, id);
                if (index >= 0 && corruptSet.Contains(index))
                    return new ThrowingReadStream(id);
                return new MemoryStream(bytes, writable: false);
            });
    }

    private sealed class ThrowingReadStream(string segmentId) : MemoryStream
    {
        private UsenetCorruptArticleException CreateException() =>
            new(segmentId, "provider-a", new InvalidDataException("CRC mismatch"));

        public override int Read(byte[] buffer, int offset, int count) =>
            throw CreateException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(CreateException());
    }

    private sealed class OutOfMemoryBlobStore : IBlobStore
    {
        public Task WriteBlob(Guid id, Stream stream, CancellationToken cancellationToken = default) =>
            Task.FromException(new NotSupportedException());

        public Task WriteBlob<T>(Guid id, T blob, CancellationToken cancellationToken = default) =>
            Task.FromException(new NotSupportedException());

        public Stream? ReadBlob(Guid id) => null;

        public Task<T?> ReadBlob<T>(Guid id) =>
            Task.FromException<T?>(new OutOfMemoryException("simulated payload allocation failure"));

        public void Delete(Guid id) => throw new NotSupportedException();
    }

    private async Task<(HealthCheckService Service, ScriptedPar2RepairService Par2)> NewServiceAsync(
        FakeNntpClient fake,
        Par2RepairOutcome par2Outcome)
    {
        await _usenet.ReplaceUnderlyingClientForTestsAsync(fake);
        var par2 = new ScriptedPar2RepairService(_configManager, _patchStore, par2Outcome);
        var service = new HealthCheckService(
            _configManager,
            _usenet,
            new WebsocketManager(),
            new BenchmarkGate(),
            _failureTracker,
            _queueManager,
            par2,
            _patchStore,
            new ArrReplacementSearchBudget());
        return (service, par2);
    }

    private Task<(HealthCheckService Service, ScriptedPar2RepairService Par2)> NewServiceAsync(
        FakeNntpClient fake,
        bool par2Outcome) =>
        NewServiceAsync(
            fake,
            par2Outcome ? Par2RepairOutcome.Repaired : Par2RepairOutcome.NotRepaired);

    private async Task<(DavItem Item, Guid BlobId)> AddVideoFileAsync(
        string name,
        string[] segmentIds,
        long[] segmentSizes,
        string[][]? fallbackIds = null,
        int[]? preExistingHoles = null,
        int[]? preExistingCorrupt = null,
        byte? containerClass = null,
        long? criticalHeadEndExclusive = null)
    {
        var itemId = Guid.NewGuid();
        var ranges = new LongRange[segmentSizes.Length];
        long offset = 0;
        for (var i = 0; i < segmentSizes.Length; i++)
        {
            ranges[i] = LongRange.FromStartAndSize(offset, segmentSizes[i]);
            offset += segmentSizes[i];
        }

        var blobId = Guid.NewGuid();
        await BlobStore.WriteBlob(blobId, new DavNzbFile
        {
            Id = itemId,
            SegmentIds = segmentIds,
            SegmentByteRanges = ranges,
            SegmentFallbackIds = fallbackIds,
            MissingSegmentIndices = preExistingHoles,
            CorruptSegmentIndices = preExistingCorrupt,
            ContainerClass = containerClass,
            CriticalHeadEndExclusive = criticalHeadEndExclusive,
        });

        var item = DavItem.New(
            itemId,
            DavItem.ContentFolder,
            name,
            fileSize: offset,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            releaseDate: DateTimeOffset.UtcNow.AddDays(-1),
            lastHealthCheck: null,
            historyItemId: null,
            fileBlobId: blobId);
        _context.Items.Add(item);
        await _context.SaveChangesAsync();
        return (item, blobId);
    }

    private void CommitPatch(string segmentId, int size)
    {
        _patchStore.CommitPatch(segmentId, new byte[size], new UsenetYencHeader
        {
            FileName = "movie.mkv",
            FileSize = size,
            LineLength = 128,
            PartNumber = 1,
            TotalParts = 1,
            PartSize = size,
            PartOffset = 0,
        });
    }

    private List<HealthCheckResult> GetHealthRows(Guid itemId) =>
        _context.HealthCheckResults.AsNoTracking()
            .Where(x => x.DavItemId == itemId)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToList();

    private DavItem ReloadItem(Guid itemId)
    {
        using var context = new DavDatabaseContext(_options);
        return context.Items.AsNoTracking().Single(x => x.Id == itemId);
    }

    private static byte[] Box(string type, int payloadSize)
    {
        var bytes = new byte[8 + payloadSize];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)(8 + payloadSize));
        Encoding.ASCII.GetBytes(type).CopyTo(bytes, 4);
        return bytes;
    }

    private static byte[] BoxHeader(string type, uint declaredSize)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, declaredSize);
        Encoding.ASCII.GetBytes(type).CopyTo(bytes, 4);
        return bytes;
    }

    private static byte[] Mp4Head(params byte[][] boxes) =>
        boxes.SelectMany(box => box).ToArray();

    private sealed class ScriptedPar2RepairService(
        ConfigManager configManager,
        RepairPatchStore store,
        Par2RepairOutcome repairOutcome) : Par2RepairService(configManager, null!, store)
    {
        public List<string[]> Requests { get; } = [];

        public override Task<Par2RepairOutcome> TryPar2RepairAsync(
            DavItem davItem, IReadOnlyList<string>? missingSegmentIds, CancellationToken ct)
        {
            Requests.Add(missingSegmentIds?.ToArray() ?? []);
            return Task.FromResult(repairOutcome);
        }
    }
}
