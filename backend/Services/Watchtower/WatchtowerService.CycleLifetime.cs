using System.Diagnostics;
using System.Runtime.ExceptionServices;
using NzbWebDAV.Extensions;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Hosted-loop ownership for a single Watchtower cycle.
/// </summary>
/// <remarks>
/// Permanent noncooperation policy (issue #1243): after cancellation is requested,
/// await the exact <c>RunCycleAsync</c> task indefinitely. Never detach it and never
/// start a replacement while that task or its memoized <c>CancelAsync</c> task is
/// incomplete. A stuck cycle can delay graceful Watchtower shutdown past
/// <c>HostOptions.ShutdownTimeout</c>; the host may then abort the process. Operators
/// see one watchdog warning and no further cycle starts. Streaming readiness is
/// unaffected. Independently owned work in <c>NzbFetchCoalescer</c>,
/// <c>PlaybackFastVerifier</c> probe cores, and best-effort <c>IndexerHitTracker</c>
/// records is not claimed to be drained by the cycle task.
/// <para>
/// Multiple <see cref="OutOfMemoryException"/> instances: propagate the
/// cancellation-task OOM first, then the cycle-task OOM. Do not log secondary OOMs
/// and do not wrap the selected OOM in a new aggregate.
/// </para>
/// </remarks>
public partial class WatchtowerService
{
    internal enum CycleStopReason
    {
        None = 0,
        CycleFinished = 1,
        Watchdog = 2,
        Disabled = 3,
        HostStopping = 4,
    }

    private enum TaskObservationStatus
    {
        RanToCompletion,
        Canceled,
        Faulted,
    }

    private readonly record struct TaskObservation(
        TaskObservationStatus Status,
        ExceptionDispatchInfo? Exception);

    private sealed class CycleObservation
    {
        public required CycleStopReason StopReason { get; init; }
        public required bool WatchdogWarningEmitted { get; init; }
        public ExceptionDispatchInfo? Failure { get; init; }
        public TimeSpan DrainElapsed { get; init; }
        public bool ExpectedCycleCancellation { get; init; }
        public bool CancellationFailed { get; init; }
        public bool RegistrationFailed { get; init; }
    }

    internal sealed class ActiveCycle : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancellationSource = new();
        private readonly TaskCompletionSource _stopRequested =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _bothTasksObservedSignal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _ownerClearedSignal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TimeProvider _timeProvider;

        private CycleStopReason _stopReason;
        private Task? _cancellationTask;
        private Task? _cycleTask;
        private bool _cancellationStartedWhileCycleActive;
        private bool _bothTasksObserved;
        private bool _hostRegistrationDisposed;
        private DateTimeOffset? _drainStartedAt;

        private bool _disposed;

        internal ActiveCycle() : this(0, TimeProvider.System)
        {
        }

        internal ActiveCycle(int cycleId, TimeProvider timeProvider)
        {
            CycleId = cycleId;
            _timeProvider = timeProvider;
        }

        internal int CycleId { get; }

        internal CancellationToken Token => _cancellationSource.Token;

        internal Task StopRequested => _stopRequested.Task;

        internal Task WhenBothTasksObserved => _bothTasksObservedSignal.Task;

        internal Task WhenOwnerCleared => _ownerClearedSignal.Task;

        internal CycleStopReason StopReason
        {
            get
            {
                lock (_gate)
                    return _stopReason;
            }
        }

        internal Task? CycleTask
        {
            get
            {
                lock (_gate)
                    return _cycleTask;
            }
        }

        internal Task? CancellationTask
        {
            get
            {
                lock (_gate)
                    return _cancellationTask;
            }
        }

        internal bool BothTasksObserved
        {
            get
            {
                lock (_gate)
                    return _bothTasksObserved;
            }
        }

        internal bool HostRegistrationDisposed
        {
            get
            {
                lock (_gate)
                    return _hostRegistrationDisposed;
            }
        }

        internal bool CancellationStartedWhileCycleActive
        {
            get
            {
                lock (_gate)
                    return _cancellationStartedWhileCycleActive;
            }
        }

        internal bool WatchdogWarningEmitted { get; set; }

        internal void AttachCycleTask(Task cycleTask)
        {
            ArgumentNullException.ThrowIfNull(cycleTask);
            lock (_gate)
            {
                if (_cycleTask is not null)
                    throw new InvalidOperationException("Watchtower cycle task is already attached.");
                _cycleTask = cycleTask;
            }
        }

        internal Task RequestCancellation(CycleStopReason reason)
        {
            if (reason == CycleStopReason.None)
                throw new ArgumentOutOfRangeException(nameof(reason));

            lock (_gate)
            {
                if (reason > _stopReason)
                    _stopReason = reason;

                if (_cancellationTask is null)
                {
                    _cancellationStartedWhileCycleActive =
                        reason != CycleStopReason.CycleFinished
                        && (_cycleTask is null || !_cycleTask.IsCompleted);
                    _drainStartedAt = _timeProvider.GetUtcNow();
                    _cancellationTask = _cancellationSource.CancelAsync();
                    _stopRequested.TrySetResult();
                }

                return _cancellationTask;
            }
        }

        internal Task GetCancellationTaskOrThrow()
        {
            lock (_gate)
            {
                return _cancellationTask
                    ?? throw new InvalidOperationException(
                        "Watchtower cycle cancellation has not been requested.");
            }
        }

        internal void MarkBothTasksObserved()
        {
            lock (_gate)
                _bothTasksObserved = true;
            _bothTasksObservedSignal.TrySetResult();
        }

        internal void MarkHostRegistrationDisposed()
        {
            lock (_gate)
                _hostRegistrationDisposed = true;
        }

        internal void MarkOwnerCleared() => _ownerClearedSignal.TrySetResult();

        internal TimeSpan DrainElapsed()
        {
            lock (_gate)
            {
                if (_drainStartedAt is not { } started)
                    return TimeSpan.Zero;
                var elapsed = _timeProvider.GetUtcNow() - started;
                return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
            }

            _cancellationSource.Dispose();
        }
    }

    private void PublishActiveCycle(ActiveCycle cycle)
    {
        lock (_activeCycleLock)
        {
            if (_activeCycle is not null)
                throw new InvalidOperationException("A Watchtower cycle is already active.");
            _activeCycle = cycle;
        }
    }

    private void ClearTerminalCycle(ActiveCycle cycle)
    {
        lock (_activeCycleLock)
        {
            if (!ReferenceEquals(_activeCycle, cycle))
                throw new InvalidOperationException("Watchtower active-cycle ownership changed.");
            if (!cycle.BothTasksObserved)
                throw new InvalidOperationException(
                    "Cannot clear a Watchtower cycle before both tasks are observed.");
            if (!cycle.HostRegistrationDisposed)
                throw new InvalidOperationException(
                    "Cannot clear a Watchtower cycle before host registration disposal.");
            if (cycle.CycleTask is { IsCompleted: false })
                throw new InvalidOperationException("Cannot clear a running Watchtower cycle.");
            if (cycle.CancellationTask is { IsCompleted: false })
                throw new InvalidOperationException(
                    "Cannot clear a Watchtower cycle before cancellation completes.");
            _activeCycle = null;
            LastClearedCycleForTests = cycle;
        }

        cycle.MarkOwnerCleared();
    }

    private async Task<CycleObservation> RunSupervisedCycleAsync(CancellationToken stoppingToken)
    {
        var active = new ActiveCycle(Interlocked.Increment(ref _nextCycleId), _timeProvider);
        PublishActiveCycle(active);

        var hostRegistration = stoppingToken.UnsafeRegister(
            static state =>
                _ = ((ActiveCycle)state!).RequestCancellation(CycleStopReason.HostStopping),
            active);

        if (stoppingToken.IsCancellationRequested)
            _ = active.RequestCancellation(CycleStopReason.HostStopping);

        if (!configManager.IsWatchtowerEnabled())
            _ = active.RequestCancellation(CycleStopReason.Disabled);

        Task cycleTask;
        try
        {
            var cycleWatch = Stopwatch.StartNew();
            cycleTask = RunCycleOverride is null
                ? RunCycleAsync(cycleWatch, active.Token)
                : RunCycleOverride(cycleWatch, active.Token);
        }
        catch (OperationCanceledException ex)
        {
            cycleTask = Task.FromException(ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            cycleTask = Task.FromException(ex);
        }

        active.AttachCycleTask(cycleTask);

        var watchdogTask = Task.Delay(CycleWatchdogTimeout, _timeProvider, active.Token);

        await Task.WhenAny(cycleTask, watchdogTask, active.StopRequested).ConfigureAwait(false);

        if (stoppingToken.IsCancellationRequested)
            _ = active.RequestCancellation(CycleStopReason.HostStopping);

        if (!configManager.IsWatchtowerEnabled())
            _ = active.RequestCancellation(CycleStopReason.Disabled);

        if (watchdogTask.Status == TaskStatus.RanToCompletion)
            _ = active.RequestCancellation(CycleStopReason.Watchdog);

        if (cycleTask.IsCompleted)
            _ = active.RequestCancellation(CycleStopReason.CycleFinished);

        if (active.CancellationTask is null)
            _ = active.RequestCancellation(CycleStopReason.CycleFinished);

        var cancellationTask = active.GetCancellationTaskOrThrow();

        if (active.StopReason == CycleStopReason.Watchdog
            && watchdogTask.Status == TaskStatus.RanToCompletion)
        {
            Log.Warning(
                "Watchtower: cycle exceeded {Budget:n0}s; cancellation requested, waiting for completion",
                CycleWatchdogTimeout.TotalSeconds);
            active.WatchdogWarningEmitted = true;
        }

        return await ObserveOwnedTasksAsync(
                active,
                cancellationTask,
                cycleTask,
                watchdogTask,
                hostRegistration,
                stoppingToken)
            .ConfigureAwait(false);
    }

    private async Task<CycleObservation> ObserveOwnedTasksAsync(
        ActiveCycle active,
        Task cancellationTask,
        Task cycleTask,
        Task watchdogTask,
        CancellationTokenRegistration hostRegistration,
        CancellationToken stoppingToken)
    {
        var cancellationObservationTask = ObserveExactTaskAsync(cancellationTask);
        var cycleObservationTask = ObserveExactTaskAsync(cycleTask);

        await Task.WhenAll(cancellationObservationTask, cycleObservationTask).ConfigureAwait(false);

        var cancellationObservation = await cancellationObservationTask.ConfigureAwait(false);
        var cycleObservation = await cycleObservationTask.ConfigureAwait(false);

        if (stoppingToken.IsCancellationRequested)
            _ = active.RequestCancellation(CycleStopReason.HostStopping);

        if (!configManager.IsWatchtowerEnabled())
            _ = active.RequestCancellation(CycleStopReason.Disabled);

        ExceptionDispatchInfo? watchdogFailure = null;
        try
        {
            await watchdogTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Owner cancellation cancelled the watchdog delay.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            watchdogFailure = ExceptionDispatchInfo.Capture(ex);
        }

        ExceptionDispatchInfo? registrationFailure = null;
        try
        {
            await hostRegistration.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            registrationFailure = ExceptionDispatchInfo.Capture(ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            registrationFailure = ExceptionDispatchInfo.Capture(ex);
        }

        active.MarkHostRegistrationDisposed();

        var oom = SelectOutOfMemory(
            cancellationObservation.Exception,
            cycleObservation.Exception,
            watchdogFailure,
            registrationFailure);

        var expectedCycleCancellation = IsExpectedCycleCancellation(
            active,
            cycleObservation,
            active.StopReason);

        var cycleFailure = expectedCycleCancellation ? null : cycleObservation.Exception;
        var failure = CombineNonfatalFailures(
            cancellationObservation.Exception,
            cycleFailure,
            watchdogFailure,
            registrationFailure);

        active.MarkBothTasksObserved();
        ClearTerminalCycle(active);
        active.Dispose();

        if (oom is not null)
            ExceptionDispatchInfo.Capture(oom).Throw();

        return new CycleObservation
        {
            StopReason = active.StopReason,
            WatchdogWarningEmitted = active.WatchdogWarningEmitted,
            Failure = failure,
            DrainElapsed = active.DrainElapsed(),
            ExpectedCycleCancellation = expectedCycleCancellation,
            CancellationFailed = cancellationObservation.Exception is not null,
            RegistrationFailed = registrationFailure is not null,
        };
    }

    private static async Task<TaskObservation> ObserveExactTaskAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
            return new TaskObservation(TaskObservationStatus.RanToCompletion, null);
        }
        catch (Exception ex)
        {
            // CancelAsync stores every callback failure on task.Exception. await
            // unwraps only one inner exception, which would drop a later OOM.
            if (task.Exception is { } aggregate)
            {
                var status = IsOnlyOperationCanceled(aggregate)
                    ? TaskObservationStatus.Canceled
                    : TaskObservationStatus.Faulted;
                return new TaskObservation(status, ExceptionDispatchInfo.Capture(aggregate));
            }

            if (ex is OperationCanceledException oce)
                return new TaskObservation(TaskObservationStatus.Canceled, ExceptionDispatchInfo.Capture(oce));

            return new TaskObservation(TaskObservationStatus.Faulted, ExceptionDispatchInfo.Capture(ex));
        }
    }

    private static bool IsExpectedCycleCancellation(
        ActiveCycle active,
        TaskObservation cycle,
        CycleStopReason reason)
    {
        if (!active.CancellationStartedWhileCycleActive)
            return false;
        if (reason is CycleStopReason.None or CycleStopReason.CycleFinished)
            return false;
        if (!active.Token.IsCancellationRequested)
            return false;
        if (cycle.Status == TaskObservationStatus.Canceled)
            return true;
        return IsOnlyOperationCanceled(cycle.Exception?.SourceException);
    }

    private static bool IsOnlyOperationCanceled(Exception? exception)
    {
        if (exception is OperationCanceledException)
            return true;
        if (exception is not AggregateException aggregate)
            return false;
        var leaves = aggregate.Flatten().InnerExceptions;
        return leaves.Count > 0 && leaves.All(inner => inner is OperationCanceledException);
    }

    private static OutOfMemoryException? SelectOutOfMemory(params ExceptionDispatchInfo?[] sources)
    {
        return sources
            .Select(TryGetOutOfMemory)
            .FirstOrDefault(oom => oom is not null);
    }

    private static OutOfMemoryException? TryGetOutOfMemory(ExceptionDispatchInfo? source) =>
        source?.SourceException.TryGetCausingException<OutOfMemoryException>(out var oom) == true
            ? oom
            : null;

    private static ExceptionDispatchInfo? CombineNonfatalFailures(params ExceptionDispatchInfo?[] sources)
    {
        List<Exception>? leaves = null;
        foreach (var source in sources.Where(source =>
                     source is not null
                     && !source.SourceException.TryGetCausingException<OutOfMemoryException>(out _)))
        {
            foreach (var leaf in EnumerateLeaves(source!.SourceException))
            {
                leaves ??= [];
                if (!leaves.Exists(existing => ReferenceEquals(existing, leaf)))
                    leaves.Add(leaf);
            }
        }

        if (leaves is null || leaves.Count == 0)
            return null;
        if (leaves.Count == 1)
            return ExceptionDispatchInfo.Capture(leaves[0]);
        return ExceptionDispatchInfo.Capture(new AggregateException(leaves));
    }

    private static IEnumerable<Exception> EnumerateLeaves(Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
                yield return inner;
            yield break;
        }

        yield return exception;
    }

    private static void LogWatchdogDrainComplete(CycleObservation observation)
    {
        var outcome = observation.ExpectedCycleCancellation
            ? "cancelled"
            : "completed after cancellation";
        Log.Information(
            "Watchtower: timed-out cycle drained after {DrainElapsed:n1}s ({Outcome})",
            observation.DrainElapsed.TotalSeconds,
            outcome);
    }

    private static void LogCycleFailure(CycleObservation observation)
    {
        var exception = observation.Failure!.SourceException;
        if (observation.CancellationFailed || observation.RegistrationFailed)
        {
            Log.Error(exception, "Watchtower: unexpected cycle cancellation failure");
            return;
        }

        if (observation.WatchdogWarningEmitted)
        {
            Log.Warning(exception, "Watchtower: timed-out cycle faulted while draining");
            return;
        }

        exception.LogWarningKnownOrStack("Watchtower loop error.");
    }

    private static bool ContainsOutOfMemory(Exception exception) =>
        exception.TryGetCausingException<OutOfMemoryException>(out _);
}
