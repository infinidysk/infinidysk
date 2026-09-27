using NzbWebDAV.Services.Prefetch;
using Microsoft.Data.Sqlite;

namespace NzbWebDAV.Tests.Services;

public sealed class PrefetchJobStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "prefetch-jobs-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void BridgingQueuedRanges_CoalescesTransitively_WithOwnersAndPriority()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var first = jobs.Enqueue(item, "source-a", 1, 0, 10);
        jobs.Enqueue(item, "manual", 80, 20, 10);
        jobs.Enqueue(item, "source-c", 3, 40, 10);
        var merged = jobs.EnqueueWithOutcome(item, "bridge", 2, 10, 30);
        Assert.False(merged.Created);
        Assert.Equal(first.Id, merged.Job.Id);
        Assert.Equal(0, merged.Job.Start);
        Assert.Equal(50, merged.Job.Length);
        Assert.Equal(80, merged.Job.Priority);
        Assert.Single(jobs.List());
        jobs.PruneOwners(owner => owner == "manual");
        Assert.Equal(first.Id, jobs.ClaimNext()!.Id);
    }

    [Fact]
    public void ResumedLegacyRanges_AreMergedTransitivelyRegardlessOfCandidateOrder()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var paused = new List<PrefetchJob>();
        for (var start = 0; start < 30; start += 10)
        {
            var job = jobs.Enqueue(item, "owner-" + start, 0, start, 10);
            jobs.Change(job.Id, "pause");
            paused.Add(job);
        }
        foreach (var job in paused) jobs.Change(job.Id, "resume");
        var merged = jobs.Enqueue(item, "manual", 0, 30, 10);
        Assert.Equal(0, merged.Start);
        Assert.Equal(40, merged.Length);
        Assert.Single(jobs.List());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongMaximumFiniteRange_IsDistinctFromOpenEndedTail(bool toEof)
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        jobs.Enqueue(item, "manual", 0, long.MaxValue - 20, 20);
        var merged = jobs.Enqueue(item, "tail", 0, long.MaxValue - 10, toEof ? 0 : 10);
        Assert.Equal(long.MaxValue - 20, merged.Start);
        Assert.Equal(toEof ? 0 : 20, merged.Length);
        Assert.Single(jobs.List());
    }

    [Fact]
    public async Task EnqueueOutcomes_AreAtomicAcrossConcurrentRequests()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(index => Task.Run(() => jobs.EnqueueWithOutcome(item, "owner-" + index, 0))));
        Assert.Single(outcomes.Where(outcome => outcome.Created));
        Assert.Single(outcomes.Select(outcome => outcome.Job.Id).Distinct());
        Assert.Single(jobs.List());
    }

    [Fact]
    public void EofRangeAtNonzeroStart_MergesAllTouchingQueuedSuccessors()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var first = jobs.Enqueue(item, "manual", 0, 10, 5);
        jobs.Enqueue(item, "source", 0, 30, 10);
        var merged = jobs.Enqueue(item, "tail", 0, 15, 0);
        Assert.Equal(first.Id, merged.Id);
        Assert.Equal(10, merged.Start);
        Assert.Equal(0, merged.Length);
        Assert.Single(jobs.List());
    }

    [Fact]
    public void WholeFileUpgrade_MergesEveryQueuedRange()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        jobs.Enqueue(item, "source-a", 0, 10, 5);
        jobs.Enqueue(item, "source-b", 0, 30, 5);
        var whole = jobs.Enqueue(item, "manual", 50);
        Assert.Equal(0, whole.Start);
        Assert.Equal(0, whole.Length);
        Assert.Single(jobs.List());
        jobs.PruneOwners(owner => owner == "source-b");
        Assert.Equal(whole.Id, jobs.ClaimNext()!.Id);
    }

    [Fact]
    public void OverlappingSuccessor_DoesNotExtendRunningRange()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var running = jobs.Enqueue(item, "manual", 0, 0, 10);
        jobs.ClaimNext();
        jobs.Enqueue(item, "tail", 0, 20, 10);
        var successor = jobs.Enqueue(item, "bridge", 0, 10, 15);
        Assert.Equal(10, successor.Start);
        Assert.Equal(20, successor.Length);
        Assert.Equal(10, jobs.List().Single(job => job.Id == running.Id).Length);
        Assert.Null(jobs.ClaimNext());
        jobs.Finish(running.Id, true, null);
        Assert.Equal(successor.Id, jobs.ClaimNext()!.Id);
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("failed")]
    public void InactiveRanges_AreOnlyDeduplicatedExactly(string state)
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var inactive = jobs.Enqueue(item, "manual", 0, 0, 10);
        if (state == "paused") jobs.Change(inactive.Id, "pause");
        else { jobs.ClaimNext(); jobs.Finish(inactive.Id, false, "failed"); }
        var overlap = jobs.EnqueueWithOutcome(item, "source", 0, 5, 10);
        Assert.True(overlap.Created);
        Assert.NotEqual(inactive.Id, overlap.Job.Id);
        Assert.Equal(inactive.Id, jobs.Enqueue(item, "exact", 0, 0, 10).Id);
        Assert.Equal(2, jobs.List().Count);
    }

    [Fact]
    public void MergeOwnerOverflow_RejectsAtomically_AndManualDoesNotUseAutomaticSlot()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        for (var i = 0; i < 64; i++) jobs.Enqueue(item, "a-" + i, 0, 0, 10);
        for (var i = 0; i < 65; i++) jobs.Enqueue(item, "b-" + i, 0, 20, 10);
        Assert.Throws<ArgumentException>(() => jobs.Enqueue(item, "manual", 100, 10, 10));
        Assert.Equal(2, jobs.List().Count);
        Assert.All(jobs.List(), job => Assert.Equal(0, job.Priority));
        var other = Guid.NewGuid();
        jobs.Enqueue(other, "manual", 0);
        for (var i = 0; i < 128; i++) jobs.Enqueue(other, "owner-" + i, 0);
        Assert.Throws<ArgumentException>(() => jobs.Enqueue(other, "excess", 0));
        jobs.PruneOwners(owner => owner == "owner-127");
        Assert.Equal(other, jobs.ClaimNext()!.ItemId);
    }

    [Fact]
    public void MergedIntent_PreservesOldestCreation_MaximumAttempts_AndLatestBackoff()
    {
        var path = Path.Combine(_root, "jobs.db");
        using var jobs = new PrefetchJobStore(path);
        var item = Guid.NewGuid();
        var first = jobs.Enqueue(item, "manual", 0, 0, 10);
        jobs.ClaimNext();
        jobs.Defer(first.Id, "first", TimeSpan.FromHours(1));
        var second = jobs.Enqueue(item, "source", 0, 20, 10);
        jobs.ClaimNext();
        jobs.Defer(second.Id, "second", TimeSpan.FromHours(2));
        using var database = new SqliteConnection("Data Source=" + path);
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = "UPDATE Attempts SET Count=3; UPDATE Jobs SET Created=123 WHERE Start=20; SELECT MAX(Until) FROM Deferred";
        var until = (long)command.ExecuteScalar()!;
        var merged = jobs.Enqueue(item, "bridge", 100, 10, 10);
        Assert.Equal(second.Id, merged.Id);
        command.CommandText = "SELECT Created FROM Jobs";
        Assert.Equal(123L, (long)command.ExecuteScalar()!);
        command.CommandText = "SELECT Count FROM Attempts";
        Assert.Equal(3L, (long)command.ExecuteScalar()!);
        command.CommandText = "SELECT Until FROM Deferred";
        Assert.Equal(until, (long)command.ExecuteScalar()!);
        Assert.Null(jobs.ClaimNext()); // Old age must not be refreshed by the bridge intent.
        Assert.Equal("failed", jobs.List().Single().State);
    }

    [Fact]
    public void WholeFileRequestDuringPartialWarm_DoesNotRunSameMediaTwiceConcurrently()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var partial = jobs.Enqueue(item, "realtime", 10, 0, 4);
        Assert.Equal(partial.Id, jobs.ClaimNext()!.Id);
        var whole = jobs.Enqueue(item, "manual", 100);
        var other = jobs.Enqueue(Guid.NewGuid(), "manual", 0);
        Assert.Equal(other.Id, jobs.ClaimNext()!.Id);
        Assert.Null(jobs.ClaimNext());
        jobs.Finish(partial.Id, true, null);
        Assert.Equal(whole.Id, jobs.ClaimNext()!.Id);
    }

    [Fact]
    public void ConfiguredQueueAndRetryLimits_ApplyWithoutRestart()
    {
        var settings = new PrefetchSettings { QueueCapacity = 1, MaxRetries = 0 };
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"), settings: () => settings);
        var job = jobs.Enqueue(Guid.NewGuid(), "manual", 0);
        Assert.Throws<ArgumentException>(() => jobs.Enqueue(Guid.NewGuid(), "manual", 0));
        settings = settings with { QueueCapacity = 2 };
        jobs.Enqueue(Guid.NewGuid(), "manual", 0);
        Assert.Equal(job.Id, jobs.ClaimNext()!.Id);
        jobs.Defer(job.Id, "failure", TimeSpan.Zero);
        Assert.Equal("failed", jobs.List().Single(item => item.Id == job.Id).State);
    }

    [Fact]
    public void PolicyWaits_DoNotConsumeFailureRetries()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var job = jobs.Enqueue(Guid.NewGuid(), "manual", 0);
        for (var index = 0; index < 10; index++)
        {
            Assert.NotNull(jobs.ClaimNext());
            jobs.Defer(job.Id, "Playback has priority", TimeSpan.Zero, consumeAttempt: false);
        }
        Assert.Equal("queued", jobs.List().Single().State);
    }

    [Fact]
    public void FailedOwnerInsertion_RollsBackManualJob()
    {
        var path = Path.Combine(_root, "jobs.db");
        using var jobs = new PrefetchJobStore(path);
        using var inspection = new SqliteConnection("Data Source=" + path);
        inspection.Open();
        using var trigger = inspection.CreateCommand();
        trigger.CommandText = "CREATE TRIGGER RejectOwner BEFORE INSERT ON Owners BEGIN SELECT RAISE(ABORT,'injected owner failure'); END;";
        trigger.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => jobs.Enqueue(Guid.NewGuid(), "manual", 0));
        Assert.Empty(jobs.List());
    }

    [Fact]
    public void FullFileRequest_SupersedesQueuedPartialWithBothOwners()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var partial = jobs.Enqueue(item, "plex:server:minimum", 1, 4096, 4096);
        var full = jobs.Enqueue(item, "manual", 5);
        Assert.Equal(partial.Id, full.Id);
        Assert.Equal(0, full.Start);
        Assert.Equal(0, full.Length);
        Assert.Single(jobs.List());
        jobs.PruneOwners(owner => owner != "manual");
        Assert.NotNull(jobs.ClaimNext());
    }

    [Fact]
    public void DeferredWorkAndDailyBudget_SurviveRestart()
    {
        var path = Path.Combine(_root, "jobs.db");
        using (var jobs = new PrefetchJobStore(path))
        {
            var job = jobs.Enqueue(Guid.NewGuid(), "manual", 0);
            jobs.ClaimNext();
            jobs.Defer(job.Id, "No writable folder", TimeSpan.FromHours(1));
            Assert.Null(jobs.ClaimNext());
            Assert.True(jobs.TrySpendDailyBudget(60, 100));
            Assert.False(jobs.TrySpendDailyBudget(50, 100));
        }
        using var reopened = new PrefetchJobStore(path);
        Assert.Null(reopened.ClaimNext());
        Assert.True(reopened.TrySpendDailyBudget(40, 100));
        Assert.False(reopened.TrySpendDailyBudget(1, 100));
    }

    [Fact]
    public void Queue_DeduplicatesBoundsAndPersistsInterruptedWork()
    {
        var path = Path.Combine(_root, "jobs.db");
        string id;
        var item = Guid.NewGuid();
        using (var jobs = new PrefetchJobStore(path, 2))
        {
            id = jobs.Enqueue(item, "manual", 5).Id;
            Assert.Equal(id, jobs.Enqueue(item, "plex-session", 10).Id);
            jobs.Enqueue(Guid.NewGuid(), "manual", 0);
            Assert.Throws<ArgumentException>(() => jobs.Enqueue(Guid.NewGuid(), "manual", 0));
            var active = jobs.ClaimNext();
            Assert.Equal(id, active!.Id);
            jobs.Progress(id, "source-v1", 123);
        }
        using var reopened = new PrefetchJobStore(path, 2);
        Assert.Null(reopened.ClaimNext());
        reopened.Change(id, "resume");
        var resumed = reopened.ClaimNext();
        Assert.Equal(id, resumed!.Id);
        Assert.Equal(123, resumed.CommittedBytes);
        Assert.Equal("source-v1", resumed.Generation);
    }

    [Fact]
    public void SharedIntent_RetainsManualOwnerWhenAutomaticSourceIsDisabled()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var job = jobs.Enqueue(item, "plex:server:history", 1);
        Assert.Equal(job.Id, jobs.Enqueue(item, "manual", 5).Id);
        jobs.PruneOwners(owner => owner == "manual");
        Assert.Equal(job.Id, jobs.ClaimNext()!.Id);
        jobs.PruneOwners(_ => false);
        Assert.Equal("cancelled", jobs.List().Single().State);
    }

    [Fact]
    public void RetryIsBoundedAndRefreshDoesNotResetAttempts()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var job = jobs.Enqueue(item, "manual", 1);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            Assert.Equal(job.Id, jobs.ClaimNext()!.Id);
            jobs.Defer(job.Id, "unavailable", TimeSpan.Zero);
            if (attempt < 3) Assert.Equal(job.Id, jobs.Enqueue(item, "manual", 1).Id);
        }
        Assert.Null(jobs.ClaimNext());
        Assert.Equal("failed", jobs.List().Single().State);
    }

    [Fact]
    public void CancelledJob_CannotBeCompletedByLateWorker()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var job = jobs.Enqueue(Guid.NewGuid(), "manual", 0);
        jobs.ClaimNext();
        jobs.Change(job.Id, "cancel");
        jobs.Finish(job.Id, true, null);
        Assert.Equal("cancelled", jobs.List().Single().State);
        jobs.Change(job.Id, "retry");
        Assert.Equal("queued", jobs.List().Single().State);
    }

    [Fact]
    public void PausedQueue_DoesNotClaimWork_AndPriorityIsStable()
    {
        using var jobs = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        jobs.Enqueue(Guid.NewGuid(), "manual", 0);
        var first = jobs.Enqueue(Guid.NewGuid(), "manual", 5);
        jobs.SetPaused(true);
        Assert.Null(jobs.ClaimNext());
        jobs.SetPaused(false);
        Assert.Equal(first.Id, jobs.ClaimNext()!.Id);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
