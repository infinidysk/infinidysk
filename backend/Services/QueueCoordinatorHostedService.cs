using Microsoft.Extensions.Hosting;
using NzbWebDAV.Queue;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Owns the queue coordinator for the process lifetime. Any unexpected
/// termination faults this BackgroundService so the host exits nonzero.
/// </summary>
public sealed class QueueCoordinatorHostedService : BackgroundService, IQueueCoordinatorLiveness
{
    private readonly Func<CancellationToken, Task> _runCoordinator;
    private readonly IHostApplicationLifetime _lifetime;
    private int _state = (int)QueueCoordinatorState.NotStarted;

    public QueueCoordinatorHostedService(
        QueueManager queueManager,
        IHostApplicationLifetime lifetime)
        : this(queueManager.StartProcessing, lifetime)
    {
    }

    // Allows lifecycle tests to supply a deterministic coordinator task.
    internal QueueCoordinatorHostedService(
        Func<CancellationToken, Task> runCoordinator,
        IHostApplicationLifetime lifetime)
    {
        _runCoordinator = runCoordinator;
        _lifetime = lifetime;
    }

    public QueueCoordinatorState GetState() =>
        (QueueCoordinatorState)Volatile.Read(ref _state);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await WaitForApplicationStartedAsync(stoppingToken).ConfigureAwait(false))
        {
            SetState(QueueCoordinatorState.Stopped);
            return;
        }

        SetState(QueueCoordinatorState.Running);

        try
        {
            // Directly await the coordinator. WaitAsync(stoppingToken) would let
            // shutdown cancellation mask a concurrent coordinator fault.
            await _runCoordinator(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (IsHostStopping(stoppingToken))
        {
            SetState(QueueCoordinatorState.Stopped);
            return;
        }
        catch (OperationCanceledException exception)
        {
            var fault = new InvalidOperationException(
                "Queue coordinator canceled without a host shutdown request.",
                exception);
            SetState(QueueCoordinatorState.Faulted);
            Log.Fatal(
                fault,
                "Queue coordinator canceled unexpectedly; stopping the backend");
            throw fault;
        }
        catch (Exception exception)
        {
            // Deliberately includes OutOfMemoryException. OOM is the confirmed
            // incident class and must reach the BackgroundService fault boundary.
            SetState(QueueCoordinatorState.Faulted);
            Log.Fatal(
                exception,
                "Queue coordinator faulted; stopping the backend instead of serving a dead queue");
            throw;
        }

        if (IsHostStopping(stoppingToken))
        {
            SetState(QueueCoordinatorState.Stopped);
            return;
        }

        var unexpectedExit = new InvalidOperationException(
            "Queue coordinator exited while the host was still running.");
        SetState(QueueCoordinatorState.Faulted);
        Log.Fatal(
            unexpectedExit,
            "Queue coordinator exited unexpectedly; stopping the backend");
        throw unexpectedExit;
    }

    private void SetState(QueueCoordinatorState state) =>
        Volatile.Write(ref _state, (int)state);

    private bool IsHostStopping(CancellationToken stoppingToken) =>
        stoppingToken.IsCancellationRequested ||
        _lifetime.ApplicationStopping.IsCancellationRequested;

    private async Task<bool> WaitForApplicationStartedAsync(
        CancellationToken stoppingToken)
    {
        if (_lifetime.ApplicationStarted.IsCancellationRequested)
        {
            TryTransition(QueueCoordinatorState.NotStarted, QueueCoordinatorState.Running);
            return true;
        }

        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = _lifetime.ApplicationStarted.Register(() =>
        {
            // Set Running on the ApplicationStarted thread so /health cannot
            // observe NotStarted after the host has already started serving.
            TryTransition(QueueCoordinatorState.NotStarted, QueueCoordinatorState.Running);
            started.TrySetResult();
        });

        try
        {
            await started.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private bool TryTransition(QueueCoordinatorState from, QueueCoordinatorState to) =>
        Interlocked.CompareExchange(ref _state, (int)to, (int)from) == (int)from;
}
