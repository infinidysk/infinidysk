using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Config;
using NzbWebDAV.Config.Scheduling;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Tests.TestUtils;
using NzbWebDAV.Websocket;
using Xunit.Sdk;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(ConfigPathCollection))]
public sealed class HealthCheckCoordinatorTests
{
    [Fact]
    public async Task DefaultWorkerCount_StartsOnlyOneFile()
    {
        using var harness = new Harness(workers: null, fullySplit: false);
        var ids = new Queue<Guid>([Guid.NewGuid(), Guid.NewGuid()]);
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Service.SelectCandidateOverride = (_, _, _) =>
            Task.FromResult<Guid?>(ids.Count > 0 ? ids.Dequeue() : null);
        harness.Service.ProcessCandidateOverride = (_, ct) => blocker.Task.WaitAsync(ct);

        await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);

        Assert.Single(harness.Service.InProgressHealthCheckIds);
        Assert.Single(ids);
        blocker.TrySetResult();
        await ReapUntilAsync(
            harness.Service,
            () => harness.Service.InProgressHealthCheckIds.Count == 0);
    }

    [Fact]
    public async Task CompletedWorker_IsReplacedWithoutWaitingForFixedBatch()
    {
        using var harness = new Harness(workers: 2, fullySplit: false);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var ids = new Queue<Guid>([first, second, third]);
        var blockers = new ConcurrentDictionary<Guid, TaskCompletionSource>();
        harness.Service.SelectCandidateOverride = (_, _, _) =>
            Task.FromResult<Guid?>(ids.Count > 0 ? ids.Dequeue() : null);
        harness.Service.ProcessCandidateOverride = (id, ct) => blockers
            .GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .Task.WaitAsync(ct);

        await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);
        Assert.Equal(2, harness.Service.InProgressHealthCheckIds.Count);

        blockers[first].TrySetResult();
        await ReapUntilAsync(
            harness.Service,
            () => !harness.Service.InProgressHealthCheckIds.Contains(first));
        await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);

        Assert.Contains(second, harness.Service.InProgressHealthCheckIds);
        Assert.Contains(third, harness.Service.InProgressHealthCheckIds);
        blockers[second].TrySetResult();
        blockers[third].TrySetResult();
        await ReapUntilAsync(
            harness.Service,
            () => harness.Service.InProgressHealthCheckIds.Count == 0);
    }

    [Fact]
    public async Task FailedWorker_DoesNotStopOtherWorkers()
    {
        using var harness = new Harness(workers: 2, fullySplit: false);
        var failed = Guid.NewGuid();
        var running = Guid.NewGuid();
        var ids = new Queue<Guid>([failed, running]);
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Service.SelectCandidateOverride = (_, _, _) =>
            Task.FromResult<Guid?>(ids.Count > 0 ? ids.Dequeue() : null);
        harness.Service.ProcessCandidateOverride = (id, ct) => id == failed
            ? Task.FromException(new InvalidOperationException("worker failure"))
            : blocker.Task.WaitAsync(ct);

        await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);
        await ReapUntilAsync(
            harness.Service,
            () => !harness.Service.InProgressHealthCheckIds.Contains(failed));

        Assert.Contains(running, harness.Service.InProgressHealthCheckIds);
        blocker.TrySetResult();
        await ReapUntilAsync(
            harness.Service,
            () => harness.Service.InProgressHealthCheckIds.Count == 0);
    }

    [Fact]
    public async Task Cancellation_DrainsAllActiveWorkers()
    {
        using var harness = new Harness(workers: 2, fullySplit: false);
        var ids = new Queue<Guid>([Guid.NewGuid(), Guid.NewGuid()]);
        using var cancellation = new CancellationTokenSource();
        harness.Service.SelectCandidateOverride = (_, _, _) =>
            Task.FromResult<Guid?>(ids.Count > 0 ? ids.Dequeue() : null);
        harness.Service.ProcessCandidateOverride = (_, ct) =>
            Task.Delay(Timeout.InfiniteTimeSpan, ct);

        await harness.Service.RefillWorkerSlotsAsync(cancellation.Token);
        Assert.Equal(2, harness.Service.InProgressHealthCheckIds.Count);

        await cancellation.CancelAsync();
        await ReapUntilAsync(
            harness.Service,
            () => harness.Service.InProgressHealthCheckIds.Count == 0);
    }

    [Fact]
    public async Task DuplicateReservation_IsRejectedByInMemoryGuard()
    {
        using var harness = new Harness(workers: 2, fullySplit: false);
        var id = Guid.NewGuid();
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Service.SelectCandidateOverride = (_, _, _) => Task.FromResult<Guid?>(id);
        harness.Service.ProcessCandidateOverride = (_, ct) => blocker.Task.WaitAsync(ct);

        await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);

        Assert.Equal(id, Assert.Single(harness.Service.InProgressHealthCheckIds));
        blocker.TrySetResult();
        await ReapUntilAsync(
            harness.Service,
            () => harness.Service.InProgressHealthCheckIds.Count == 0);
    }

    [Fact]
    public async Task RaisingWorkerCount_FillsAdditionalSlots()
    {
        using var harness = new Harness(workers: 1, fullySplit: false);
        var ids = new Queue<Guid>([Guid.NewGuid(), Guid.NewGuid()]);
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Service.SelectCandidateOverride = (_, _, _) =>
            Task.FromResult<Guid?>(ids.Count > 0 ? ids.Dequeue() : null);
        harness.Service.ProcessCandidateOverride = (_, ct) => blocker.Task.WaitAsync(ct);

        await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);
        Assert.Single(harness.Service.InProgressHealthCheckIds);

        harness.SetWorkers(2);
        await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);

        Assert.Equal(2, harness.Service.InProgressHealthCheckIds.Count);
        blocker.TrySetResult();
        await ReapUntilAsync(
            harness.Service,
            () => harness.Service.InProgressHealthCheckIds.Count == 0);
    }

    [Fact]
    public async Task LoweringWorkerCount_DoesNotCancelRunningWorkersOrStartReplacement()
    {
        using var harness = new Harness(workers: 2, fullySplit: false);
        var ids = new Queue<Guid>([Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()]);
        var blockers = new ConcurrentDictionary<Guid, TaskCompletionSource>();
        harness.Service.SelectCandidateOverride = (_, _, _) =>
            Task.FromResult<Guid?>(ids.Count > 0 ? ids.Dequeue() : null);
        harness.Service.ProcessCandidateOverride = (id, ct) => blockers
            .GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .Task.WaitAsync(ct);

        await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);
        var running = harness.Service.InProgressHealthCheckIds.ToArray();
        harness.SetWorkers(1);
        blockers[running[0]].TrySetResult();
        await ReapUntilAsync(
            harness.Service,
            () => harness.Service.InProgressHealthCheckIds.Count == 1);

        await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);

        Assert.Single(harness.Service.InProgressHealthCheckIds);
        Assert.Single(ids);
        blockers[running[1]].TrySetResult();
        await ReapUntilAsync(
            harness.Service,
            () => harness.Service.InProgressHealthCheckIds.Count == 0);
    }

    [Fact]
    public async Task ActiveQueue_DefersNewWorkersWithSplitProviderBudgets()
    {
        using var harness = new Harness(workers: 2, fullySplit: true);
        var selectorCalled = false;
        harness.Service.HasActiveQueueItemsOverride = () => true;
        harness.Service.SelectCandidateOverride = (_, _, _) =>
        {
            selectorCalled = true;
            return Task.FromResult<Guid?>(Guid.NewGuid());
        };

        var outcome = await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);

        Assert.Equal(HealthCheckRefillOutcome.Blocked, outcome);
        Assert.Equal(
            harness.Service.CoordinatorIdleInterval,
            harness.Service.GetCoordinatorWaitInterval(outcome));
        Assert.False(selectorCalled);
        Assert.Empty(harness.Service.InProgressHealthCheckIds);
    }

    [Fact]
    public async Task BenchmarkPause_UsesIdleCoordinatorIntervalWhenNoWorkerIsActive()
    {
        using var harness = new Harness(workers: 2, fullySplit: false);
        using var pause = harness.BenchmarkGate.Enter();

        var outcome = await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);

        Assert.Equal(HealthCheckRefillOutcome.Blocked, outcome);
        Assert.Equal(
            harness.Service.CoordinatorIdleInterval,
            harness.Service.GetCoordinatorWaitInterval(outcome));
    }

    [Fact]
    public async Task DisabledRepairJob_UsesIdleCoordinatorInterval()
    {
        using var harness = new Harness(workers: 2, fullySplit: false);
        harness.SetRepairEnabled(false);

        var outcome = await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);

        Assert.Equal(HealthCheckRefillOutcome.Blocked, outcome);
        Assert.Equal(
            harness.Service.CoordinatorIdleInterval,
            harness.Service.GetCoordinatorWaitInterval(outcome));
    }

    [Fact]
    public async Task ClosedSchedules_BlockNewWorkers()
    {
        using var harness = new Harness(workers: 2, fullySplit: false);
        harness.CloseHealthSchedules();
        var selectorCalled = false;
        harness.Service.SelectCandidateOverride = (_, _, _) =>
        {
            selectorCalled = true;
            return Task.FromResult<Guid?>(Guid.NewGuid());
        };

        var outcome = await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);

        Assert.Equal(HealthCheckRefillOutcome.Blocked, outcome);
        Assert.False(selectorCalled);
        Assert.Empty(harness.Service.InProgressHealthCheckIds);
    }

    [Fact]
    public async Task ManualRun_OpensChecksOnlyUntilDueWorkDrains()
    {
        using var harness = new Harness(workers: 1, fullySplit: false);
        harness.CloseHealthSchedules();
        var id = Guid.NewGuid();
        var candidates = new Queue<Guid>([id]);
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        HealthWorkAdmission? observedAdmission = null;
        harness.Service.SelectCandidateOverride = (_, admission, _) =>
        {
            observedAdmission = admission;
            return Task.FromResult<Guid?>(
                candidates.Count > 0 ? candidates.Dequeue() : null);
        };
        harness.Service.ProcessCandidateOverride = (_, ct) => blocker.Task.WaitAsync(ct);

        Assert.False(harness.HealthSchedule.BeginManualRun());
        await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);

        Assert.True(observedAdmission?.ChecksOpen);
        Assert.False(observedAdmission?.RepairsOpen);
        Assert.True(harness.HealthSchedule.IsManualRunActive);
        blocker.TrySetResult();
        await ReapUntilAsync(
            harness.Service,
            () => harness.Service.InProgressHealthCheckIds.Count == 0);

        var outcome = await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);

        Assert.Equal(HealthCheckRefillOutcome.NoCandidate, outcome);
        Assert.False(harness.HealthSchedule.IsManualRunActive);
    }

    [Fact]
    public async Task BlockedRefill_UsesShortIntervalWhileAWorkerIsStillActive()
    {
        using var harness = new Harness(workers: 2, fullySplit: false);
        var id = Guid.NewGuid();
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Service.SelectCandidateOverride = (_, _, _) => Task.FromResult<Guid?>(id);
        harness.Service.ProcessCandidateOverride = (_, ct) => blocker.Task.WaitAsync(ct);
        await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);
        harness.Service.HasActiveQueueItemsOverride = () => true;

        var outcome = await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);

        Assert.Equal(HealthCheckRefillOutcome.Blocked, outcome);
        Assert.Equal(
            harness.Service.CoordinatorPollInterval,
            harness.Service.GetCoordinatorWaitInterval(outcome));
        blocker.TrySetResult();
        await ReapUntilAsync(
            harness.Service,
            () => harness.Service.InProgressHealthCheckIds.Count == 0);
    }

    [Fact]
    public async Task WorkerOutOfMemoryBeforeFirstSuspension_FaultsCoordinator()
    {
        using var harness = new Harness(workers: 1, fullySplit: false);
        await using var database = await harness.ConfigureEmptyDatabaseAsync();
        var id = Guid.NewGuid();
        harness.Service.SelectCandidateOverride = (_, _, _) => Task.FromResult<Guid?>(id);
        harness.Service.ProcessCandidateOverride = (_, _) =>
            throw new OutOfMemoryException("synthetic pre-suspension OOM");
        var previousGrace = HealthCheckService.StartupGracePeriod;
        HealthCheckService.StartupGracePeriod = TimeSpan.Zero;

        try
        {
            await Assert.ThrowsAsync<OutOfMemoryException>(
                () => harness.Service.ExecuteHostedServiceForTests(CancellationToken.None));
        }
        finally
        {
            HealthCheckService.StartupGracePeriod = previousGrace;
        }

        Assert.Empty(harness.Service.InProgressHealthCheckIds);
    }

    [Fact]
    public async Task WorkerOutOfMemoryAfterProgress_ClearsProgressAndFaultsCoordinator()
    {
        using var harness = new Harness(workers: 1, fullySplit: false);
        await using var database = await harness.ConfigureEmptyDatabaseAsync();
        var id = Guid.NewGuid();
        harness.Service.SelectCandidateOverride = (_, _, _) => Task.FromResult<Guid?>(id);
        harness.Service.ProcessCandidateOverride = (candidateId, _) =>
        {
            Assert.True(harness.Service.MarkHealthProgressStarted(candidateId));
            return Task.FromException(
                new OutOfMemoryException("synthetic post-progress OOM"));
        };
        var previousGrace = HealthCheckService.StartupGracePeriod;
        HealthCheckService.StartupGracePeriod = TimeSpan.Zero;

        try
        {
            await Assert.ThrowsAsync<OutOfMemoryException>(
                () => harness.Service.ExecuteHostedServiceForTests(CancellationToken.None));
        }
        finally
        {
            HealthCheckService.StartupGracePeriod = previousGrace;
        }

        Assert.Equal(
            $"{id}|done",
            harness.WebsocketManager.PeekLastMessage(WebsocketTopic.HealthItemProgress));
        Assert.Empty(harness.Service.InProgressHealthCheckIds);
    }

    [Fact]
    public async Task MultipleOutOfMemoryWorkers_AreObservedAndRemovedBeforePropagation()
    {
        using var harness = new Harness(workers: 2, fullySplit: false);
        var ids = new Queue<Guid>([Guid.NewGuid(), Guid.NewGuid()]);
        harness.Service.SelectCandidateOverride = (_, _, _) =>
            Task.FromResult<Guid?>(ids.Count > 0 ? ids.Dequeue() : null);
        harness.Service.ProcessCandidateOverride = (id, _) =>
            Task.FromException(new OutOfMemoryException($"synthetic OOM for {id}"));
        await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);

        await Assert.ThrowsAsync<OutOfMemoryException>(
            () => harness.Service.ReapCompletedWorkersAsync());

        Assert.Empty(harness.Service.InProgressHealthCheckIds);
    }

    [Fact]
    public async Task FatalWorkerCancellation_TerminalizesSiblingProgress()
    {
        using var harness = new Harness(workers: 2, fullySplit: false);
        var fatal = Guid.NewGuid();
        var sibling = Guid.NewGuid();
        var ids = new Queue<Guid>([fatal, sibling]);
        var siblingStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Service.SelectCandidateOverride = (_, _, _) =>
            Task.FromResult<Guid?>(ids.Count > 0 ? ids.Dequeue() : null);
        harness.Service.ProcessCandidateOverride = async (id, ct) =>
        {
            if (id == fatal)
            {
                await siblingStarted.Task.WaitAsync(ct);
                throw new OutOfMemoryException("synthetic fatal worker");
            }

            Assert.True(harness.Service.MarkHealthProgressStarted(id));
            siblingStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        };

        await Assert.ThrowsAsync<OutOfMemoryException>(
            () => harness.Service.RunCoordinatorIterationAsync(CancellationToken.None));
        await ReapUntilAsync(
            harness.Service,
            () => harness.Service.InProgressHealthCheckIds.Count == 0);

        Assert.Equal(
            $"{sibling}|done",
            harness.WebsocketManager.PeekLastMessage(WebsocketTopic.HealthItemProgress));
    }

    [Fact]
    public async Task CandidateQueryFailure_IsAttemptedAtMostOncePerCooldownWindow()
    {
        using var harness = new Harness(workers: 1, fullySplit: false);
        var attempts = 0;
        harness.Service.SelectCandidateOverride = (_, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("synthetic candidate query failure");
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.RefillWorkerSlotsAsync(CancellationToken.None));
        var blocked = await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);

        Assert.Equal(1, Volatile.Read(ref attempts));
        Assert.Equal(HealthCheckRefillOutcome.Blocked, blocked);

        harness.TimeProvider.Advance(HealthCheckService.InfrastructureFailureCooldown);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.RefillWorkerSlotsAsync(CancellationToken.None));
        Assert.Equal(2, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task WorkerSetupFailure_CoolsDownTheItemAndCoordinator()
    {
        using var harness = new Harness(workers: 1, fullySplit: false);
        var id = Guid.NewGuid();
        var contextAttempts = 0;
        harness.Service.SelectCandidateOverride = (_, _, _) => Task.FromResult<Guid?>(id);
        harness.Service.CreateDbContextOverride = () =>
        {
            Interlocked.Increment(ref contextAttempts);
            throw new InvalidOperationException("synthetic context creation failure");
        };

        await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);
        await ReapUntilAsync(
            harness.Service,
            () => harness.Service.InProgressHealthCheckIds.Count == 0);
        var blocked = await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);

        Assert.True(harness.Service.IsWorkerFailureCooldownActive(id));
        Assert.Equal(1, Volatile.Read(ref contextAttempts));
        Assert.Equal(HealthCheckRefillOutcome.Blocked, blocked);

        harness.TimeProvider.Advance(HealthCheckService.InfrastructureFailureCooldown);
        await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);
        await ReapUntilAsync(
            harness.Service,
            () => harness.Service.InProgressHealthCheckIds.Count == 0);
        Assert.Equal(2, Volatile.Read(ref contextAttempts));
    }

    [Fact]
    public async Task DeferredScheduleDoubleFailure_CoolsDownTheItem()
    {
        using var harness = new Harness(workers: 1, fullySplit: false);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        var candidate = NewCandidate("schedule-failure.mkv", null);
        await using (var setup = new DavDatabaseContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Items.Add(candidate);
            await setup.SaveChangesAsync();
        }

        await using var failingContext = new FailingSaveDavDatabaseContext(options, failureCount: 2);
        var trackedCandidate = await failingContext.Items.SingleAsync(item => item.Id == candidate.Id);
        await harness.Service.DeferHealthCheck(
            trackedCandidate,
            new DavDatabaseClient(failingContext),
            new InvalidOperationException("synthetic health failure"),
            CancellationToken.None);

        Assert.Equal(2, failingContext.SaveAttempts);
        Assert.True(harness.Service.IsWorkerFailureCooldownActive(candidate.Id));
    }

    [Fact]
    public async Task BenchmarkDrain_WaitsForActiveHealthAdmissionToRelease()
    {
        using var harness = new Harness(workers: 1, fullySplit: true);
        var id = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Service.SelectCandidateOverride = (_, _, _) => Task.FromResult<Guid?>(id);
        harness.Service.ProcessCandidateOverride = async (_, ct) =>
        {
            using var lease = await harness.ConnectionGate.AcquireAsync(
                HealthCheckAdmissionPriority.Background,
                ct);
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        using var pause = harness.BenchmarkGate.Enter();

        var drain = harness.Service.WaitForQuiescenceAsync(CancellationToken.None);

        Assert.False(drain.IsCompleted);
        Assert.Equal(1, harness.ConnectionGate.GetSnapshot().Active);
        release.TrySetResult();
        await ReapUntilAsync(
            harness.Service,
            () => harness.Service.InProgressHealthCheckIds.Count == 0);
        await drain.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0, harness.ConnectionGate.GetSnapshot().Active);
    }

    [Fact]
    public async Task ProductionSelection_PreservesOrderingAndScheduleAdmission()
    {
        using var harness = new Harness(workers: 5, fullySplit: true);
        var databasePath = Path.Join(
            Path.GetTempPath(),
            $"infinidysk-health-selection-{Guid.NewGuid():N}.sqlite");
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        var urgent = NewCandidate("urgent.mkv", DateTimeOffset.UnixEpoch);
        var pendingRepair = NewCandidate(
            "pending-repair.mkv",
            DateTimeOffset.UtcNow + TimeSpan.FromDays(1));
        pendingRepair.HealthRepairPending = true;
        var neverChecked = NewCandidate("never-checked.mkv", null);
        var forced = NewCandidate("forced.mkv", HealthCheckService.ForcedRecheckSentinel);
        var scheduled = NewCandidate("scheduled.mkv", DateTimeOffset.UtcNow - TimeSpan.FromHours(1));
        var nonMedia = NewCandidate("notes.nfo", null);
        try
        {
            await using (var db = new DavDatabaseContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.Items.AddRange(
                    urgent,
                    pendingRepair,
                    neverChecked,
                    forced,
                    scheduled,
                    nonMedia);
                await db.SaveChangesAsync();
            }
            harness.Service.CreateDbContextOverride = () => new DavDatabaseContext(options);

            var unrestricted = await harness.Service.SelectNextHealthCheckIdsAsync(
                [],
                allowChecks: true,
                allowRepairs: true,
                maximumCount: 5,
                CancellationToken.None);
            var checksOnly = await harness.Service.SelectNextHealthCheckIdsAsync(
                [],
                allowChecks: true,
                allowRepairs: false,
                maximumCount: 5,
                CancellationToken.None);
            var repairsOnly = await harness.Service.SelectNextHealthCheckIdsAsync(
                [],
                allowChecks: false,
                allowRepairs: true,
                maximumCount: 5,
                CancellationToken.None);

            Assert.Equal(
                [urgent.Id, pendingRepair.Id, neverChecked.Id, forced.Id, scheduled.Id],
                unrestricted);
            Assert.Equal([neverChecked.Id, forced.Id, scheduled.Id], checksOnly);
            Assert.Equal([urgent.Id, pendingRepair.Id], repairsOnly);
        }
        finally
        {
            try { File.Delete(databasePath); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ProductionWorker_UsesItsOwnContextAndRecordsMissingPayload()
    {
        using var harness = new Harness(workers: 1, fullySplit: false);
        var databasePath = Path.Join(
            Path.GetTempPath(),
            $"infinidysk-health-worker-{Guid.NewGuid():N}.sqlite");
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        var candidate = NewCandidate("missing-payload.mkv", null);
        try
        {
            await using (var db = new DavDatabaseContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.Items.Add(candidate);
                await db.SaveChangesAsync();
            }
            harness.Service.CreateDbContextOverride = () => new DavDatabaseContext(options);

            await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);
            await ReapUntilAsync(
                harness.Service,
                () => harness.Service.InProgressHealthCheckIds.Count == 0);

            await using var verificationDb = new DavDatabaseContext(options);
            var result = Assert.Single(await verificationDb.HealthCheckResults.ToListAsync());
            Assert.Equal(candidate.Id, result.DavItemId);
            Assert.Contains("streaming data is missing", result.Message);
        }
        finally
        {
            try { File.Delete(databasePath); } catch (IOException) { }
        }
    }

    private static async Task ReapUntilAsync(
        HealthCheckService service,
        Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            while (!condition())
            {
                await service.ReapCompletedWorkersAsync();
                await Task.Delay(10, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new XunitException("Timed out waiting for the health coordinator condition.");
        }
    }

    private static DavItem NewCandidate(string name, DateTimeOffset? nextHealthCheck)
    {
        var id = Guid.NewGuid();
        return new DavItem
        {
            Id = id,
            IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
            CreatedAt = DateTime.UtcNow,
            Name = name,
            Type = DavItem.ItemType.UsenetFile,
            SubType = DavItem.ItemSubType.NzbFile,
            Path = $"/library/{name}",
            NextHealthCheck = nextHealthCheck,
        };
    }

    private sealed class FailingSaveDavDatabaseContext(
        DbContextOptions<DavDatabaseContext> options,
        int failureCount) : DavDatabaseContext(options)
    {
        private int _remainingFailures = failureCount;

        public int SaveAttempts { get; private set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            if (Interlocked.Decrement(ref _remainingFailures) >= 0)
            {
                return Task.FromException<int>(
                    new InvalidOperationException("synthetic schedule persistence failure"));
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _root = Path.Join(
            Path.GetTempPath(),
            $"infinidysk-health-coordinator-{Guid.NewGuid():N}");
        private readonly UsenetStreamingClient _usenet;
        private readonly QueueManager _queueManager;
        private readonly HealthCheckConnectionGate _gate;
        private readonly RepairPatchStore _patchStore;

        public ConfigManager Config { get; }
        public HealthCheckService Service { get; }
        public BenchmarkGate BenchmarkGate { get; }
        public HealthWorkSchedulePolicy HealthSchedule { get; }
        public HealthCheckConnectionGate ConnectionGate => _gate;
        public ControllableTimeProvider TimeProvider { get; } =
            new(DateTimeOffset.UtcNow);
        public WebsocketManager WebsocketManager { get; }

        public Harness(int? workers, bool fullySplit)
        {
            Directory.CreateDirectory(_root);
            Config = new ConfigManager();
            var providerConfig = new UsenetProviderConfig();
            if (fullySplit)
            {
                providerConfig.Providers.Add(new UsenetProviderConfig.ConnectionDetails
                {
                    ProviderId = Guid.NewGuid(),
                    Type = ProviderType.Pooled,
                    Host = "split.example",
                    Port = 563,
                    UseSsl = true,
                    User = "user",
                    Pass = "pass",
                    MaxConnections = 10,
                });
            }

            var values = new List<ConfigItem>
            {
                new() { ConfigName = ConfigKeys.RepairEnable, ConfigValue = "true" },
                new() { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = _root },
                new()
                {
                    ConfigName = ConfigKeys.ArrInstances,
                    ConfigValue = JsonSerializer.Serialize(new ArrConfig
                    {
                        RadarrInstances =
                        [
                            new ArrConfig.ConnectionDetails
                            {
                                Host = "http://radarr.example",
                                ApiKey = "test",
                            },
                        ],
                    }),
                },
                new()
                {
                    ConfigName = ConfigKeys.UsenetProviders,
                    ConfigValue = JsonSerializer.Serialize(providerConfig),
                },
            };
            if (workers is { } workerCount)
            {
                values.Add(new ConfigItem
                {
                    ConfigName = ConfigKeys.RepairHealthcheckWorkers,
                    ConfigValue = workerCount.ToString(),
                });
            }
            Config.UpdateValues(values);
            HealthSchedule = new HealthWorkSchedulePolicy(Config);

            WebsocketManager = new WebsocketManager();
            _patchStore = new RepairPatchStore(Path.Join(_root, "patches"), 1024 * 1024);
            _usenet = new UsenetStreamingClient(
                Config,
                WebsocketManager,
                new ProviderUsageTracker(),
                new MetricsWriter(),
                new ProviderBytesTracker(),
                new StreamTraceBuffer(10),
                new ActiveReadRegistry(),
                repairPatchStore: _patchStore);
            _gate = new HealthCheckConnectionGate(Config);
            BenchmarkGate = new BenchmarkGate();
            _queueManager = QueueManager.CreateForTests(
                _usenet,
                Config,
                WebsocketManager,
                new ProviderUsageTracker(),
                new WatchdogLog(),
                new QueueItemSourceTracker(),
                BenchmarkGate,
                startLoop: false,
                healthCheckConnectionGate: _gate);
            Service = new HealthCheckService(
                Config,
                _usenet,
                WebsocketManager,
                BenchmarkGate,
                new StreamingFailureTracker(),
                _queueManager,
                new Par2RepairService(Config, _usenet, _patchStore),
                _patchStore,
                new ArrReplacementSearchBudget(),
                _gate,
                timeProvider: TimeProvider,
                healthWorkSchedule: HealthSchedule);
        }

        public void SetWorkers(int count)
        {
            Config.UpdateValues([
                new ConfigItem
                {
                    ConfigName = ConfigKeys.RepairHealthcheckWorkers,
                    ConfigValue = count.ToString(),
                },
            ]);
        }

        public void SetRepairEnabled(bool enabled)
        {
            Config.UpdateValues([
                new ConfigItem
                {
                    ConfigName = ConfigKeys.RepairEnable,
                    ConfigValue = enabled.ToString(),
                },
            ]);
        }

        public void CloseHealthSchedules()
        {
            var localNow = TimeZoneInfo.ConvertTime(TimeProvider.GetUtcNow(), TimeZoneInfo.Local);
            var closedDay = ((int)localNow.DayOfWeek + 1) % 7;
            var closedSchedule = JsonSerializer.Serialize(new WeeklyWindowSchedule
            {
                Enabled = true,
                Windows =
                [
                    new WeeklyWindow
                    {
                        Days = [closedDay],
                        StartMinute = 0,
                        EndMinute = 1,
                    },
                ],
            });
            Config.UpdateValues([
                new ConfigItem
                {
                    ConfigName = ConfigKeys.RepairHealthcheckSchedule,
                    ConfigValue = closedSchedule,
                },
                new ConfigItem
                {
                    ConfigName = ConfigKeys.RepairActionSchedule,
                    ConfigValue = closedSchedule,
                },
            ]);
        }

        public async Task<SqliteConnection> ConfigureEmptyDatabaseAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DavDatabaseContext>()
                .UseSqlite(connection)
                .Options;
            await using (var context = new DavDatabaseContext(options))
            {
                await context.Database.EnsureCreatedAsync();
            }

            Service.CreateDbContextOverride = () => new DavDatabaseContext(options);
            return connection;
        }

        public void Dispose()
        {
            Service.Dispose();
            _queueManager.Dispose();
            _gate.Dispose();
            _usenet.Dispose();
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }
    }
}
