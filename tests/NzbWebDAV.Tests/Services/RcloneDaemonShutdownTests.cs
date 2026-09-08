using System.Net;
using System.Text;
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
        // Program.cs configures HostOptions.ShutdownTimeout to 5 seconds. The
        // unmount and the process stop that follows it both have to complete
        // inside that, or the mount is left behind — which is the bug this path
        // exists to prevent.
        var total = RcloneDaemonService.UnmountTimeout + RcloneProcessLauncher.GracefulShutdownTimeout;

        Assert.True(
            total < TimeSpan.FromSeconds(5),
            $"unmount plus process stop is {total.TotalSeconds}s, which does not fit the host's 5s budget");
    }

    private static RcloneDaemonService Service(FakeLauncher launcher)
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.RcloneBuiltinEnabled, ConfigValue = "true" },
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
            MountReconciler = (_, _) => Task.FromResult(new RcloneReconcileResult([], [], [])),
        };
    }

    public void Dispose()
    {
        RcloneClient.TestHandler = null;
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
