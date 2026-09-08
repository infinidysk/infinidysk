using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>What the admin UI needs to show about the built-in daemon.</summary>
/// <param name="Enabled">Whether built-in mode is switched on in config.</param>
/// <param name="Running">Whether the daemon process is alive right now.</param>
/// <param name="BaseUrl">Loopback RC address, when running.</param>
/// <param name="BlockingReasons">Why it cannot run, when it cannot.</param>
public sealed record RcloneDaemonStatus(
    bool Enabled,
    bool Running,
    string? BaseUrl,
    IReadOnlyList<string> BlockingReasons);

/// <summary>
/// Owns the lifetime of the built-in <c>rclone rcd</c> process, and keeps the
/// live mounts matching configuration.
///
/// The service is completely dormant unless <c>rclone.builtin.enabled</c> is on:
/// an installation that already runs its own external rclone sees no new process,
/// no new logs, and no behavior change.
///
/// When it is on, the service preflights the container's FUSE capabilities before
/// spawning anything. A container started without <c>--device /dev/fuse</c> cannot
/// mount no matter how correct the configuration is, and saying so plainly is more
/// useful than a crash loop.
/// </summary>
public sealed class RcloneDaemonService(
    ConfigManager configManager,
    IRcloneProcessLauncher launcher) : BackgroundService
{
    /// <summary>How many output lines are kept for the admin UI's log tail.</summary>
    internal const int MaxRetainedLogLines = 200;

    /// <summary>Ceiling on the wait between restart attempts after repeated exits.</summary>
    internal static readonly TimeSpan MaxRestartBackoff = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long shutdown waits for the daemon to release its mounts.
    ///
    /// Sized to fit inside <c>HostOptions.ShutdownTimeout</c> alongside the
    /// process stop that follows it. An unmount over loopback is a millisecond
    /// operation; this budget exists so a wedged daemon cannot hold the whole
    /// container's shutdown.
    /// </summary>
    internal static readonly TimeSpan UnmountTimeout = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();

    /// <summary>
    /// Serializes mount changes. The supervisor's poll and the admin endpoints
    /// all reconcile the same daemon, and a reconcile pass reads what is mounted
    /// before deciding what to mount. Two passes at once both read "nothing is
    /// mounted" and both mount the same path; one wins and the other reports
    /// "already mounted".
    /// </summary>
    private readonly SemaphoreSlim _mountGate = new(1, 1);

    private readonly Queue<string> _logLines = new();
    private IRcloneProcessHandle? _handle;
    private RcloneDaemonOptions? _options;
    private RcloneClient? _builtinClient;
    private IReadOnlyList<string> _blockingReasons = [];
    private int _consecutiveExits;
    private DateTimeOffset _nextStartAttempt = DateTimeOffset.MinValue;
    private string? _appliedMountSignature;
    private bool _lastMountPassFailed;
    private int _consecutiveMountFailures;
    private DateTimeOffset _nextMountAttempt = DateTimeOffset.MinValue;
    private bool _needsStaleSweep = true;

    /// <summary>
    /// A client bound to the built-in daemon, or null when it is not running.
    ///
    /// Deliberately a separate instance from the one configured for the user's
    /// external rclone: built-in mode must not overwrite the host, user, and
    /// password they saved in Settings.
    /// </summary>
    public IRcloneClient? BuiltinClient
    {
        get
        {
            lock (_gate) return _builtinClient;
        }
    }

    /// <summary>Seam for tests; production checks the real container.</summary>
    internal RcloneCapabilityCheck CapabilityCheck { get; init; } = new();

    /// <summary>Seam for tests; production creates the config and cache directories.</summary>
    internal Action<RcloneDaemonOptions> PrepareDirectories { get; init; } = CreateDirectories;

    /// <summary>Seam for tests; production reconciles over the daemon's RC API.</summary>
    internal Func<IRcloneClient, CancellationToken, Task<RcloneReconcileResult>>? MountReconciler { get; init; }

    /// <summary>Seam for tests; production uses the wall clock.</summary>
    internal Func<DateTimeOffset> UtcNow { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>Seam for tests; production sweeps the real mount table.</summary>
    internal RcloneStaleMountCleaner StaleMountCleaner { get; init; } = new();

    /// <summary>How often the supervisor re-checks the daemon.</summary>
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How many times a freshly launched daemon is probed before the mount pass
    /// gives up on this cycle. With the interval below this is a 15 second budget,
    /// which is far more than rclone needs to open a loopback listener.
    /// </summary>
    internal int ReadinessProbeAttempts { get; init; } = 60;

    /// <summary>How long to wait between readiness probes.</summary>
    internal TimeSpan ReadinessProbeInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Seam for tests; production connects to the daemon's loopback RC port.</summary>
    internal Func<RcloneDaemonOptions, CancellationToken, Task<bool>> ProbeRcPort { get; init; } =
        ProbeLoopbackPort;

    public RcloneDaemonStatus GetStatus()
    {
        lock (_gate)
        {
            var running = _handle is { HasExited: false };
            return new RcloneDaemonStatus(
                configManager.IsRcloneBuiltinEnabled(),
                running,
                running ? _options?.BaseUrl : null,
                _blockingReasons);
        }
    }

    public IReadOnlyList<string> GetRecentLogLines()
    {
        lock (_gate)
        {
            return _logLines.ToArray();
        }
    }

    /// <summary>
    /// Releases the mounts before the process is stopped.
    ///
    /// A FUSE mount outlives the process that created it, so leaving this to
    /// rclone's own signal handling is a race the container loses: the host stops
    /// waiting after its shutdown timeout and the mount survives into the next
    /// start, where it blocks the new one with "directory already mounted".
    /// Unmounting over the RC API first makes the outcome independent of how
    /// quickly rclone reacts to a signal.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        RcloneClient? client;
        lock (_gate) client = _builtinClient;

        if (client is not null)
        {
            try
            {
                // Deliberately not linked to the host's shutdown token. Hosted
                // services stop one after another on a single shared budget, so
                // by the time this one runs that token can already be cancelled
                // by whatever stopped ahead of it -- and a cancelled token means
                // the request never reaches rclone, which leaves the mount behind.
                // Overrunning the host's deadline by UnmountTimeout costs far
                // less than a mount that survives into the next start.
                using var budget = new CancellationTokenSource(UnmountTimeout);

                var response = await client.UnmountAll(budget.Token).ConfigureAwait(false);
                if (response.Success)
                    Log.Information("Released the built-in rclone mounts before shutdown.");
                else
                    Log.Warning(
                        "Could not release the built-in rclone mounts before shutdown: {Error}. " +
                        "A mount may need 'fusermount3 -uz' before the next start.",
                        response.Error ?? "unknown error");
            }
            catch (Exception e)
            {
                // Shutdown must continue regardless; the process stop below still
                // gives rclone its own chance to clean up.
                Log.Warning(e, "Could not release the built-in rclone mounts before shutdown.");
            }
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a mount-changing operation with exclusive access to the daemon's
    /// mounts, so it cannot interleave with the supervisor's own pass.
    /// </summary>
    /// <remarks>
    /// Callers must not nest this: the gate is not reentrant.
    /// </remarks>
    public async Task<T> WithMountGateAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await _mountGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mountGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                Log.Warning(e, "The built-in rclone supervisor failed a cycle.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        StopDaemon();
    }

    /// <summary>
    /// Brings the daemon and its mounts into the state the configuration asks for:
    /// started when built-in mode is on and the container can mount, stopped
    /// otherwise, restarted after an unexpected exit or a settings change, and
    /// mounted to match <c>rclone.builtin.mounts</c>. Safe to call repeatedly.
    /// </summary>
    internal async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!configManager.IsRcloneBuiltinEnabled())
        {
            StopDaemon();
            lock (_gate)
            {
                _blockingReasons = [];
                _consecutiveExits = 0;
                _nextStartAttempt = DateTimeOffset.MinValue;
                _appliedMountSignature = null;
                _lastMountPassFailed = false;
                _consecutiveMountFailures = 0;
                _nextMountAttempt = DateTimeOffset.MinValue;
            }

            return;
        }

        // A mount left by a killed container blocks the new daemon from mounting
        // the same path, so it is cleared before anything is started. Done outside
        // the lock: it reads the mount table and may shell out, and the status
        // endpoint should not block behind it.
        SweepStaleMountsIfNeeded();

        IRcloneClient? client;
        bool started;
        RcloneDaemonOptions? options;
        lock (_gate)
        {
            (client, started) = EnsureDaemonStarted();
            options = _options;
        }

        if (client is null) return;

        // A daemon launched moments ago has not bound its remote-control port yet,
        // and every mount call against it fails with "connection refused". Waiting
        // for the port is the difference between mounting now and mounting on some
        // later poll, with a warning in between that reads like a real fault.
        if (started && options is not null
                    && !await WaitForRcPortAsync(options, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await ReconcileMountsAsync(client, started, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for the freshly started daemon to accept a connection on its RC port.
    /// Returns false when the budget runs out or the process died while waiting;
    /// the mounts are then applied by a later pass instead.
    /// </summary>
    private async Task<bool> WaitForRcPortAsync(
        RcloneDaemonOptions options,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= ReadinessProbeAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                // It exited while we were waiting. The exit accounting in the next
                // pass reports that, and there is nothing to mount onto meanwhile.
                if (_handle is null or { HasExited: true }) return false;
            }

            if (await ProbeRcPort(options, cancellationToken).ConfigureAwait(false)) return true;

            if (attempt < ReadinessProbeAttempts)
                await Task.Delay(ReadinessProbeInterval, cancellationToken).ConfigureAwait(false);
        }

        Log.Warning(
            "The built-in rclone daemon did not open its remote-control port on {BaseUrl} within {Seconds}s, " +
            "so its mounts were not applied yet. Check the rclone log below.",
            options.BaseUrl,
            (int)(ReadinessProbeAttempts * ReadinessProbeInterval.TotalSeconds));
        return false;
    }

    /// <summary>
    /// Whether the daemon is accepting connections. A plain TCP connect rather
    /// than an RC call: it answers exactly the question, and it does so without
    /// logging a failed request on every attempt while the port is still closed.
    /// </summary>
    private static async Task<bool> ProbeLoopbackPort(
        RcloneDaemonOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            using var probe = new TcpClient();
            await probe.ConnectAsync(IPAddress.Loopback, options.RcPort, cancellationToken).ConfigureAwait(false);
            return probe.Connected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Clears dead mounts from a previous run, once per start cycle.
    ///
    /// Only mount points this installation has configured are considered, and
    /// only when the kernel says a FUSE filesystem is mounted there and that
    /// mount no longer answers. A mount that still works is reported and left
    /// alone: it belongs to something else, most likely an external rclone the
    /// operator has not stopped yet.
    /// </summary>
    private void SweepStaleMountsIfNeeded()
    {
        lock (_gate)
        {
            // A healthy daemon owns its mounts; there is nothing to clean up and
            // reading the mount table every poll would be pure overhead.
            if (_handle is { HasExited: false }) return;

            // Noticed here rather than waiting for the stop path to run, because
            // the restart happens later in this same pass: an exit that left a
            // mount behind has to be swept before rclone is asked to take that
            // path again.
            if (_handle is { HasExited: true }) _needsStaleSweep = true;

            if (!_needsStaleSweep) return;
            if (UtcNow() < _nextStartAttempt) return;

            _needsStaleSweep = false;
        }

        // Disabled mounts included on purpose. Disabling one does not detach
        // whatever a previous run left on its mount point, and that leftover
        // still blocks the path. The cleaner's own two conditions -- a dead FUSE
        // filesystem, mounted exactly there -- are what keep this safe.
        var mountPoints = configManager.GetRcloneBuiltinMounts()
            .Select(mount => mount.MountPoint)
            .ToList();

        if (mountPoints.Count == 0) return;

        try
        {
            RcloneStaleMountCleaner.Report(StaleMountCleaner.Sweep(mountPoints));
        }
        catch (Exception e)
        {
            // Never block a start on the cleanup: rclone will report the mount
            // conflict itself, with instructions.
            Log.Warning(e, "The stale-mount sweep failed.");
        }
    }

    /// <summary>
    /// Process supervision. Called under <see cref="_gate"/>; returns the client
    /// to reconcile mounts with, or null when nothing is running.
    /// </summary>
    private (IRcloneClient? Client, bool Started) EnsureDaemonStarted()
    {
        var desired = BuildOptions();

        if (_handle is { HasExited: false })
        {
            // The daemon survived to another pass, so whatever made it exit before
            // is over. Leaving the counter set would keep growing the backoff, and
            // leaving the reason set would keep reporting a failure that is no
            // longer happening.
            _consecutiveExits = 0;
            _nextStartAttempt = DateTimeOffset.MinValue;
            _blockingReasons = [];

            // A settings change that only takes effect at process start (the RC
            // port, the config file, the cache directory) needs a restart, or the
            // saved value silently does not apply until the container is bounced.
            if (desired.HasSameSettingsAs(_options)) return (_builtinClient, false);

            Log.Information("Built-in rclone settings changed. Restarting the daemon.");
            StopDaemonLocked();

            // Stop now, start on the next pass. The daemon being stopped here is
            // the one that may leave a mount behind, and the sweep can only tell
            // a stale mount from a live one while nothing is running -- but this
            // pass already swept, before the stop. Starting the replacement now
            // would put a healthy daemon in front of every later sweep, so the
            // leftover would never be cleared. One poll interval is the whole
            // cost.
            return (null, false);
        }
        else if (_handle is not null)
        {
            // A handle that exited on its own is a crash or a manual kill; log it
            // before replacing it so an operator can correlate it with the tail.
            _consecutiveExits++;
            Log.Warning(
                "The built-in rclone daemon exited (consecutive exits: {Count}).",
                _consecutiveExits);
            StopDaemonLocked();

            var backoff = RestartBackoff(_consecutiveExits);
            _nextStartAttempt = UtcNow() + backoff;
            if (backoff > TimeSpan.Zero)
            {
                _blockingReasons =
                [
                    $"The built-in rclone daemon has exited {_consecutiveExits} times in a row. " +
                    $"The next restart is in {(int)backoff.TotalSeconds}s. Check the rclone log below " +
                    "for why it is failing to stay up.",
                ];
            }
        }

        if (UtcNow() < _nextStartAttempt) return (null, false);

        var capability = CapabilityCheck.Evaluate();
        if (!capability.CanMount)
        {
            if (_blockingReasons.Count == 0)
            {
                foreach (var reason in capability.BlockingReasons)
                    Log.Warning("The built-in rclone mount cannot start. {Reason}", reason);
            }

            _blockingReasons = capability.BlockingReasons;
            return (null, false);
        }

        _blockingReasons = [];

        try
        {
            PrepareDirectories(desired);
            _handle = launcher.Start(desired, AppendLogLine);
        }
        catch (Exception e)
        {
            // A cache directory that cannot be created, or a binary that cannot be
            // executed, throws here. Without the same accounting an exit gets, the
            // supervisor would retry every poll interval forever and the tab would
            // show nothing wrong.
            _consecutiveExits++;
            _nextStartAttempt = UtcNow() + RestartBackoff(_consecutiveExits);
            _blockingReasons =
            [
                $"The built-in rclone daemon could not be started: {e.Message}",
            ];

            Log.Warning(e, "The built-in rclone daemon could not be started.");
            return (null, false);
        }

        // Published only once the process is actually up, so a failed launch
        // cannot look like a daemon already serving these settings.
        _options = desired;
        _builtinClient = RcloneClient.ForEndpoint(desired.BaseUrl, desired.RcUser, desired.RcPass);

        // Published for the static cache-invalidation call sites in the database
        // layer, which cannot reach the supervisor any other way.
        RcloneClient.Builtin = _builtinClient;
        return (_builtinClient, true);
    }

    /// <summary>
    /// Applies the configured mounts. Skipped while nothing has changed, so a
    /// healthy daemon is not asked to list its mounts every poll interval.
    /// </summary>
    private async Task ReconcileMountsAsync(
        IRcloneClient client,
        bool daemonJustStarted,
        CancellationToken cancellationToken)
    {
        var configured = configManager.GetRcloneBuiltinMounts();
        var signature = MountSignature(configured);

        bool retryingAfterFailure;
        lock (_gate)
        {
            var unchanged = string.Equals(signature, _appliedMountSignature, StringComparison.Ordinal);

            // A changed mount list is always worth another attempt straight away:
            // editing the settings is how an operator fixes a failing mount.
            if (unchanged && _lastMountPassFailed && UtcNow() < _nextMountAttempt) return;

            if (!daemonJustStarted && unchanged && !_lastMountPassFailed) return;

            // Nothing configured and nothing applied yet: no reason to talk to the
            // daemon at all.
            if (configured.Count == 0 && _appliedMountSignature is null && !_lastMountPassFailed) return;

            retryingAfterFailure = unchanged && _lastMountPassFailed;
        }

        var result = await WithMountGateAsync(
                token => RunMountPass(client, token),
                cancellationToken)
            .ConfigureAwait(false);
        var failed = result.Errors.Count > 0;
        int failureCount;

        lock (_gate)
        {
            _appliedMountSignature = signature;
            _lastMountPassFailed = failed;

            if (failed)
            {
                // A mount point held by something else does not free itself, so
                // retrying every poll interval only fills the log. Back off the
                // same way a failing daemon start does.
                _consecutiveMountFailures++;
                _nextMountAttempt = UtcNow() + RestartBackoff(_consecutiveMountFailures);
            }
            else
            {
                _consecutiveMountFailures = 0;
                _nextMountAttempt = DateTimeOffset.MinValue;
            }

            failureCount = _consecutiveMountFailures;
        }

        // Logged once per attempt, and attempts are spaced out, so a mount that
        // cannot succeed does not bury everything else in the log.
        if (failed && !retryingAfterFailure)
        {
            foreach (var error in result.Errors)
                Log.Warning("The built-in rclone mount pass reported a problem. {Error}", error);
        }
        else if (failed)
        {
            Log.Debug(
                "The built-in rclone mount pass is still failing ({Count} attempts). {Error}",
                failureCount,
                result.Errors[0]);
        }
    }

    /// <summary>
    /// Everything about a mount list that requires a reconcile pass when it
    /// changes. Deliberately not a hash: a readable value is easier to reason
    /// about in a debugger than a checksum.
    /// </summary>
    internal static string MountSignature(IReadOnlyList<RcloneMountConfig> mounts) =>
        string.Join(
            "\n",
            mounts.Select(m => string.Join(
                '|',
                m.Id,
                m.Enabled,
                m.MountPoint,
                m.RemotePath,
                m.VfsCacheMode,
                m.AllowOther,
                m.Links,
                m.DirCacheTime,
                m.VfsCacheMaxAge,
                m.VfsCacheMaxSizeBytes?.ToString() ?? "auto",
                m.ReadAheadBytes?.ToString() ?? "default")));

    /// <summary>
    /// How long to wait before restarting after <paramref name="consecutiveExits"/>
    /// failures. The first exit restarts immediately, because the common case is a
    /// one-off; repeated exits back off so a daemon that cannot start does not
    /// respawn every poll interval forever.
    /// </summary>
    internal static TimeSpan RestartBackoff(int consecutiveExits)
    {
        if (consecutiveExits <= 1) return TimeSpan.Zero;

        var seconds = Math.Min(
            MaxRestartBackoff.TotalSeconds,
            5 * Math.Pow(2, Math.Min(consecutiveExits - 2, 10)));
        return TimeSpan.FromSeconds(seconds);
    }

    private RcloneDaemonOptions BuildOptions() => new()
    {
        RcPort = configManager.GetRcloneBuiltinRcPort(),
        ConfigFilePath = Path.Join(DavDatabaseContext.ConfigPath, "rclone", "rclone.conf"),
        CacheDir = configManager.GetRcloneBuiltinCacheDir(),
        RcUser = "infinidysk",
        RcPass = RcloneDaemonOptions.GenerateRcPassword(),
    };

    private Task<RcloneReconcileResult> RunMountPass(
        IRcloneClient client,
        CancellationToken cancellationToken) =>
        MountReconciler is { } custom
            ? custom(client, cancellationToken)
            : RcloneMountReconciler
                .ForBuiltinDaemon(client, configManager)
                .ReconcileAsync(configManager.GetRcloneBuiltinMounts(), cancellationToken);

    private void StopDaemon()
    {
        lock (_gate) StopDaemonLocked();
    }

    /// <summary>Tears down the process and its client. Called under <see cref="_gate"/>.</summary>
    private void StopDaemonLocked()
    {
        if (_handle is not null)
        {
            _handle.Terminate();
            _handle.Dispose();
            _handle = null;
        }

        // Disposed on every teardown, including a restart: leaving the previous
        // client attached would leak its config subscription for the life of the
        // process.
        if (ReferenceEquals(RcloneClient.Builtin, _builtinClient)) RcloneClient.Builtin = null;
        _builtinClient?.Dispose();
        _builtinClient = null;
        _appliedMountSignature = null;

        // The next start gets a fresh look at the mount table: this stop may be
        // the one that leaves a mount behind.
        _needsStaleSweep = true;
    }

    private void AppendLogLine(string line)
    {
        lock (_gate)
        {
            _logLines.Enqueue(Redact(line, _options?.RcPass));
            while (_logLines.Count > MaxRetainedLogLines) _logLines.Dequeue();
        }
    }

    /// <summary>
    /// Keeps the generated RC password out of the retained tail. The credentials
    /// travel in the environment rather than the command line, but rclone echoes
    /// its effective configuration at higher verbosities, and this tail is shown
    /// in the admin UI and collected in support packs.
    /// </summary>
    internal static string Redact(string line, string? secret) =>
        string.IsNullOrEmpty(secret) ? line : line.Replace(secret, "***", StringComparison.Ordinal);

    private static void CreateDirectories(RcloneDaemonOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(options.ConfigFilePath)!);
        Directory.CreateDirectory(options.CacheDir);
    }

    public override void Dispose()
    {
        StopDaemon();
        _mountGate.Dispose();
        base.Dispose();
    }
}
