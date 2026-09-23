using System.Text;
using System.Text.Json;
using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Api.SabControllers.AddFile;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Api;

[Collection(nameof(ConfigPathCollection))]
public sealed class AddFileDuplicateReplaceTests : IAsyncLifetime
{
    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-addfile-cfg-{Guid.NewGuid():N}");
    private string? _previousConfigPath;
    private DbContextOptions<DavDatabaseContext> _options = null!;
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _dbClient = null!;
    private QueueManager _queueManager = null!;
    private ConfigManager _configManager = null!;
    private WebsocketManager _websocketManager = null!;

    public async Task InitializeAsync()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(_configRoot);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configRoot);

        _options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={DavDatabaseContext.DatabaseFilePath}")
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
        ]);

        _websocketManager = new WebsocketManager();
        var usenet = new UsenetStreamingClient(
            _configManager,
            _websocketManager,
            new ProviderUsageTracker(),
            new MetricsWriter(),
            new ProviderBytesTracker(),
            new StreamTraceBuffer(100),
            new ActiveReadRegistry());
        _queueManager = QueueManager.CreateForTests(
            usenet,
            _configManager,
            _websocketManager,
            new ProviderUsageTracker(),
            new WatchdogLog(),
            new QueueItemSourceTracker(),
            new BenchmarkGate());
    }

    public async Task DisposeAsync()
    {
        _queueManager.Dispose();
        await _context.DisposeAsync();
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        try { Directory.Delete(_configRoot, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task AddFileAsync_ReplacesExistingQueueItemWithSameCategoryAndFileName()
    {
        var existingId = Guid.NewGuid();
        const string fileName = "Sliders.S01E01.part.1.Pilot.nzb";
        const string category = "tv";

        _context.QueueItems.Add(new QueueItem
        {
            Id = existingId,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            FileName = fileName,
            JobName = "Sliders.S01E01.part.1.Pilot",
            NzbFileSize = 10,
            TotalSegmentBytes = 10,
            Category = category,
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None,
        });
        _context.NzbNames.Add(new NzbName { Id = existingId, FileName = fileName });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var controller = CreateController();
        var response = await controller.AddFileAsync(CreateRequest(fileName, category));

        Assert.True(response.Status);
        Assert.Single(response.NzoIds);
        var newId = Guid.Parse(response.NzoIds[0]);
        Assert.NotEqual(existingId, newId);

        Assert.Null(await _context.QueueItems.AsNoTracking()
            .SingleOrDefaultAsync(q => q.Id == existingId));
        var replacement = await _context.QueueItems.AsNoTracking()
            .SingleAsync(q => q.Category == category && q.FileName == fileName);
        Assert.Equal(newId, replacement.Id);

        // Watch-folder create reads the new QueueItem from the request change tracker.
        Assert.Contains(_context.ChangeTracker.Entries<QueueItem>(),
            e => e.Entity.Id == newId);
        Assert.NotNull(BlobStore.ReadBlob(newId));
    }

    [Fact]
    public async Task AddFileAsync_FailedReplacementPreservesExistingQueueItem()
    {
        var existingId = Guid.NewGuid();
        const string fileName = "Sliders.S01E01.part.99.Pilot.nzb";
        const string category = "tv";

        await SeedQueueItemAsync(existingId, fileName, category);

        var controller = CreateController();
        var invalidRequest = CreateRequest(fileName, category, contentBytes: Encoding.UTF8.GetBytes("not valid xml or gzip"));

        await Assert.ThrowsAsync<ApiValidationException>(() => controller.AddFileAsync(invalidRequest));

        var existingItem = await _context.QueueItems.AsNoTracking()
            .SingleOrDefaultAsync(q => q.Id == existingId);
        Assert.NotNull(existingItem);
        Assert.Equal(fileName, existingItem.FileName);
    }

    [Fact]
    public async Task AddFileAsync_RetriesSaveAfterUniqueConflictInsertedBetweenPreCheckAndSave()
    {
        const string fileName = "Sliders.S01E01.part.2.Pilot.nzb";
        const string category = "tv";
        var conflictingId = Guid.NewGuid();

        var controller = CreateController();
        controller.AfterDuplicatePreCheckHook = async () =>
        {
            await using var raceCtx = new DavDatabaseContext(_options);
            raceCtx.QueueItems.Add(new QueueItem
            {
                Id = conflictingId,
                CreatedAt = DateTime.UtcNow,
                FileName = fileName,
                JobName = "Sliders.S01E01.part.2.Pilot",
                NzbFileSize = 10,
                TotalSegmentBytes = 10,
                Category = category,
                Priority = QueueItem.PriorityOption.Normal,
                PostProcessing = QueueItem.PostProcessingOption.None,
            });
            raceCtx.NzbNames.Add(new NzbName { Id = conflictingId, FileName = fileName });
            await raceCtx.SaveChangesAsync();
        };

        var response = await controller.AddFileAsync(CreateRequest(fileName, category));

        Assert.True(response.Status);
        var newId = Guid.Parse(Assert.Single(response.NzoIds));
        Assert.NotEqual(conflictingId, newId);

        Assert.Null(await _context.QueueItems.AsNoTracking()
            .SingleOrDefaultAsync(q => q.Id == conflictingId));
        Assert.Equal(newId, (await _context.QueueItems.AsNoTracking()
            .SingleAsync(q => q.Category == category && q.FileName == fileName)).Id);
        Assert.NotNull(BlobStore.ReadBlob(newId));
        Assert.Contains(_context.ChangeTracker.Entries<QueueItem>(),
            e => e.Entity.Id == newId);
    }

    [Fact]
    public async Task AddFileAsync_NoReplace_KeepsExistingQueueItem()
    {
        var existingId = Guid.NewGuid();
        const string fileName = "Sliders.S01E01.part.3.Pilot.nzb";
        const string category = "tv";
        await SeedQueueItemAsync(existingId, fileName, category);

        var controller = CreateController();
        var error = await Assert.ThrowsAsync<BadHttpRequestException>(() =>
            controller.AddFileAsync(CreateRequest(
                fileName, category, replaceExisting: false)));

        Assert.Contains("already exists", error.Message);
        Assert.Equal(existingId, (await _context.QueueItems.AsNoTracking()
            .SingleAsync(q => q.Category == category && q.FileName == fileName)).Id);
    }

    [Fact]
    public async Task AddFileAsync_NoReplace_KeepsQueueItemInsertedAfterPreCheck()
    {
        var conflictingId = Guid.NewGuid();
        const string fileName = "Sliders.S01E01.part.5.Pilot.nzb";
        const string category = "tv";
        var controller = CreateController();
        controller.AfterDuplicatePreCheckHook = async () =>
        {
            await using var raceContext = new DavDatabaseContext(_options);
            raceContext.QueueItems.Add(CreateQueueItem(conflictingId, fileName, category));
            raceContext.NzbNames.Add(new NzbName { Id = conflictingId, FileName = fileName });
            await raceContext.SaveChangesAsync();
        };

        var error = await Assert.ThrowsAsync<BadHttpRequestException>(() =>
            controller.AddFileAsync(CreateRequest(
                fileName, category, replaceExisting: false)));

        Assert.Contains("already exists", error.Message);
        Assert.Equal(conflictingId, (await _context.QueueItems.AsNoTracking()
            .SingleAsync(q => q.Category == category && q.FileName == fileName)).Id);
    }

    [Fact]
    public async Task AddFileAsync_UsesCallerAssignedNzoId()
    {
        var assignedId = Guid.NewGuid();
        const string fileName = "Sliders.S01E01.part.4.Pilot.nzb";

        var response = await CreateController().AddFileAsync(
            CreateRequest(fileName, "tv", nzoId: assignedId));

        Assert.Equal(assignedId.ToString(), Assert.Single(response.NzoIds));
        Assert.True(await _context.QueueItems.AsNoTracking().AnyAsync(q => q.Id == assignedId));
        Assert.Null((await _context.QueueItems.AsNoTracking().SingleAsync(q => q.Id == assignedId)).ArrDownloadId);
    }

    [Fact]
    public async Task AddFileAsync_ExternalSabAdd_PersistsReturnedNzoIdAsArrDownloadId()
    {
        var response = await CreateController().AddFileAsync(
            CreateRequest("Arr.Grab.nzb", "tv", origin: NzbSubmissionOrigin.ExternalSabAdd));

        var nzoId = Guid.Parse(Assert.Single(response.NzoIds));
        var queued = await _context.QueueItems.AsNoTracking().SingleAsync(q => q.Id == nzoId);
        Assert.Equal(nzoId, queued.ArrDownloadId);
    }

    [Fact]
    public async Task AddFileAsync_RejectsAtLimit_AndResumesAtThreshold()
    {
        ConfigureAdmission(maxItems: 2, resumeThreshold: 1);
        await SeedQueueItem("first.nzb", "tv");
        await SeedQueueItem("second.nzb", "tv");

        var rejected = await CreateController().AddFileAsync(CreateRequest("third.nzb", "tv"));

        Assert.False(rejected.Status);
        Assert.Empty(rejected.NzoIds);
        Assert.Contains("Queue is full", rejected.Error);
        Assert.Equal(2, await _context.QueueItems.CountAsync());

        var first = await _context.QueueItems.SingleAsync(x => x.FileName == "first.nzb");
        _context.QueueItems.Remove(first);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var accepted = await CreateController().AddFileAsync(CreateRequest("third.nzb", "tv"));

        Assert.True(accepted.Status);
        Assert.Single(accepted.NzoIds);
        Assert.Equal(2, await _context.QueueItems.CountAsync());
    }

    [Fact]
    public async Task AddFileAsync_AllowsDuplicateReplacementWhileQueueIsFull()
    {
        ConfigureAdmission(maxItems: 2, resumeThreshold: 1);
        await SeedQueueItem("replace-me.nzb", "tv");
        await SeedQueueItem("other.nzb", "tv");

        var response = await CreateController()
            .AddFileAsync(CreateRequest("replace-me.nzb", "tv"));

        Assert.True(response.Status);
        Assert.Single(response.NzoIds);
        Assert.Equal(2, await _context.QueueItems.CountAsync());
        Assert.Single(await _context.QueueItems
            .Where(x => x.FileName == "replace-me.nzb" && x.Category == "tv")
            .ToListAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddFileAsync_StaleReplacement_UsesCurrentQueueCapacity(bool fillFreedSlot)
    {
        ConfigureAdmission(maxItems: 1, resumeThreshold: 1);
        var backupRoot = Path.Join(_configRoot, "submission-backups");
        _configManager.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.ApiNzbBackupEnabled,
                ConfigValue = "true",
            },
            new ConfigItem
            {
                ConfigName = ConfigKeys.ApiNzbBackupLocation,
                ConfigValue = backupRoot,
            },
        ]);

        var originalId = Guid.NewGuid();
        var replacementId = Guid.NewGuid();
        Guid? otherId = null;
        await SeedQueueItemAsync(originalId, "same.nzb", "tv");
        var controller = CreateController();
        controller.AfterDuplicatePreCheckHook = async () =>
        {
            await RemoveQueuedItemAsync(originalId);
            if (!fillFreedSlot) return;

            await using var otherContext = new DavDatabaseContext(_options);
            var other = await CreateController(new DavDatabaseClient(otherContext))
                .AddFileAsync(CreateRequest("other.nzb", "tv"));
            Assert.True(other.Status);
            otherId = Guid.Parse(Assert.Single(other.NzoIds));
        };

        var response = await controller.AddFileAsync(
            CreateRequest("same.nzb", "tv", nzoId: replacementId));

        Assert.Equal(!fillFreedSlot, response.Status);
        var remaining = Assert.Single(await _context.QueueItems.AsNoTracking().ToListAsync());
        Assert.Equal(fillFreedSlot ? otherId!.Value : replacementId, remaining.Id);
        Assert.Equal(!fillFreedSlot, BlobStore.Exists(replacementId));
        Assert.Equal(!fillFreedSlot,
            await _context.NzbNames.AsNoTracking().AnyAsync(name => name.Id == replacementId));
        Assert.Equal(!fillFreedSlot, File.Exists(Path.Join(backupRoot, "tv", "same.nzb")));

        if (fillFreedSlot)
        {
            Assert.Empty(response.NzoIds);
            Assert.Equal("Queue is full (1 of 1 items); submissions resume at or below 1.", response.Error);
            Assert.True(BlobStore.Exists(otherId!.Value));
            Assert.True(File.Exists(Path.Join(backupRoot, "tv", "other.nzb")));
        }
        else
        {
            Assert.Equal(replacementId.ToString(), Assert.Single(response.NzoIds));
            Assert.Null(response.Error);
        }
    }

    [Fact]
    public async Task AddFileAsync_StaleReplacement_AccountsForPendingOrdinaryAdmission()
    {
        ConfigureAdmission(maxItems: 1, resumeThreshold: 1);
        var originalId = Guid.NewGuid();
        var replacementId = Guid.NewGuid();
        await SeedQueueItemAsync(originalId, "same.nzb", "tv");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var staleHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStale = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ordinaryHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOrdinary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<AddFileResponse>? staleTask = null;
        Task<AddFileResponse>? ordinaryTask = null;

        try
        {
            await using var staleContext = new DavDatabaseContext(_options);
            var staleController = CreateController(new DavDatabaseClient(staleContext));
            staleController.AfterDuplicatePreCheckHook = async () =>
            {
                staleHeld.TrySetResult();
                await releaseStale.Task.WaitAsync(timeout.Token);
            };
            staleTask = staleController.AddFileAsync(CreateRequest(
                "same.nzb", "tv", nzoId: replacementId, cancellationToken: timeout.Token));
            await staleHeld.Task.WaitAsync(timeout.Token);
            await RemoveQueuedItemAsync(originalId);

            await using var ordinaryContext = new DavDatabaseContext(_options);
            var ordinaryController = CreateController(new DavDatabaseClient(ordinaryContext));
            ordinaryController.AfterDuplicatePreCheckHook = async () =>
            {
                ordinaryHeld.TrySetResult();
                await releaseOrdinary.Task.WaitAsync(timeout.Token);
            };
            ordinaryTask = ordinaryController.AddFileAsync(CreateRequest(
                "other.nzb", "tv", cancellationToken: timeout.Token));
            await ordinaryHeld.Task.WaitAsync(timeout.Token);

            releaseStale.TrySetResult();
            var staleResponse = await staleTask.WaitAsync(timeout.Token);
            Assert.False(staleResponse.Status);
            Assert.Contains("Queue is full", staleResponse.Error);
            await using (var verifyContext = new DavDatabaseContext(_options))
                Assert.Empty(await verifyContext.QueueItems.AsNoTracking().ToListAsync());

            releaseOrdinary.TrySetResult();
            var ordinaryResponse = await ordinaryTask.WaitAsync(timeout.Token);
            Assert.True(ordinaryResponse.Status);
            var ordinaryId = Guid.Parse(Assert.Single(ordinaryResponse.NzoIds));
            await using var finalContext = new DavDatabaseContext(_options);
            Assert.Equal(ordinaryId,
                (await finalContext.QueueItems.AsNoTracking().SingleAsync()).Id);
            Assert.False(BlobStore.Exists(replacementId));
            Assert.False(await finalContext.NzbNames.AsNoTracking()
                .AnyAsync(name => name.Id == replacementId));
        }
        finally
        {
            releaseStale.TrySetResult();
            releaseOrdinary.TrySetResult();
            await DrainAsync(staleTask, ordinaryTask);
        }
    }

    [Fact]
    public async Task AddFileAsync_ConcurrentStaleReplacements_DoNotExceedLimit()
    {
        ConfigureAdmission(maxItems: 2, resumeThreshold: 2);
        var firstOriginalId = Guid.NewGuid();
        var secondOriginalId = Guid.NewGuid();
        var firstReplacementId = Guid.Parse("00000001-0000-0000-0000-000000000000");
        var secondReplacementId = Guid.Parse("00000002-0000-0000-0000-000000000000");
        await SeedQueueItemAsync(firstOriginalId, "first.nzb", "tv");
        await SeedQueueItemAsync(secondOriginalId, "second.nzb", "tv");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var firstHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReplacements = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<AddFileResponse>? firstTask = null;
        Task<AddFileResponse>? secondTask = null;

        try
        {
            await using var firstContext = new DavDatabaseContext(_options);
            var firstController = CreateController(new DavDatabaseClient(firstContext));
            firstController.AfterDuplicatePreCheckHook = async () =>
            {
                firstHeld.TrySetResult();
                await releaseReplacements.Task.WaitAsync(timeout.Token);
            };
            firstTask = firstController.AddFileAsync(CreateRequest(
                "first.nzb", "tv", nzoId: firstReplacementId, cancellationToken: timeout.Token));

            await using var secondContext = new DavDatabaseContext(_options);
            var secondController = CreateController(new DavDatabaseClient(secondContext));
            secondController.AfterDuplicatePreCheckHook = async () =>
            {
                secondHeld.TrySetResult();
                await releaseReplacements.Task.WaitAsync(timeout.Token);
            };
            secondTask = secondController.AddFileAsync(CreateRequest(
                "second.nzb", "tv", nzoId: secondReplacementId, cancellationToken: timeout.Token));

            await Task.WhenAll(firstHeld.Task, secondHeld.Task).WaitAsync(timeout.Token);
            await RemoveQueuedItemAsync(firstOriginalId);
            await RemoveQueuedItemAsync(secondOriginalId);
            var other = await CreateController().AddFileAsync(CreateRequest("other.nzb", "tv"));
            Assert.True(other.Status);

            releaseReplacements.TrySetResult();
            var responses = await Task.WhenAll(firstTask, secondTask).WaitAsync(timeout.Token);
            Assert.Single(responses, response => response.Status);
            var rejected = Assert.Single(responses, response => !response.Status);
            Assert.Contains("Queue is full", rejected.Error);
            Assert.Equal(2, await _context.QueueItems.AsNoTracking().CountAsync());

            var rejectedId = ReferenceEquals(rejected, responses[0])
                ? firstReplacementId
                : secondReplacementId;
            Assert.False(BlobStore.Exists(rejectedId));
            Assert.False(await _context.NzbNames.AsNoTracking()
                .AnyAsync(name => name.Id == rejectedId));
        }
        finally
        {
            releaseReplacements.TrySetResult();
            await DrainAsync(firstTask, secondTask);
        }
    }

    [Fact]
    public async Task AddFileAsync_StaleReplacement_RespectsPausedResumeThreshold()
    {
        ConfigureAdmission(maxItems: 3, resumeThreshold: 1);
        var originalId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var thirdId = Guid.NewGuid();
        await SeedQueueItemAsync(originalId, "same.nzb", "tv");
        await SeedQueueItemAsync(secondId, "second.nzb", "tv");
        await SeedQueueItemAsync(thirdId, "third.nzb", "tv");

        var full = await CreateController().AddFileAsync(CreateRequest("fourth.nzb", "tv"));
        Assert.False(full.Status);

        var replacementId = Guid.NewGuid();
        var controller = CreateController();
        controller.AfterDuplicatePreCheckHook = () => RemoveQueuedItemAsync(originalId);
        var rejected = await controller.AddFileAsync(CreateRequest(
            "same.nzb", "tv", nzoId: replacementId));
        Assert.False(rejected.Status);
        Assert.Contains("Queue is full", rejected.Error);
        Assert.Equal(2, await _context.QueueItems.AsNoTracking().CountAsync());
        Assert.False(BlobStore.Exists(replacementId));

        await RemoveQueuedItemAsync(secondId);
        var accepted = await CreateController().AddFileAsync(CreateRequest("same.nzb", "tv"));
        Assert.True(accepted.Status);
        Assert.Equal(2, await _context.QueueItems.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task AddFileAsync_TrueReplacement_DoesNotReserveTwice()
    {
        ConfigureAdmission(maxItems: 2, resumeThreshold: 2);
        var replacementId = Guid.NewGuid();
        var controller = CreateController();
        controller.AfterDuplicatePreCheckHook = async () =>
        {
            await using var competingContext = new DavDatabaseContext(_options);
            var competing = await CreateController(new DavDatabaseClient(competingContext))
                .AddFileAsync(CreateRequest("same.nzb", "tv"));
            Assert.True(competing.Status);
        };

        var response = await controller.AddFileAsync(CreateRequest(
            "same.nzb", "tv", nzoId: replacementId));

        Assert.True(response.Status);
        Assert.Equal(replacementId.ToString(), Assert.Single(response.NzoIds));
        Assert.Equal(replacementId,
            (await _context.QueueItems.AsNoTracking().SingleAsync()).Id);
    }

    [Fact]
    public async Task AddFileAsync_NoReplace_StaleConflictStillRequiresAdmission()
    {
        ConfigureAdmission(maxItems: 1, resumeThreshold: 1);
        var originalId = Guid.NewGuid();
        var rejectedId = Guid.NewGuid();
        Guid? otherId = null;
        await SeedQueueItemAsync(originalId, "same.nzb", "tv");
        var controller = CreateController();
        controller.AfterDuplicatePreCheckHook = async () =>
        {
            await RemoveQueuedItemAsync(originalId);
            var other = await CreateController().AddFileAsync(CreateRequest("other.nzb", "tv"));
            otherId = Guid.Parse(Assert.Single(other.NzoIds));
        };

        var response = await controller.AddFileAsync(CreateRequest(
            "same.nzb", "tv", nzoId: rejectedId, replaceExisting: false));

        Assert.False(response.Status);
        Assert.Contains("Queue is full", response.Error);
        Assert.Equal(otherId,
            (await _context.QueueItems.AsNoTracking().SingleAsync()).Id);
        Assert.False(BlobStore.Exists(rejectedId));
        Assert.False(await _context.NzbNames.AsNoTracking().AnyAsync(name => name.Id == rejectedId));
    }

    [Fact]
    public async Task AddFileAsync_StaleReplacement_UnlimitedQueueRemainsAllowed()
    {
        ConfigureAdmission(maxItems: 0, resumeThreshold: 0);
        var originalId = Guid.NewGuid();
        await SeedQueueItemAsync(originalId, "same.nzb", "tv");
        var controller = CreateController();
        controller.AfterDuplicatePreCheckHook = async () =>
        {
            await RemoveQueuedItemAsync(originalId);
            var other = await CreateController().AddFileAsync(CreateRequest("other.nzb", "tv"));
            Assert.True(other.Status);
        };

        var response = await controller.AddFileAsync(CreateRequest("same.nzb", "tv"));

        Assert.True(response.Status);
        Assert.Equal(2, await _context.QueueItems.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task CommitSubmissionAsync_LegacyOverloadRejectsFullQueue()
    {
        ConfigureAdmission(maxItems: 1, resumeThreshold: 1);
        await SeedQueueItem("other.nzb", "tv");
        var newId = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<BadHttpRequestException>(() =>
            _queueManager.CommitSubmissionAsync(
                CreateQueueItem(newId, "same.nzb", "tv"),
                new NzbName { Id = newId, FileName = "same.nzb" },
                replaceExisting: true,
                _dbClient,
                CancellationToken.None));

        Assert.Contains("Queue is full", error.Message);
        Assert.Equal("other.nzb",
            (await _context.QueueItems.AsNoTracking().SingleAsync()).FileName);
        Assert.False(await _context.NzbNames.AsNoTracking().AnyAsync(name => name.Id == newId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddFileAsync_StaleReplacement_ReleasesFallbackAfterFailure(bool cancel)
    {
        ConfigureAdmission(maxItems: 1, resumeThreshold: 1);
        var backupRoot = Path.Join(_configRoot, "submission-backups");
        _configManager.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.ApiNzbBackupEnabled,
                ConfigValue = "true",
            },
            new ConfigItem
            {
                ConfigName = ConfigKeys.ApiNzbBackupLocation,
                ConfigValue = backupRoot,
            },
        ]);
        var originalId = Guid.NewGuid();
        var replacementId = Guid.NewGuid();
        await SeedQueueItemAsync(originalId, "same.nzb", "tv");
        using var cancellationSource = new CancellationTokenSource();
        var interceptor = new SubmissionSaveInterceptor(cancellationToken =>
        {
            using var competing = _queueManager.TryReserveQueueSlot(
                persistedCount: 0, maxItems: 1, resumeThreshold: 1);
            Assert.Null(competing);
            if (cancel)
            {
                cancellationSource.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            throw new DbUpdateException("Simulated submission save failure.");
        });
        var faultOptions = new DbContextOptionsBuilder<DavDatabaseContext>(_options)
            .AddInterceptors(interceptor)
            .Options;

        await using (var faultContext = new DavDatabaseContext(faultOptions))
        {
            var controller = CreateController(new DavDatabaseClient(faultContext));
            controller.AfterDuplicatePreCheckHook = () => RemoveQueuedItemAsync(originalId);
            var request = CreateRequest(
                "same.nzb", "tv", nzoId: replacementId,
                cancellationToken: cancellationSource.Token);

            if (cancel)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.AddFileAsync(request));
            else
                await Assert.ThrowsAsync<DbUpdateException>(() => controller.AddFileAsync(request));
        }

        Assert.Empty(await _context.QueueItems.AsNoTracking().ToListAsync());
        Assert.False(await _context.NzbNames.AsNoTracking().AnyAsync(name => name.Id == replacementId));
        Assert.False(BlobStore.Exists(replacementId));
        Assert.False(File.Exists(Path.Join(backupRoot, "tv", "same.nzb")));

        var afterFailure = await CreateController().AddFileAsync(CreateRequest("after-failure.nzb", "tv"));
        Assert.True(afterFailure.Status);
        Assert.Single(await _context.QueueItems.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task QueueAdmissionAsync_CountAndReserveRemainSerializedWithCommit()
    {
        ConfigureAdmission(maxItems: 1, resumeThreshold: 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var countRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCount = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new QueueCountPauseInterceptor(countRead, releaseCount);
        var pausedOptions = new DbContextOptionsBuilder<DavDatabaseContext>(_options)
            .AddInterceptors(interceptor)
            .Options;
        await using var pausedContext = new DavDatabaseContext(pausedOptions);
        await using var commitContext = new DavDatabaseContext(_options);
        Task<(IDisposable? Reservation, int CurrentCount)>? admissionTask = null;
        Task<QueueManager.QueueSubmissionCommitResult>? commitTask = null;

        try
        {
            admissionTask = _queueManager.TryReserveQueueSlotAsync(
                new DavDatabaseClient(pausedContext), 1, 1, timeout.Token);
            await countRead.Task.WaitAsync(timeout.Token);

            var id = Guid.NewGuid();
            var commitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            commitTask = CommitAfterSignalAsync();
            await commitStarted.Task.WaitAsync(timeout.Token);
            Assert.False(commitTask.IsCompleted);

            releaseCount.TrySetResult();
            var admission = await admissionTask.WaitAsync(timeout.Token);
            Assert.Equal(0, admission.CurrentCount);
            using (admission.Reservation)
            {
                Assert.NotNull(admission.Reservation);
                var result = await commitTask.WaitAsync(timeout.Token);
                Assert.NotNull(result.Rejection);
                Assert.False(result.Rejection.Status);
                Assert.Empty(result.RemovedIds);
                Assert.Empty(await commitContext.QueueItems.AsNoTracking().ToListAsync());
            }

            var fresh = await CreateController().AddFileAsync(CreateRequest("fresh.nzb", "tv"));
            Assert.True(fresh.Status);
            Assert.Single(await _context.QueueItems.AsNoTracking().ToListAsync());

            async Task<QueueManager.QueueSubmissionCommitResult> CommitAfterSignalAsync()
            {
                commitStarted.TrySetResult();
                var competingId = Guid.NewGuid();
                return await _queueManager.CommitSubmissionAsync(
                    CreateQueueItem(competingId, "competing.nzb", "tv"),
                    new NzbName { Id = competingId, FileName = "competing.nzb" },
                    replaceExisting: true,
                    hasAdmissionReservation: false,
                    new DavDatabaseClient(commitContext),
                    timeout.Token);
            }
        }
        finally
        {
            releaseCount.TrySetResult();
            await DrainAsync(admissionTask, commitTask);
        }
    }

    [Fact]
    public void QueueAdmission_AccountsForConcurrentPendingReservations()
    {
        using var first = _queueManager.TryReserveQueueSlot(
            persistedCount: 0, maxItems: 1, resumeThreshold: 1);

        var second = _queueManager.TryReserveQueueSlot(
            persistedCount: 0, maxItems: 1, resumeThreshold: 1);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void IsCategoryFileNameUniqueViolation_DetectsSqliteConstraintMessage()
    {
        var sqlite = new Microsoft.Data.Sqlite.SqliteException(
            "SQLite Error 19: 'UNIQUE constraint failed: QueueItems.Category, QueueItems.FileName'.",
            19);
        var update = new DbUpdateException("save failed", sqlite);
        Assert.True(AddFileController.IsCategoryFileNameUniqueViolation(update));
    }

    private AddFileController CreateController(DavDatabaseClient? dbClient = null)
    {
        var controller = new AddFileController(
            new DefaultHttpContext(),
            dbClient ?? _dbClient,
            _queueManager,
            _configManager,
            _websocketManager)
        {
        };
        return controller;
    }

    private void ConfigureAdmission(int maxItems, int resumeThreshold)
    {
        _configManager.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.QueueMaxItems,
                ConfigValue = maxItems.ToString(),
            },
            new ConfigItem
            {
                ConfigName = ConfigKeys.QueueResumeThreshold,
                ConfigValue = resumeThreshold.ToString(),
            },
        ]);
    }

    private async Task SeedQueueItemAsync(Guid id, string fileName, string category)
    {
        _context.QueueItems.Add(CreateQueueItem(id, fileName, category));
        _context.NzbNames.Add(new NzbName { Id = id, FileName = fileName });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task SeedQueueItem(string fileName, string category)
    {
        await SeedQueueItemAsync(Guid.NewGuid(), fileName, category);
    }

    private static QueueItem CreateQueueItem(Guid id, string fileName, string category) =>
        new()
        {
            Id = id,
            CreatedAt = DateTime.UtcNow,
            FileName = fileName,
            JobName = Path.GetFileNameWithoutExtension(fileName),
            NzbFileSize = 10,
            TotalSegmentBytes = 10,
            Category = category,
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None,
        };

    private static AddFileRequest CreateRequest(
        string fileName,
        string category,
        Guid? nzoId = null,
        bool replaceExisting = true,
        NzbSubmissionOrigin origin = NzbSubmissionOrigin.Internal,
        byte[]? contentBytes = null,
        CancellationToken cancellationToken = default)
    {
        var nzb = contentBytes ?? Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="utf-8"?>
            <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
              <file subject="test">
                <groups><group>alt.binaries.test</group></groups>
                <segments>
                  <segment bytes="100" number="1">seg@example.com</segment>
                </segments>
              </file>
            </nzb>
            """);
        return new AddFileRequest
        {
            NzoId = nzoId,
            ReplaceExistingQueueItem = replaceExisting,
            FileName = fileName,
            ContentType = "application/x-nzb",
            NzbFileStream = new MemoryStream(nzb),
            Category = category,
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None,
            CancellationToken = cancellationToken,
            Origin = origin,
        };
    }

    private async Task RemoveQueuedItemAsync(Guid id)
    {
        await using var context = new DavDatabaseContext(_options);
        var removal = await _queueManager.RemoveQueueItemsDetailedAsync(
            [id], new DavDatabaseClient(context));
        Assert.Equal(new[] { id }, removal.RemovedIds);
        Assert.Empty(removal.StillRunningIds);
    }

    private static async Task DrainAsync(params Task?[] tasks)
    {
        foreach (var task in tasks)
        {
            if (task is null) continue;
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
            }
        }
    }

    private sealed class SubmissionSaveInterceptor(Action<CancellationToken> onSave)
        : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<QueueItem>()
                .Any(entry => entry.State == EntityState.Added))
            {
                onSave(cancellationToken);
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class QueueCountPauseInterceptor(
        TaskCompletionSource countRead,
        TaskCompletionSource releaseCount) : DbCommandInterceptor
    {
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            await PauseCountAsync(command, cancellationToken);
            return result;
        }

        public override async ValueTask<object?> ScalarExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            object? result,
            CancellationToken cancellationToken = default)
        {
            await PauseCountAsync(command, cancellationToken);
            return result;
        }

        private async Task PauseCountAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (command.CommandText.Contains("COUNT(*)", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("QueueItems", StringComparison.Ordinal))
            {
                countRead.TrySetResult();
                await releaseCount.Task.WaitAsync(cancellationToken);
            }
        }
    }
}
