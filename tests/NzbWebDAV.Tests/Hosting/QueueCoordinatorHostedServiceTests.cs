using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NzbWebDAV;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Hosting;

[Collection(nameof(HttpIntegrationCollection))]
public sealed class QueueCoordinatorHostedServiceTests
{
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task OutOfMemoryException_FaultsHostAndSetsExitCodeOne()
    {
        await AssertFaultStopsHostAsync(
            coordinator => coordinator.Fault(new OutOfMemoryException("deterministic coordinator OOM")));
    }

    [Fact]
    public async Task SynchronousStartupException_FaultsHostAndSetsExitCodeOne()
    {
        var priorExitCode = Environment.ExitCode;
        IHostApplicationLifetime? lifetime = null;
        Task? runTask = null;
        try
        {
            Environment.ExitCode = 0;
            var host = CreateHost(
                _ => throw new InvalidOperationException("deterministic coordinator startup failure"),
                out var service,
                out lifetime);

            runTask = Program.RunHostAndSetExitCodeAsync(host);
            var thrown = await Record.ExceptionAsync(() => runTask.WaitAsync(GateTimeout));

            Assert.Null(thrown);
            Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
            Assert.True(service.ExecuteTask!.IsFaulted);
            Assert.Equal(QueueCoordinatorState.Faulted, service.GetState());
            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            if (runTask is not null && lifetime is not null)
                await StopIfRunningAsync(runTask, lifetime);
            Environment.ExitCode = priorExitCode;
        }
    }

    [Fact]
    public async Task AsynchronousException_FaultsHostAndSetsExitCodeOne()
    {
        await AssertFaultStopsHostAsync(
            coordinator => coordinator.Fault(
                new InvalidOperationException("deterministic coordinator fault")));
    }

    [Fact]
    public async Task UnexpectedCancellation_IsConvertedToFaultAndSetsExitCodeOne()
    {
        var priorExitCode = Environment.ExitCode;
        IHostApplicationLifetime? lifetime = null;
        Task? runTask = null;
        try
        {
            Environment.ExitCode = 0;
            var coordinator = new ControlledCoordinator();
            var host = CreateHost(coordinator.RunAsync, out var service, out lifetime);

            runTask = Program.RunHostAndSetExitCodeAsync(host);
            await coordinator.Started.Task.WaitAsync(GateTimeout);
            coordinator.Cancel();

            var thrown = await Record.ExceptionAsync(() => runTask.WaitAsync(GateTimeout));
            Assert.Null(thrown);
            Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
            Assert.True(service.ExecuteTask!.IsFaulted);
            Assert.False(service.ExecuteTask.IsCanceled);
            Assert.Equal(QueueCoordinatorState.Faulted, service.GetState());
            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            if (runTask is not null && lifetime is not null)
                await StopIfRunningAsync(runTask, lifetime);
            Environment.ExitCode = priorExitCode;
        }
    }

    [Fact]
    public async Task UnexpectedCleanCompletion_FaultsHostAndSetsExitCodeOne()
    {
        await AssertFaultStopsHostAsync(coordinator => coordinator.Complete());
    }

    [Fact]
    public async Task GracefulHostStop_RecordsStoppedAndLeavesExitCodeZero()
    {
        var priorExitCode = Environment.ExitCode;
        IHostApplicationLifetime? lifetime = null;
        Task? runTask = null;
        try
        {
            Environment.ExitCode = 0;
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task RunUntilStoppedAsync(CancellationToken stoppingToken)
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            }

            var host = CreateHost(RunUntilStoppedAsync, out var service, out lifetime);

            runTask = Program.RunHostAndSetExitCodeAsync(host);
            await started.Task.WaitAsync(GateTimeout);
            lifetime.StopApplication();

            var thrown = await Record.ExceptionAsync(() => runTask.WaitAsync(GateTimeout));
            Assert.Null(thrown);
            Assert.False(service.ExecuteTask!.IsFaulted);
            Assert.Equal(QueueCoordinatorState.Stopped, service.GetState());
            Assert.Equal(0, Environment.ExitCode);
        }
        finally
        {
            if (runTask is not null && lifetime is not null)
                await StopIfRunningAsync(runTask, lifetime);
            Environment.ExitCode = priorExitCode;
        }
    }

    [Fact]
    public async Task ApplicationStopping_BeforeHostedServiceToken_IsGraceful()
    {
        var priorExitCode = Environment.ExitCode;
        IHostApplicationLifetime? lifetime = null;
        Task? runTask = null;
        try
        {
            Environment.ExitCode = 0;
            IHostApplicationLifetime? capturedLifetime = null;
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task RunUntilApplicationStoppingAsync(CancellationToken _)
            {
                started.TrySetResult();
                var completion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = capturedLifetime!.ApplicationStopping.Register(
                    () => completion.TrySetResult());
                await completion.Task;
            }

            var host = CreateHost(RunUntilApplicationStoppingAsync, out var service, out lifetime);
            capturedLifetime = lifetime;

            runTask = Program.RunHostAndSetExitCodeAsync(host);
            await started.Task.WaitAsync(GateTimeout);
            lifetime.StopApplication();

            var thrown = await Record.ExceptionAsync(() => runTask.WaitAsync(GateTimeout));
            Assert.Null(thrown);
            Assert.False(service.ExecuteTask!.IsFaulted);
            Assert.Equal(QueueCoordinatorState.Stopped, service.GetState());
            Assert.Equal(0, Environment.ExitCode);
        }
        finally
        {
            if (runTask is not null && lifetime is not null)
                await StopIfRunningAsync(runTask, lifetime);
            Environment.ExitCode = priorExitCode;
        }
    }

    [Fact]
    public async Task HostStopRacingWithFault_StillRecordsFaultedAndExitsOne()
    {
        var priorExitCode = Environment.ExitCode;
        IHostApplicationLifetime? lifetime = null;
        Task? runTask = null;
        try
        {
            Environment.ExitCode = 0;
            var coordinator = new ControlledCoordinator();
            var host = CreateHost(coordinator.RunAsync, out var service, out lifetime);

            runTask = Program.RunHostAndSetExitCodeAsync(host);
            await coordinator.Started.Task.WaitAsync(GateTimeout);
            lifetime.StopApplication();
            coordinator.Fault(new InvalidOperationException("shutdown race fault"));

            var thrown = await Record.ExceptionAsync(() => runTask.WaitAsync(GateTimeout));
            Assert.Null(thrown);
            Assert.True(service.ExecuteTask!.IsFaulted);
            Assert.Equal(QueueCoordinatorState.Faulted, service.GetState());
            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            if (runTask is not null && lifetime is not null)
                await StopIfRunningAsync(runTask, lifetime);
            Environment.ExitCode = priorExitCode;
        }
    }

    [Fact]
    public async Task Coordinator_IsNotInvokedUntilApplicationStarted()
    {
        using var lifetime = new TestHostApplicationLifetime();
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task RunAsync(CancellationToken stoppingToken)
        {
            invoked.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }

        using var service = new QueueCoordinatorHostedService(RunAsync, lifetime);
        await service.StartAsync(CancellationToken.None);

        Assert.Equal(QueueCoordinatorState.NotStarted, service.GetState());
        Assert.False(invoked.Task.IsCompleted);

        lifetime.SignalStarted();
        await invoked.Task.WaitAsync(GateTimeout);
        Assert.Equal(QueueCoordinatorState.Running, service.GetState());

        await service.StopAsync(CancellationToken.None);
        Assert.Equal(QueueCoordinatorState.Stopped, service.GetState());
        Assert.False(service.ExecuteTask!.IsFaulted);
    }

    private static async Task AssertFaultStopsHostAsync(Action<ControlledCoordinator> terminate)
    {
        var priorExitCode = Environment.ExitCode;
        IHostApplicationLifetime? lifetime = null;
        Task? runTask = null;
        try
        {
            Environment.ExitCode = 0;
            var coordinator = new ControlledCoordinator();
            var host = CreateHost(coordinator.RunAsync, out var service, out lifetime);

            runTask = Program.RunHostAndSetExitCodeAsync(host);
            await coordinator.Started.Task.WaitAsync(GateTimeout);
            terminate(coordinator);

            var thrown = await Record.ExceptionAsync(() => runTask.WaitAsync(GateTimeout));
            Assert.Null(thrown);
            Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
            Assert.True(service.ExecuteTask!.IsFaulted);
            Assert.Equal(QueueCoordinatorState.Faulted, service.GetState());
            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            if (runTask is not null && lifetime is not null)
                await StopIfRunningAsync(runTask, lifetime);
            Environment.ExitCode = priorExitCode;
        }
    }

    private static IHost CreateHost(
        Func<CancellationToken, Task> runCoordinator,
        out QueueCoordinatorHostedService service,
        out IHostApplicationLifetime lifetime)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromSeconds(5);
            options.BackgroundServiceExceptionBehavior =
                BackgroundServiceExceptionBehavior.StopHost;
        });
        builder.Services.AddSingleton<ProcessExitCoordinator>();
        builder.Services.AddSingleton(sp => new QueueCoordinatorHostedService(
            runCoordinator,
            sp.GetRequiredService<IHostApplicationLifetime>()));
        builder.Services.AddSingleton<IQueueCoordinatorLiveness>(sp =>
            sp.GetRequiredService<QueueCoordinatorHostedService>());
        builder.Services.AddHostedService(sp =>
            sp.GetRequiredService<QueueCoordinatorHostedService>());

        var host = builder.Build();
        service = host.Services.GetRequiredService<QueueCoordinatorHostedService>();
        lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        return host;
    }

    private static async Task StopIfRunningAsync(
        Task runTask,
        IHostApplicationLifetime lifetime)
    {
        if (!runTask.IsCompleted)
            lifetime.StopApplication();

        try
        {
            await runTask.WaitAsync(GateTimeout);
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
            // Teardown only: the test body already observed success or failure.
        }
    }

    private sealed class ControlledCoordinator
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RunAsync(CancellationToken _)
        {
            Started.TrySetResult();
            return _completion.Task;
        }

        public void Complete() => _completion.TrySetResult();
        public void Cancel() => _completion.TrySetCanceled();
        public void Fault(Exception exception) => _completion.TrySetException(exception);
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void SignalStarted() => _started.Cancel();
        public void StopApplication() => _stopping.Cancel();

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }
}
