using NzbWebDAV.Services.Prefetch;
using Microsoft.Data.Sqlite;

namespace NzbWebDAV.Tests.Services;

public sealed class PrefetchCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "prefetch-runner-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("claim")]
    [InlineData("disposed")]
    public async Task UnavailableMetadata_DisablesWarmingWithoutEscapingOrRepeatedAdmission(string failure)
    {
        var path = Path.Combine(_root, "jobs.db");
        using var store = new PrefetchJobStore(path);
        store.Enqueue(Guid.NewGuid(), "manual", 0);
        if (failure == "claim") ExecuteSql(path, "DROP TABLE Jobs");
        else store.Dispose();
        var admitted = 0;
        using var coordinator = new PrefetchCoordinator(store, new FailingExecutor(), () => new(), () => { admitted++; return true; });
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.NotNull(coordinator.RuntimeError);
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, admitted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedCompletionOrDeferTransition_IsContainedWithoutClaimingSuccess(bool sourceFailure)
    {
        var path = Path.Combine(_root, "jobs.db");
        using var store = new PrefetchJobStore(path);
        var job = store.Enqueue(Guid.NewGuid(), "manual", 0);
        var executor = new CallbackExecutor((_, _) =>
        {
            ExecuteSql(path, "CREATE TRIGGER FailTransitions BEFORE UPDATE ON Jobs BEGIN SELECT RAISE(ABORT,'private-metadata-error'); END");
            return sourceFailure ? Task.FromException(new IOException("private-source-error")) : Task.CompletedTask;
        });
        using var coordinator = new PrefetchCoordinator(store, executor, () => new(), () => true);
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.NotNull(coordinator.RuntimeError);
        Assert.DoesNotContain("private", coordinator.RuntimeError);
        Assert.Equal("running", Assert.Single(store.List()).State);
        ExecuteSql(path, "DROP TRIGGER FailTransitions; DROP TABLE State");
        await coordinator.RunOnceAsync(CancellationToken.None); // A latched fault must not touch failed metadata again.
        Assert.Equal(0, Assert.Single(store.List()).CommittedBytes);
    }

    [Fact]
    public async Task ClaimFailure_CancelsAlreadyStartedSibling_AndHostedLoopStopsCleanly()
    {
        var path = Path.Combine(_root, "jobs.db");
        using var store = new PrefetchJobStore(path);
        store.Enqueue(Guid.NewGuid(), "manual", 0);
        store.Enqueue(Guid.NewGuid(), "manual", 0);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new CallbackExecutor(async (_, ct) =>
        {
            ExecuteSql(path, "DROP TABLE Jobs");
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { cancelled.TrySetResult(); }
        });
        using var coordinator = new PrefetchCoordinator(store, executor, () => new() { MaxConcurrentJobs = 2 }, () => true);
        await coordinator.StartAsync(CancellationToken.None);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(coordinator.RuntimeError);
        Assert.False(coordinator.ExecuteTask!.IsCompleted);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await coordinator.StopAsync(stop.Token);
        Assert.True(coordinator.ExecuteTask.IsCompletedSuccessfully);
    }

    private static void ExecuteSql(string path, string sql)
    {
        using var database = new SqliteConnection("Data Source=" + path);
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task ShutdownCancellation_IsClean_AndOutOfMemoryIsNotHidden()
    {
        using var store = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        store.Enqueue(Guid.NewGuid(), "manual", 0);
        using var coordinator = new PrefetchCoordinator(store,
            new CallbackExecutor((_, _) => Task.FromException(new OutOfMemoryException("injected"))), () => new(), () => true);
        await coordinator.RunOnceAsync(new CancellationToken(canceled: true));
        Assert.Null(coordinator.RuntimeError);
        Assert.Equal("queued", Assert.Single(store.List()).State);
        await Assert.ThrowsAsync<OutOfMemoryException>(() => coordinator.RunOnceAsync(CancellationToken.None));
        Assert.Null(coordinator.RuntimeError);
    }

    private sealed class CallbackExecutor(Func<PrefetchJob, CancellationToken, Task> callback) : IPrefetchExecutor
    {
        public Task ExecuteAsync(PrefetchJob job, CancellationToken ct) => callback(job, ct);
    }

    [Theory]
    [InlineData(0, "failed")]
    [InlineData(1, "queued")]
    public async Task TransientSourceFailure_UsesConfiguredBoundedRetry(int retries, string expected)
    {
        using var store = new PrefetchJobStore(Path.Combine(_root, "jobs.db"), settings: () => new() { MaxRetries = retries });
        store.Enqueue(Guid.NewGuid(), "manual", 0);
        using var coordinator = new PrefetchCoordinator(store, new FailingExecutor(), () => new(), () => true);
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(expected, Assert.Single(store.List()).State);
    }

    private sealed class FailingExecutor : IPrefetchExecutor
    {
        public Task ExecuteAsync(PrefetchJob job, CancellationToken ct) => throw new IOException("Temporary source failure");
    }

    [Fact]
    public async Task Cancellation_InterruptsWorkerAndCannotBecomeCompleted()
    {
        using var store = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var job = store.Enqueue(Guid.NewGuid(), "manual", 0);
        var executor = new BlockingExecutor();
        using var coordinator = new PrefetchCoordinator(store, executor, () => new(), () => true);
        var run = coordinator.RunOnceAsync(CancellationToken.None);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.Change(job.Id, "cancel");
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("cancelled", store.List().Single().State);
    }

    [Fact]
    public async Task RemovingLastSource_CancelsActiveIoButRetainsManualOwnership()
    {
        using var store = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var item = Guid.NewGuid();
        var job = store.Enqueue(item, "source", 0);
        store.Enqueue(item, "manual", 0);
        var executor = new BlockingExecutor();
        using var coordinator = new PrefetchCoordinator(store, executor, () => new(), () => true);
        var run = coordinator.RunOnceAsync(CancellationToken.None);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.PruneOwners(owner => owner == "manual");
        Assert.Equal("running", store.List().Single().State);
        coordinator.PruneOwners(_ => false);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("cancelled", store.List().Single().State);
    }

    [Fact]
    public async Task AdmissionPressure_DoesNotClaimQueuedWork()
    {
        using var store = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        store.Enqueue(Guid.NewGuid(), "manual", 0);
        var executor = new BlockingExecutor();
        using var coordinator = new PrefetchCoordinator(store, executor, () => new(), () => false);
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.False(executor.Started.Task.IsCompleted);
        Assert.Equal("queued", store.List().Single().State);
    }

    private sealed class BlockingExecutor : IPrefetchExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ExecuteAsync(PrefetchJob job, CancellationToken ct)
        {
            Started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
