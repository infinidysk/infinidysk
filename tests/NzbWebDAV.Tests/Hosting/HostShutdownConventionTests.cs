using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NzbWebDAV;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestUtils;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Hosting;

[Collection(nameof(HttpIntegrationCollection))]
public sealed class HostShutdownConventionTests(NzbDavWebApplicationFactory factory)
{
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void HostShutdownTimeout_IsFiveSeconds()
    {
        var options = factory.Services.GetRequiredService<IOptions<HostOptions>>();
        Assert.Equal(TimeSpan.FromSeconds(5), options.Value.ShutdownTimeout);
    }

    [Fact]
    public void BackgroundServiceExceptionBehavior_IsStopHost()
    {
        var options = factory.Services.GetRequiredService<IOptions<HostOptions>>();
        Assert.Equal(
            BackgroundServiceExceptionBehavior.StopHost,
            options.Value.BackgroundServiceExceptionBehavior);
    }

    [Fact]
    public async Task FrameworkRunAsync_StopHostFault_CompletesNormallyAndLeavesExitCodeZero()
    {
        var priorExitCode = Environment.ExitCode;
        IHostApplicationLifetime? lifetime = null;
        Task? runTask = null;
        try
        {
            Environment.ExitCode = 0;

            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.Configure<HostOptions>(options =>
                options.BackgroundServiceExceptionBehavior =
                    BackgroundServiceExceptionBehavior.StopHost);
            builder.Services.AddSingleton<ControlledBackgroundService>();
            builder.Services.AddHostedService(sp =>
                sp.GetRequiredService<ControlledBackgroundService>());

            var host = builder.Build();
            var service = host.Services.GetRequiredService<ControlledBackgroundService>();
            lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

            runTask = host.RunAsync();
            await service.Started.Task.WaitAsync(GateTimeout);
            service.Fail();

            var thrown = await Record.ExceptionAsync(() => runTask.WaitAsync(GateTimeout));
            Assert.Null(thrown);
            Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
            Assert.True(service.ExecuteTask?.IsFaulted);
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
    public async Task RunHostAndSetExitCodeAsync_RuntimeFault_SetsExitCodeOne()
    {
        var priorExitCode = Environment.ExitCode;
        IHostApplicationLifetime? lifetime = null;
        Task? runTask = null;
        try
        {
            Environment.ExitCode = 0;
            var host = CreateControlledHost(out var service, out lifetime, out _);

            runTask = Program.RunHostAndSetExitCodeAsync(host);
            await service.Started.Task.WaitAsync(GateTimeout);
            service.Fail();

            var thrown = await Record.ExceptionAsync(() => runTask.WaitAsync(GateTimeout));
            Assert.Null(thrown);
            Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
            Assert.True(service.ExecuteTask?.IsFaulted);
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
    public async Task RunHostAndSetExitCodeAsync_GracefulStop_LeavesExitCodeZero()
    {
        var priorExitCode = Environment.ExitCode;
        IHostApplicationLifetime? lifetime = null;
        Task? runTask = null;
        try
        {
            Environment.ExitCode = 0;
            var host = CreateControlledHost(out var service, out lifetime, out _);

            runTask = Program.RunHostAndSetExitCodeAsync(host);
            await service.Started.Task.WaitAsync(GateTimeout);
            lifetime.StopApplication();

            var thrown = await Record.ExceptionAsync(() => runTask.WaitAsync(GateTimeout));
            Assert.Null(thrown);
            Assert.Equal(0, Environment.ExitCode);
            Assert.True(service.ExecuteTask?.IsCanceled);
            Assert.False(service.ExecuteTask?.IsFaulted);
        }
        finally
        {
            if (runTask is not null && lifetime is not null)
                await StopIfRunningAsync(runTask, lifetime);
            Environment.ExitCode = priorExitCode;
        }
    }

    [Fact]
    public async Task RunHostAndSetExitCodeAsync_RestoreRestart_PreservesCode86()
    {
        var priorExitCode = Environment.ExitCode;
        IHostApplicationLifetime? lifetime = null;
        Task? runTask = null;
        try
        {
            Environment.ExitCode = 0;
            var host = CreateControlledHost(out var service, out lifetime, out var restart);

            runTask = Program.RunHostAndSetExitCodeAsync(host);
            await service.Started.Task.WaitAsync(GateTimeout);
            Assert.Equal(RestartUtil.RestartForRestoreExitCode, restart.StopForStagedRestore());

            var thrown = await Record.ExceptionAsync(() => runTask.WaitAsync(GateTimeout));
            Assert.Null(thrown);
            Assert.Equal(RestartUtil.RestartForRestoreExitCode, Environment.ExitCode);
            Assert.False(service.ExecuteTask?.IsFaulted);
        }
        finally
        {
            if (runTask is not null && lifetime is not null)
                await StopIfRunningAsync(runTask, lifetime);
            Environment.ExitCode = priorExitCode;
        }
    }

    [Fact]
    public void ProcessExitCoordinator_RestoreThenRuntimeFault_EndsAtOne()
    {
        var priorExitCode = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            var coordinator = new ProcessExitCoordinator();
            var restart = new RestartService(new NoopHostApplicationLifetime(), coordinator);

            Assert.Equal(RestartUtil.RestartForRestoreExitCode, restart.StopForStagedRestore());
            Assert.Equal(RestartUtil.RestartForRestoreExitCode, Environment.ExitCode);

            Assert.Equal(1, coordinator.ReportBackgroundServiceFault());
            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = priorExitCode;
        }
    }

    [Fact]
    public void ProcessExitCoordinator_RuntimeFaultThenRestore_StaysAtOne()
    {
        var priorExitCode = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            var coordinator = new ProcessExitCoordinator();
            var restart = new RestartService(new NoopHostApplicationLifetime(), coordinator);

            Assert.Equal(1, coordinator.ReportBackgroundServiceFault());
            Assert.Equal(1, Environment.ExitCode);

            Assert.Equal(1, restart.StopForStagedRestore());
            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = priorExitCode;
        }
    }

    [Fact]
    public async Task RunHostAndSetExitCodeAsync_StartupFailure_StillPropagates()
    {
        var priorExitCode = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            var expected = new InvalidOperationException("deterministic startup failure");

            var builder = CreateMinimalHostBuilder();
            builder.Services.AddSingleton<ProcessExitCoordinator>();
            builder.Services.AddSingleton<IHostedService>(_ => new StartFailingHostedService(expected));
            var host = builder.Build();

            var thrown = await Record.ExceptionAsync(
                () => Program.RunHostAndSetExitCodeAsync(host).WaitAsync(GateTimeout));

            Assert.Same(expected, thrown);
            Assert.Equal(0, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = priorExitCode;
        }
    }

    [Fact]
    public async Task RunHostAndSetExitCodeAsync_HostedServiceFactoryFailure_DisposesAndPropagates()
    {
        var priorExitCode = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            DisposableSentinel? sentinel = null;
            var expected = new InvalidOperationException(
                "deterministic hosted-service factory failure");

            var builder = CreateMinimalHostBuilder();
            builder.Services.AddSingleton<ProcessExitCoordinator>();
            builder.Services.AddSingleton<DisposableSentinel>(
                _ => sentinel = new DisposableSentinel());
            builder.Services.AddSingleton<IHostedService>(serviceProvider =>
            {
                _ = serviceProvider.GetRequiredService<DisposableSentinel>();
                throw expected;
            });

            var host = builder.Build();
            var thrown = await Record.ExceptionAsync(
                () => Program.RunHostAndSetExitCodeAsync(host).WaitAsync(GateTimeout));

            Assert.Same(expected, thrown);
            Assert.NotNull(sentinel);
            Assert.True(sentinel.IsDisposed);
        }
        finally
        {
            Environment.ExitCode = priorExitCode;
        }
    }

    [Fact]
    public async Task RunHostAndSetExitCodeAsync_ShutdownFailure_DisposesAndPropagates()
    {
        var priorExitCode = Environment.ExitCode;
        IHostApplicationLifetime? lifetime = null;
        Task? runTask = null;
        try
        {
            Environment.ExitCode = 0;
            DisposableSentinel? sentinel = null;
            var expected = new InvalidOperationException("deterministic shutdown failure");

            var builder = CreateMinimalHostBuilder();
            builder.Services.AddSingleton<ProcessExitCoordinator>();
            builder.Services.AddSingleton<DisposableSentinel>(
                _ => sentinel = new DisposableSentinel());
            builder.Services.AddSingleton<IHostedService>(serviceProvider =>
            {
                _ = serviceProvider.GetRequiredService<DisposableSentinel>();
                return new StopFailingHostedService(expected);
            });

            var host = builder.Build();
            lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            runTask = Program.RunHostAndSetExitCodeAsync(host);
            await WaitUntilStartedAsync(lifetime);
            lifetime.StopApplication();

            var thrown = await Record.ExceptionAsync(() => runTask.WaitAsync(GateTimeout));
            Assert.Same(expected, thrown);
            Assert.NotNull(sentinel);
            Assert.True(sentinel.IsDisposed);
        }
        finally
        {
            if (runTask is not null && lifetime is not null)
                await StopIfRunningAsync(runTask, lifetime);
            Environment.ExitCode = priorExitCode;
        }
    }

    private static IHost CreateControlledHost(
        out ControlledBackgroundService service,
        out IHostApplicationLifetime lifetime,
        out RestartService restart)
    {
        var builder = CreateMinimalHostBuilder();
        builder.Services.AddSingleton<ProcessExitCoordinator>();
        builder.Services.AddSingleton<RestartService>();
        builder.Services.AddSingleton<ControlledBackgroundService>();
        builder.Services.AddHostedService(sp =>
            sp.GetRequiredService<ControlledBackgroundService>());

        var host = builder.Build();
        service = host.Services.GetRequiredService<ControlledBackgroundService>();
        lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        restart = host.Services.GetRequiredService<RestartService>();
        return host;
    }

    private static HostApplicationBuilder CreateMinimalHostBuilder()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromSeconds(5);
            options.BackgroundServiceExceptionBehavior =
                BackgroundServiceExceptionBehavior.StopHost;
        });
        return builder;
    }

    private static async Task WaitUntilStartedAsync(IHostApplicationLifetime lifetime)
    {
        if (lifetime.ApplicationStarted.IsCancellationRequested)
            return;

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        if (lifetime.ApplicationStarted.IsCancellationRequested)
            started.TrySetResult();

        await started.Task.WaitAsync(GateTimeout);
    }

    private static async Task StopIfRunningAsync(Task runTask, IHostApplicationLifetime lifetime)
    {
        if (!runTask.IsCompleted)
            lifetime.StopApplication();

        try
        {
            await runTask.WaitAsync(GateTimeout);
        }
        catch (Exception)
        {
            // Tear-down only: the test body already observed success or failure.
        }
    }

    private sealed class ControlledBackgroundService : BackgroundService
    {
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Fail() => _release.TrySetResult(true);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Started.TrySetResult(true);
            await _release.Task.WaitAsync(stoppingToken);
            throw new InvalidOperationException("deterministic hosted-service fault");
        }
    }

    private sealed class StartFailingHostedService(Exception startException) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) =>
            Task.FromException(startException);

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StopFailingHostedService(Exception stopException) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) =>
            Task.FromException(stopException);
    }

    private sealed class DisposableSentinel : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class NoopHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => _stopping.Cancel();
    }
}
