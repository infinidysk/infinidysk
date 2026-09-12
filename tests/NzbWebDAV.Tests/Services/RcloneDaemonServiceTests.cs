using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class RcloneDaemonServiceTests
{
    private static ConfigManager Config(bool enabled, params ConfigItem[] extra)
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.RcloneBuiltinEnabled,
                ConfigValue = enabled ? "true" : "false",
            },
            .. extra,
        ]);
        return config;
    }

    private static RcloneDaemonService Service(
        ConfigManager config,
        FakeRcloneProcessLauncher launcher,
        bool canMount = true,
        List<IRcloneClient>? reconciled = null,
        Func<DateTimeOffset>? utcNow = null,
        IReadOnlyList<string>? mountErrors = null,
        List<string>? swept = null,
        Func<RcloneDaemonOptions, CancellationToken, Task<bool>>? probeRcPort = null,
        IReadOnlyList<MountTableEntry>? mountTable = null,
        List<string>? unmounted = null,
        bool responsive = true,
        Func<Task>? onMountPass = null)
    {
        return new RcloneDaemonService(config, launcher)
        {
            ProbeRcPort = probeRcPort ?? ((_, _) => Task.FromResult(true)),
            ReadinessProbeAttempts = 3,
            ReadinessProbeInterval = TimeSpan.Zero,
            StaleMountCleaner = new RcloneStaleMountCleaner
            {
                // No mount table in a unit test, so the sweep is inert unless a
                // case explicitly records what it was asked about.
                ReadMountTable = () =>
                {
                    swept?.Add("sweep");
                    return mountTable ?? [];
                },
                IsResponsive = _ => responsive,
                Unmount = path =>
                {
                    unmounted?.Add(path);
                    return true;
                },
            },
            CapabilityCheck = new RcloneCapabilityCheck
            {
                FileExists = _ => canMount,
                ExecutableOnPath = _ => canMount,
                CanOpenForWrite = _ => canMount,
                ReadFileText = _ => canMount ? "user_allow_other\n" : null,
            },
            PrepareDirectories = _ => { },
            UtcNow = utcNow ?? (() => DateTimeOffset.UnixEpoch),
            MountReconciler = async (client, _) =>
            {
                reconciled?.Add(client);
                if (onMountPass is not null) await onMountPass().ConfigureAwait(false);
                return new RcloneReconcileResult([], [], mountErrors ?? []);
            },
        };
    }

    private static ConfigItem Mounts(string mountPoint, bool enabled = true) => new()
    {
        ConfigName = ConfigKeys.RcloneBuiltinMounts,
        ConfigValue =
            $$"""[{"Id":"library","MountPoint":"{{mountPoint}}","RemotePath":"/","Enabled":{{(enabled ? "true" : "false")}}}]""",
    };

    [Fact]
    public async Task ReconcileAsync_DoesNothing_WhenBuiltinModeIsOff()
    {
        var launcher = new FakeRcloneProcessLauncher();
        var service = Service(Config(enabled: false), launcher);

        await service.ReconcileAsync(CancellationToken.None);

        Assert.Empty(launcher.Started);
        Assert.False(service.GetStatus().Running);
    }

    [Fact]
    public async Task ReconcileAsync_StartsTheDaemon_WhenEnabledAndCapable()
    {
        var launcher = new FakeRcloneProcessLauncher();
        var service = Service(Config(enabled: true), launcher);

        await service.ReconcileAsync(CancellationToken.None);

        var options = Assert.Single(launcher.Started);
        Assert.Equal(ConfigManager.DefaultRcloneBuiltinRcPort, options.RcPort);
        Assert.NotEmpty(options.RcPass);
        Assert.True(service.GetStatus().Running);
    }

    [Fact]
    public async Task ReconcileAsync_IsIdempotent_WhileTheDaemonIsHealthy()
    {
        var launcher = new FakeRcloneProcessLauncher();
        var service = Service(Config(enabled: true), launcher);

        await service.ReconcileAsync(CancellationToken.None);
        await service.ReconcileAsync(CancellationToken.None);

        Assert.Single(launcher.Started);
    }

    [Fact]
    public async Task ReconcileAsync_AppliesTheConfiguredMounts_AfterTheDaemonStarts()
    {
        // Without this the daemon comes up with no mounts after every restart and
        // stays that way until someone opens the settings page and presses Apply.
        var reconciled = new List<IRcloneClient>();
        var launcher = new FakeRcloneProcessLauncher();
        var service = Service(
            Config(enabled: true, Mounts("/mnt/remote/infinidysk")),
            launcher,
            reconciled: reconciled);

        await service.ReconcileAsync(CancellationToken.None);

        Assert.Single(reconciled);
    }

    [Fact]
    public async Task ReconcileAsync_DoesNotReapplyMounts_WhileNothingChanged()
    {
        var reconciled = new List<IRcloneClient>();
        var launcher = new FakeRcloneProcessLauncher();
        var service = Service(
            Config(enabled: true, Mounts("/mnt/remote/infinidysk")),
            launcher,
            reconciled: reconciled);

        await service.ReconcileAsync(CancellationToken.None);
        await service.ReconcileAsync(CancellationToken.None);
        await service.ReconcileAsync(CancellationToken.None);

        Assert.Single(reconciled);
    }

    [Fact]
    public async Task ReconcileAsync_ReappliesMounts_WhenTheMountListChanges()
    {
        var reconciled = new List<IRcloneClient>();
        var launcher = new FakeRcloneProcessLauncher();
        var config = Config(enabled: true, Mounts("/mnt/remote/infinidysk"));
        var service = Service(config, launcher, reconciled: reconciled);

        await service.ReconcileAsync(CancellationToken.None);
        config.UpdateValues([Mounts("/mnt/remote/library")]);
        await service.ReconcileAsync(CancellationToken.None);

        Assert.Equal(2, reconciled.Count);
    }

    [Fact]
    public async Task ReconcileAsync_BacksOffWhenTheMountPassKeepsFailing()
    {
        // A mount point held by something else does not free itself. Retrying
        // every poll interval only fills the log with the same message.
        var now = DateTimeOffset.UnixEpoch;
        var reconciled = new List<IRcloneClient>();
        var launcher = new FakeRcloneProcessLauncher();
        var service = Service(
            Config(enabled: true, Mounts("/data/nzbdav")),
            launcher,
            reconciled: reconciled,
            utcNow: () => now,
            mountErrors: ["Could not mount '/data/nzbdav': something is already mounted there."]);

        await service.ReconcileAsync(CancellationToken.None);
        await service.ReconcileAsync(CancellationToken.None);
        await service.ReconcileAsync(CancellationToken.None);

        Assert.Equal(2, reconciled.Count);

        now += TimeSpan.FromMinutes(10);
        await service.ReconcileAsync(CancellationToken.None);

        Assert.Equal(3, reconciled.Count);
    }

    [Fact]
    public async Task ReconcileAsync_RetriesAFailedMountImmediately_WhenTheSettingsChange()
    {
        // Editing the mount list is how an operator fixes a failing mount, so it
        // must not be held behind the backoff.
        var now = DateTimeOffset.UnixEpoch;
        var reconciled = new List<IRcloneClient>();
        var launcher = new FakeRcloneProcessLauncher();
        var config = Config(enabled: true, Mounts("/data/nzbdav"));
        var service = Service(
            config,
            launcher,
            reconciled: reconciled,
            utcNow: () => now,
            mountErrors: ["Could not mount '/data/nzbdav': something is already mounted there."]);

        await service.ReconcileAsync(CancellationToken.None);
        await service.ReconcileAsync(CancellationToken.None);
        var beforeEdit = reconciled.Count;

        config.UpdateValues([Mounts("/data/nzbdav-fixed")]);
        await service.ReconcileAsync(CancellationToken.None);

        Assert.Equal(beforeEdit + 1, reconciled.Count);
    }

    [Fact]
    public async Task ReconcileAsync_SweepsForStaleMountsBeforeStartingTheDaemon()
    {
        // A mount left by a killed container blocks the new daemon from taking
        // the same path, so it has to be cleared before rclone is asked to mount.
        var swept = new List<string>();
        var launcher = new FakeRcloneProcessLauncher();
        var service = Service(
            Config(enabled: true, Mounts("/data/nzbdav")),
            launcher,
            swept: swept);

        await service.ReconcileAsync(CancellationToken.None);

        Assert.Single(swept);
        Assert.Single(launcher.Started);
    }

    [Fact]
    public async Task ReconcileAsync_DoesNotSweepAgainWhileTheDaemonIsHealthy()
    {
        // Reading the mount table on every poll would be pure overhead.
        var swept = new List<string>();
        var launcher = new FakeRcloneProcessLauncher();
        var service = Service(
            Config(enabled: true, Mounts("/data/nzbdav")),
            launcher,
            swept: swept);

        await service.ReconcileAsync(CancellationToken.None);
        await service.ReconcileAsync(CancellationToken.None);
        await service.ReconcileAsync(CancellationToken.None);

        Assert.Single(swept);
    }

    [Fact]
    public async Task ReconcileAsync_SweepsAgainAfterTheDaemonExits()
    {
        // The exit that just happened may be the one that left a mount behind.
        var swept = new List<string>();
        var launcher = new FakeRcloneProcessLauncher();
        var service = Service(
            Config(enabled: true, Mounts("/data/nzbdav")),
            launcher,
            swept: swept);

        await service.ReconcileAsync(CancellationToken.None);
        launcher.Handles[0].SimulateExit();
        await service.ReconcileAsync(CancellationToken.None);

        Assert.Equal(2, swept.Count);
    }

    [Fact]
    public async Task MountPasses_NeverRunAtTheSameTime()
    {
        // The supervisor's poll and the three admin endpoints all reconcile the
        // same daemon. Two passes at once each read "nothing is mounted" and then
        // both mount the same path: one wins, the other reports "already
        // mounted", and the supervisor records a failed pass it then backs off
        // from.
        using var entered = new SemaphoreSlim(0, 2);
        using var release = new SemaphoreSlim(0, 2);
        var launcher = new FakeRcloneProcessLauncher();
        var service = Service(
            Config(enabled: true, Mounts("/data/nzbdav")),
            launcher,
            onMountPass: async () =>
            {
                entered.Release();
                await release.WaitAsync();
            });

        var supervisorPass = service.ReconcileAsync(CancellationToken.None);
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(5)), "the supervisor's pass never started");

        var adminPass = service.WithMountGateAsync(
            async _ =>
            {
                entered.Release();
                await release.WaitAsync();
                return 0;
            },
            CancellationToken.None);

        var secondStarted = await entered.WaitAsync(TimeSpan.FromMilliseconds(500));

        release.Release(2);
        await Task.WhenAll(supervisorPass, adminPass);

        Assert.False(secondStarted, "a second mount pass ran while the first was still going");
    }

    [Fact]
    public async Task ReconcileAsync_SweepsAStaleMountLeftOnADisabledMountPoint()
    {
        // Disabling a mount does not remove the mount point from the container.
        // A leftover there still blocks the path for whatever the operator points
        // at it next, and it is a path this installation configured, so it is
        // ours to clear. The two safety conditions still gate it: a dead FUSE
        // filesystem, mounted exactly there.
        var unmounted = new List<string>();
        var launcher = new FakeRcloneProcessLauncher();
        var service = Service(
            Config(enabled: true, Mounts("/data/nzbdav", enabled: false)),
            launcher,
            mountTable: [new MountTableEntry("/data/nzbdav", "fuse.rclone")],
            unmounted: unmounted,
            responsive: false);

        await service.ReconcileAsync(CancellationToken.None);

        Assert.Equal(["/data/nzbdav"], unmounted);
    }

    [Fact]
    public async Task ReconcileAsync_SweepsBetweenStoppingAndRestarting_WhenAStartupSettingChanges()
    {
        // The restart is itself an event that can leave a mount behind, and a
        // stale mount can only be told apart from a live one while nothing is
        // running. Starting the replacement in the same pass puts a healthy
        // daemon in front of every later sweep, so a mount left by the old
        // process would never be cleared and the new one would fail with
        // "already mounted" until the container is restarted.
        var events = new List<string>();
        var launcher = new FakeRcloneProcessLauncher { Events = events };
        var config = Config(enabled: true, Mounts("/data/nzbdav"));
        var service = Service(config, launcher, swept: events);

        await service.ReconcileAsync(CancellationToken.None);
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.RcloneBuiltinCacheDir,
                ConfigValue = "/mnt/fast/rclone-cache",
            },
        ]);
        await service.ReconcileAsync(CancellationToken.None);
        await service.ReconcileAsync(CancellationToken.None);

        Assert.Equal(["sweep", "start", "sweep", "start"], events);
    }

    [Fact]
    public async Task ReconcileAsync_RefusesToStart_AndExplainsWhy_WhenFuseIsUnavailable()
    {
        var launcher = new FakeRcloneProcessLauncher();
        var service = Service(Config(enabled: true), launcher, canMount: false);

        await service.ReconcileAsync(CancellationToken.None);

        Assert.Empty(launcher.Started);
        var status = service.GetStatus();
        Assert.False(status.Running);
        Assert.NotEmpty(status.BlockingReasons);
    }

    [Fact]
    public async Task ReconcileAsync_RestartsTheDaemon_AfterItExits()
    {
        var launcher = new FakeRcloneProcessLauncher();
        var service = Service(Config(enabled: true), launcher);

        await service.ReconcileAsync(CancellationToken.None);
        launcher.Handles[0].SimulateExit();
        await service.ReconcileAsync(CancellationToken.None);

        Assert.Equal(2, launcher.Started.Count);
    }

    [Fact]
    public async Task ReconcileAsync_BacksOff_WhenTheDaemonKeepsExiting()
    {
        // A daemon that cannot stay up must not be respawned every poll interval
        // for the life of the container.
        var now = DateTimeOffset.UnixEpoch;
        var launcher = new FakeRcloneProcessLauncher();
        var service = Service(Config(enabled: true), launcher, utcNow: () => now);

        await service.ReconcileAsync(CancellationToken.None);
        launcher.Handles[0].SimulateExit();
        await service.ReconcileAsync(CancellationToken.None);
        launcher.Handles[1].SimulateExit();
        await service.ReconcileAsync(CancellationToken.None);

        Assert.Equal(2, launcher.Started.Count);
        Assert.Contains(service.GetStatus().BlockingReasons, r => r.Contains("exited", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReconcileAsync_RestartsAfterTheBackoffElapses()
    {
        var now = DateTimeOffset.UnixEpoch;
        var launcher = new FakeRcloneProcessLauncher();
        var service = Service(Config(enabled: true), launcher, utcNow: () => now);

        await service.ReconcileAsync(CancellationToken.None);
        launcher.Handles[0].SimulateExit();
        await service.ReconcileAsync(CancellationToken.None);
        launcher.Handles[1].SimulateExit();
        await service.ReconcileAsync(CancellationToken.None);

        now += TimeSpan.FromMinutes(10);
        await service.ReconcileAsync(CancellationToken.None);

        Assert.Equal(3, launcher.Started.Count);
    }

    [Fact]
    public async Task ReconcileAsync_ForgetsPastExits_OnceTheDaemonStaysUp()
    {
        // Otherwise a daemon that flaps once in a while accumulates backoff
        // forever, and the tab keeps reporting a failure that has stopped.
        var now = DateTimeOffset.UnixEpoch;
        var launcher = new FakeRcloneProcessLauncher();
        var service = Service(Config(enabled: true), launcher, utcNow: () => now);

        await service.ReconcileAsync(CancellationToken.None);
        launcher.Handles[0].SimulateExit();
        await service.ReconcileAsync(CancellationToken.None);
        launcher.Handles[1].SimulateExit();
        await service.ReconcileAsync(CancellationToken.None);

        // Past the backoff, the daemon starts and then stays up for a pass.
        now += TimeSpan.FromMinutes(10);
        await service.ReconcileAsync(CancellationToken.None);
        await service.ReconcileAsync(CancellationToken.None);

        Assert.Empty(service.GetStatus().BlockingReasons);

        // The next single exit restarts immediately rather than waiting out a
        // backoff inherited from the earlier failures.
        launcher.Handles[2].SimulateExit();
        await service.ReconcileAsync(CancellationToken.None);

        Assert.Equal(4, launcher.Started.Count);
    }

    [Fact]
    public async Task ReconcileAsync_RestartsTheDaemon_WhenAStartupSettingChanges()
    {
        // The cache directory is a process argument, so a saved change does
        // nothing until the daemon is restarted. The restart takes two passes:
        // one stops the old process, the next sweeps for anything it left behind
        // and then starts the replacement.
        var launcher = new FakeRcloneProcessLauncher();
        var config = Config(enabled: true);
        var service = Service(config, launcher);

        await service.ReconcileAsync(CancellationToken.None);
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.RcloneBuiltinCacheDir,
                ConfigValue = "/mnt/fast/rclone-cache",
            },
        ]);
        await service.ReconcileAsync(CancellationToken.None);
        await service.ReconcileAsync(CancellationToken.None);

        Assert.Equal(2, launcher.Started.Count);
        Assert.Equal("/mnt/fast/rclone-cache", launcher.Started[1].CacheDir);
        Assert.True(launcher.Handles[0].Terminated);
    }

    [Fact]
    public async Task ReconcileAsync_BacksOffAndExplains_WhenTheDaemonCannotBeStarted()
    {
        // A cache directory that cannot be created throws before the process
        // exists, so there is no exit to observe. Without the same accounting the
        // supervisor would retry every poll interval and report nothing wrong.
        var now = DateTimeOffset.UnixEpoch;
        var launcher = new FakeRcloneProcessLauncher
        {
            FailWith = new UnauthorizedAccessException("Access to the path '/config/rclone' is denied."),
        };
        var service = Service(Config(enabled: true), launcher, utcNow: () => now);

        await service.ReconcileAsync(CancellationToken.None);
        await service.ReconcileAsync(CancellationToken.None);
        await service.ReconcileAsync(CancellationToken.None);

        Assert.Empty(launcher.Started);
        Assert.False(service.GetStatus().Running);
        Assert.Contains(
            service.GetStatus().BlockingReasons,
            r => r.Contains("could not be started", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReconcileAsync_DoesNotTreatAFailedLaunchAsARunningDaemon()
    {
        // Publishing the options before the process starts would make the next
        // pass believe a daemon was already serving them.
        var launcher = new FakeRcloneProcessLauncher
        {
            FailWith = new UnauthorizedAccessException("denied"),
        };
        var service = Service(Config(enabled: true), launcher);

        await service.ReconcileAsync(CancellationToken.None);
        launcher.FailWith = null;
        await service.ReconcileAsync(CancellationToken.None);

        Assert.Single(launcher.Started);
        Assert.True(service.GetStatus().Running);
        Assert.Empty(service.GetStatus().BlockingReasons);
    }

    [Fact]
    public async Task ReconcileAsync_StopsTheDaemon_WhenBuiltinModeIsTurnedOff()
    {
        var config = Config(enabled: true);
        var launcher = new FakeRcloneProcessLauncher();
        var service = Service(config, launcher);
        await service.ReconcileAsync(CancellationToken.None);

        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.RcloneBuiltinEnabled, ConfigValue = "false" },
        ]);
        await service.ReconcileAsync(CancellationToken.None);

        Assert.True(launcher.Handles[0].Terminated);
        Assert.False(service.GetStatus().Running);
    }

    [Fact]
    public async Task BuiltinClient_IsBoundToTheDaemonsLoopbackAddress()
    {
        var launcher = new FakeRcloneProcessLauncher();
        var service = Service(Config(enabled: true), launcher);

        Assert.Null(service.BuiltinClient);

        await service.ReconcileAsync(CancellationToken.None);

        Assert.NotNull(service.BuiltinClient);
        Assert.Equal(
            $"http://127.0.0.1:{ConfigManager.DefaultRcloneBuiltinRcPort}",
            service.BuiltinClient!.Host);
    }

    [Fact]
    public async Task BuiltinClient_IsDroppedWhenTheDaemonStops()
    {
        var config = Config(enabled: true);
        var launcher = new FakeRcloneProcessLauncher();
        var service = Service(config, launcher);
        await service.ReconcileAsync(CancellationToken.None);

        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.RcloneBuiltinEnabled, ConfigValue = "false" },
        ]);
        await service.ReconcileAsync(CancellationToken.None);

        Assert.Null(service.BuiltinClient);
    }

    [Fact]
    public async Task DaemonOutput_IsKeptForTheUi()
    {
        var launcher = new FakeRcloneProcessLauncher();
        var service = Service(Config(enabled: true), launcher);
        await service.ReconcileAsync(CancellationToken.None);

        launcher.Handles[0].EmitOutput("NOTICE: serving remote control on http://127.0.0.1:5572/");

        Assert.Contains(
            service.GetRecentLogLines(),
            line => line.Contains("serving remote control", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DaemonOutput_NeverRetainsTheGeneratedPassword()
    {
        // The tail is rendered in the admin UI and collected in support packs.
        var launcher = new FakeRcloneProcessLauncher();
        var service = Service(Config(enabled: true), launcher);
        await service.ReconcileAsync(CancellationToken.None);

        var password = launcher.Started[0].RcPass;
        launcher.Handles[0].EmitOutput($"DEBUG: rc auth configured with pass {password}");

        var lines = service.GetRecentLogLines();
        Assert.DoesNotContain(lines, line => line.Contains(password, StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("***", StringComparison.Ordinal));
    }

    [Fact]
    public void RestartBackoff_IsImmediateOnce_ThenGrowsToACap()
    {
        Assert.Equal(TimeSpan.Zero, RcloneDaemonService.RestartBackoff(1));
        Assert.Equal(TimeSpan.FromSeconds(5), RcloneDaemonService.RestartBackoff(2));
        Assert.Equal(TimeSpan.FromSeconds(10), RcloneDaemonService.RestartBackoff(3));
        Assert.Equal(RcloneDaemonService.MaxRestartBackoff, RcloneDaemonService.RestartBackoff(50));
    }

    [Fact]
    public async Task ReconcileAsync_WaitsForTheDaemonToAnswer_BeforeApplyingMounts()
    {
        // rclone binds its remote-control port a moment after the process starts.
        // Mounting in that window fails with "connection refused", which reads in
        // the log like a broken deployment rather than a race with startup.
        var reconciled = new List<IRcloneClient>();
        var probes = 0;
        var launcher = new FakeRcloneProcessLauncher();
        var service = Service(
            Config(enabled: true, Mounts("/mnt/remote/infinidysk")),
            launcher,
            reconciled: reconciled,
            probeRcPort: (_, _) => Task.FromResult(++probes >= 2));

        await service.ReconcileAsync(CancellationToken.None);

        Assert.Equal(2, probes);
        Assert.Single(reconciled);
    }

    [Fact]
    public async Task ReconcileAsync_DefersTheMountPass_WhileTheDaemonIsStillStarting()
    {
        var reconciled = new List<IRcloneClient>();
        var launcher = new FakeRcloneProcessLauncher();
        var service = Service(
            Config(enabled: true, Mounts("/mnt/remote/infinidysk")),
            launcher,
            reconciled: reconciled,
            probeRcPort: (_, _) => Task.FromResult(false));

        await service.ReconcileAsync(CancellationToken.None);

        Assert.Empty(reconciled);
        Assert.True(service.GetStatus().Running);
    }

    [Fact]
    public async Task ReconcileAsync_AppliesTheMountsOnALaterPass_WhenTheDaemonAnswersLate()
    {
        // The deferral must not be recorded as a failed mount pass: that would put
        // the retry behind the failure backoff and leave the library unmounted for
        // longer than the daemon actually took to come up.
        var reconciled = new List<IRcloneClient>();
        var ready = false;
        var launcher = new FakeRcloneProcessLauncher();
        var service = Service(
            Config(enabled: true, Mounts("/mnt/remote/infinidysk")),
            launcher,
            reconciled: reconciled,
            probeRcPort: (_, _) => Task.FromResult(ready));

        await service.ReconcileAsync(CancellationToken.None);
        Assert.Empty(reconciled);

        ready = true;
        await service.ReconcileAsync(CancellationToken.None);

        Assert.Single(reconciled);
        Assert.Single(launcher.Started);
    }

    [Fact]
    public async Task ReconcileAsync_StopsWaiting_WhenTheDaemonExitsWhileStarting()
    {
        // A daemon that dies during the wait cannot be mounted onto, and holding
        // the supervisor there would delay noticing the exit.
        var launcher = new FakeRcloneProcessLauncher();
        var probes = 0;
        var service = Service(
            Config(enabled: true, Mounts("/mnt/remote/infinidysk")),
            launcher,
            probeRcPort: (_, _) =>
            {
                probes++;
                launcher.Handles[0].SimulateExit();
                return Task.FromResult(false);
            });

        await service.ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, probes);
    }

    [Fact]
    public async Task ReconcileAsync_DoesNotWaitAgain_ForADaemonThatIsAlreadyUp()
    {
        // The probe belongs to a fresh start. Repeating it every poll would add a
        // connect to loopback for no reason on a healthy container.
        var probes = 0;
        var launcher = new FakeRcloneProcessLauncher();
        var service = Service(
            Config(enabled: true, Mounts("/mnt/remote/infinidysk")),
            launcher,
            probeRcPort: (_, _) =>
            {
                probes++;
                return Task.FromResult(true);
            });

        await service.ReconcileAsync(CancellationToken.None);
        await service.ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, probes);
    }

    private sealed class FakeRcloneProcessLauncher : IRcloneProcessLauncher
    {
        public List<RcloneDaemonOptions> Started { get; } = [];
        public List<FakeHandle> Handles { get; } = [];
        public Exception? FailWith { get; set; }

        /// <summary>Shared with the sweep recorder when a test needs the order of both.</summary>
        public List<string>? Events { get; init; }

        public IRcloneProcessHandle Start(RcloneDaemonOptions options, Action<string> onOutput)
        {
            if (FailWith is not null) throw FailWith;

            Events?.Add("start");
            Started.Add(options);
            var handle = new FakeHandle(onOutput);
            Handles.Add(handle);
            return handle;
        }
    }

    private sealed class FakeHandle(Action<string> onOutput) : IRcloneProcessHandle
    {
        public bool HasExited { get; private set; }
        public bool Terminated { get; private set; }

        public void SimulateExit() => HasExited = true;
        public void EmitOutput(string line) => onOutput(line);

        public void Terminate()
        {
            Terminated = true;
            HasExited = true;
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task ReconcileAsync_LooksAtTheMountsAgain_OnceTheRecheckIntervalPasses()
    {
        // Watching the process is not supervising its mounts. A mount can go away
        // while rcd stays alive, and with the configuration unchanged nothing
        // would ever look again.
        var now = DateTimeOffset.UnixEpoch;
        var reconciled = new List<IRcloneClient>();
        var launcher = new FakeRcloneProcessLauncher();
        var service = Service(
            Config(enabled: true, Mounts("/mnt/remote/infinidysk")),
            launcher,
            reconciled: reconciled,
            utcNow: () => now);

        await service.ReconcileAsync(CancellationToken.None);
        await service.ReconcileAsync(CancellationToken.None);
        Assert.Single(reconciled);

        now += service.MountRecheckInterval + TimeSpan.FromSeconds(1);
        await service.ReconcileAsync(CancellationToken.None);

        Assert.Equal(2, reconciled.Count);
    }

    [Fact]
    public async Task InvalidateAppliedMounts_MakesTheNextPassReconcileAgain()
    {
        // Remount and clear-cache take the mounts down and put them back outside
        // the supervisor. When the replacement fails there, the supervisor still
        // remembers a successful pass over an unchanged configuration -- and the
        // library stays down until somebody clicks again.
        var reconciled = new List<IRcloneClient>();
        var launcher = new FakeRcloneProcessLauncher();
        var service = Service(
            Config(enabled: true, Mounts("/mnt/remote/infinidysk")),
            launcher,
            reconciled: reconciled);

        await service.ReconcileAsync(CancellationToken.None);
        await service.ReconcileAsync(CancellationToken.None);
        Assert.Single(reconciled);

        service.InvalidateAppliedMounts();
        await service.ReconcileAsync(CancellationToken.None);

        Assert.Equal(2, reconciled.Count);
    }
}
