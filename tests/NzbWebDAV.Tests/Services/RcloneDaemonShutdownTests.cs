using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Services;

/// <summary>
/// A FUSE mount outlives the process that created it, so shutdown has to release
/// the mounts rather than hope rclone reacts to a signal before the host tears
/// the container down. A mount that survives blocks the next start with
/// "directory already mounted".
/// </summary>
[Collection(nameof(RcloneClientCollection))]
public sealed class RcloneDaemonShutdownTests : IDisposable
{
    private readonly RecordingHandler _handler = new();

    public RcloneDaemonShutdownTests()
    {
        RcloneClient.TestHandler = _handler;
    }

    [Fact]
    public async Task StopAsync_ReleasesTheMountsBeforeStoppingTheProcess()
    {
        var launcher = new FakeLauncher();
        var service = Service(launcher);
        await service.ReconcileAsync(CancellationToken.None);

        await service.StopAsync(CancellationToken.None);

        Assert.Contains("/mount/unmountall", _handler.Paths);
    }

    [Fact]
    public async Task StopAsync_StillShutsDown_WhenTheUnmountFails()
    {
        // A wedged daemon must not hold the whole container's shutdown open.
        _handler.FailEverything = true;
        var launcher = new FakeLauncher();
        var service = Service(launcher);
        await service.ReconcileAsync(CancellationToken.None);

        await service.StopAsync(CancellationToken.None);

        Assert.Contains("/mount/unmountall", _handler.Paths);
    }

    [Fact]
    public async Task StopAsync_ReleasesTheMounts_EvenWhenTheShutdownBudgetIsAlreadySpent()
    {
        // Hosted services stop one after another under a single shared budget, so
        // the token this one is handed can already be cancelled by whatever
        // stopped ahead of it. Skipping the unmount then leaves the mount behind,
        // which is the failure this whole path exists to prevent.
        var service = Service(new FakeLauncher());
        await service.ReconcileAsync(CancellationToken.None);

        await service.StopAsync(new CancellationToken(canceled: true));

        Assert.Contains("/mount/unmountall", _handler.Paths);
    }

    [Fact]
    public async Task StopAsync_DoesNothing_WhenNoDaemonWasEverStarted()
    {
        var service = Service(new FakeLauncher());

        await service.StopAsync(CancellationToken.None);

        Assert.Empty(_handler.Paths);
    }

    [Fact]
    public void UnmountBudget_FitsInsideTheHostsShutdownWindow()
    {
        // Read from Program rather than repeated here: the unmount and the
        // process stop that follows it both have to complete inside the host's
        // budget, or the mount is left behind, and a copied number would stop
        // noticing if that budget ever changed.
        var total = RcloneDaemonService.UnmountTimeout + RcloneProcessLauncher.GracefulShutdownTimeout;

        Assert.True(
            total < Program.ShutdownTimeout,
            $"unmount plus process stop is {total.TotalSeconds}s, which does not fit the host's "
            + $"{Program.ShutdownTimeout.TotalSeconds}s budget");
    }

    [Fact]
    public async Task HostShutdown_ReleasesTheMounts_BeforeAServiceRegisteredAfterTheDaemonStops()
    {
        // ASP.NET adds its web host when the app is built, after every service the
        // app registers, so it stops ahead of the daemon. Its stop waits for open
        // requests -- rclone's own reads among them -- on the shared shutdown
        // budget, which can leave nothing for the unmount. The stand-in below
        // takes that place.
        var service = Service(new FakeLauncher());
        await service.ReconcileAsync(CancellationToken.None);

        using var host = new HostBuilder()
            .ConfigureServices(services => services
                .AddHostedService(_ => service)
                .AddHostedService(_ => new StopRecorder(_handler.Paths, "web host stopped")))
            .Build();

        await host.StartAsync();
        await host.StopAsync();

        Assert.Single(_handler.Paths, path => path == "/mount/unmountall");
        Assert.True(
            _handler.Paths.IndexOf("/mount/unmountall") < _handler.Paths.IndexOf("web host stopped"),
            $"order was: {string.Join(", ", _handler.Paths)}");
    }

    [Fact]
    public async Task StoppingAsync_KeepsLaterMountPassesFromPuttingTheMountsBack()
    {
        // The supervisor keeps polling until StopAsync, and an admin request can
        // still arrive. A pass after the release would remount the library and
        // leave exactly the mount this path exists to remove.
        var passes = 0;
        var service = Service(new FakeLauncher(), withMount: true, onMountPass: () => passes++);
        await service.ReconcileAsync(CancellationToken.None);
        Assert.Equal(1, passes);

        await service.StoppingAsync(CancellationToken.None);
        service.InvalidateAppliedMounts();
        await service.ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, passes);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.WithMountGateAsync(_ => Task.FromResult(0), CancellationToken.None));
    }

    [Fact]
    public async Task StoppingAsync_CancelsTheMountPassInFlight_AndReleasesOnceItHasStopped()
    {
        // A pass in flight would otherwise go on mounting after the release and
        // leave that mount behind. Cancelled, it gives the gate back, and only
        // then are the mounts released.
        var armed = false;
        var inPass = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Service(
            new FakeLauncher(),
            withMount: true,
            mountPass: async token =>
            {
                if (!armed) return;
                inPass.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                finally
                {
                    _handler.Paths.Add("pass stopped");
                }
            });
        await service.ReconcileAsync(CancellationToken.None);

        armed = true;
        service.InvalidateAppliedMounts();
        var pass = service.ReconcileAsync(CancellationToken.None);
        await inPass.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await service.StoppingAsync(CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pass);
        Assert.Single(_handler.Paths, path => path == "/mount/unmountall");
        Assert.True(
            _handler.Paths.IndexOf("pass stopped") < _handler.Paths.IndexOf("/mount/unmountall"),
            $"order was: {string.Join(", ", _handler.Paths)}");
    }

    [Fact]
    public async Task StoppingAsync_KeepsTheMounts_WhileAPassThatIgnoresCancellationHoldsTheGate()
    {
        // Unmounting under a pass that still holds the gate could race a mount it
        // has already sent. Shutdown does not wait for it indefinitely, and the
        // release is tried again once that pass has finished.
        var armed = false;
        var inPass = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finish = new SemaphoreSlim(0, 1);
        var service = Service(
            new FakeLauncher(),
            withMount: true,
            mountPass: async _ =>
            {
                if (!armed) return;
                inPass.TrySetResult();
                await finish.WaitAsync();
            },
            shutdownGateTimeout: TimeSpan.FromMilliseconds(100));
        await service.ReconcileAsync(CancellationToken.None);

        armed = true;
        service.InvalidateAppliedMounts();
        var pass = service.ReconcileAsync(CancellationToken.None);
        await inPass.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await service.StoppingAsync(CancellationToken.None);
        Assert.DoesNotContain("/mount/unmountall", _handler.Paths);

        finish.Release();
        await pass;
        await service.StopAsync(CancellationToken.None);

        Assert.Single(_handler.Paths, path => path == "/mount/unmountall");
    }

    private sealed class StopRecorder(List<string> events, string name) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            events.Add(name);
            return Task.CompletedTask;
        }
    }

    private static RcloneDaemonService Service(
        FakeLauncher launcher,
        bool withMount = false,
        Action? onMountPass = null,
        Func<CancellationToken, Task>? mountPass = null,
        TimeSpan? shutdownGateTimeout = null)
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.RcloneBuiltinEnabled, ConfigValue = "true" },
            .. withMount
                ? new[]
                {
                    new ConfigItem
                    {
                        ConfigName = ConfigKeys.RcloneBuiltinMounts,
                        ConfigValue = """[{"Id":"library","MountPoint":"/mnt/remote/infinidysk"}]""",
                    },
                }
                : [],
        ]);

        return new RcloneDaemonService(config, launcher)
        {
            CapabilityCheck = new RcloneCapabilityCheck
            {
                FileExists = _ => true,
                ExecutableOnPath = _ => true,
                CanOpenForWrite = _ => true,
                ReadFileText = _ => "user_allow_other\n",
            },
            PrepareDirectories = _ => { },
            ProbeRcPort = (_, _) => Task.FromResult(true),
            ShutdownGateTimeout = shutdownGateTimeout ?? RcloneDaemonService.UnmountTimeout,
            MountReconciler = async (_, token) =>
            {
                onMountPass?.Invoke();
                if (mountPass is not null) await mountPass(token).ConfigureAwait(false);
                return new RcloneReconcileResult([], [], []);
            },
        };
    }

    public void Dispose()
    {
        RcloneClient.TestHandler = null;

        // Starting the daemon publishes its client process-wide; a later test
        // would otherwise inherit it.
        RcloneClient.Builtin = null;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public bool FailEverything { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // A real transport never puts a request on the wire under a cancelled
            // token, so a double that ignores it cannot tell a call that happened
            // from one that was skipped.
            cancellationToken.ThrowIfCancellationRequested();

            Paths.Add(request.RequestUri!.AbsolutePath);

            var status = FailEverything ? HttpStatusCode.InternalServerError : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FakeLauncher : IRcloneProcessLauncher
    {
        public IRcloneProcessHandle Start(RcloneDaemonOptions options, Action<string> onOutput) =>
            new FakeHandle();
    }

    private sealed class FakeHandle : IRcloneProcessHandle
    {
        public bool HasExited { get; private set; }

        public void Terminate() => HasExited = true;

        public void Dispose()
        {
        }
    }
}
